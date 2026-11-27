using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>Today's games, <see cref="JobKeys.EspnSlate"/>: the scoreboard, MLB's spine and the injury report.</summary>
internal sealed class EspnSlateJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    ILogger<IngestionJobs> logger,
    TimeProvider clock) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key) => key == JobKeys.EspnSlate;

    public ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
        => !menu.Espn
            ? JobSupport.Nothing()
            : JobSupport.Describe(new IngestionJob(
                Key: JobKeys.EspnSlate,
                Label: "Today's games",
                Description: "Schedules, start times and live scores for every sport in play.",
                EstimatedRequests: menu.Stats * menu.Sports.Length,
                Available: menu.Stats > 0,
                Unavailable: menu.Stats > 0 ? null : "No stats source registered.",
                Group: JobGroups.Today));

    public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct) => RunSlateAsync(ct);

    /// <summary>
    /// Today's scoreboard, and the day of any game still in play from before midnight.
    ///
    /// <para>
    /// A game that starts at 10:10pm Eastern is still being played at half past midnight, and it
    /// is listed on the scoreboard of the day it started, not the day it is now — so a slate
    /// pressed after midnight reads that day too. The live poll used to run this pass; it is
    /// <see cref="EspnLiveJob"/> now, and this one is the pre-game refresh it was built as.
    /// </para>
    /// </summary>
    private async Task<JobOutcome> RunSlateAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        List<DateTimeOffset> inPlay;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            inPlay = await scope.ServiceProvider.GetRequiredService<XLinesDbContext>().Games
                .Where(g => g.StartsAt <= now
                            && g.StartsAt >= now.AddDays(-1)
                            && g.Status != GameStatus.Final
                            && g.Status != GameStatus.Postponed)
                .Select(g => g.StartsAt)
                .ToListAsync(ct);
        }

        var days = inPlay.Select(LeagueClock.DateOf).Append(LeagueClock.Today()).Distinct().Order();

        var rows = 0;
        var failures = 0;

        foreach (var day in days)
        {
            var outcome = await JobSupport.RunStatsAsync(scopeFactory, _options, JobKeys.EspnSlate, day, schedulesOnly: true, ct);
            rows += outcome.Rows;
            failures += outcome.Failures;
        }

        await SyncSpineAsync(ct);
        await SyncInjuriesAsync(ct);

        return new JobOutcome(JobKeys.EspnSlate, failures == 0, rows, failures, null);
    }

    /// <summary>When MLB's schedule was last read. Probable pitchers do not change often, and one
    /// call per half hour is plenty however often the slate is pressed.</summary>
    private DateTimeOffset _spineSyncedAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan SpineInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// MLB's ids, doubleheader numbers and probable pitchers for today and tomorrow — tomorrow's
    /// starters are usually announced the day before. After ESPN's pass, because it only
    /// annotates games ESPN has created. Best effort: a failure here is logged and costs the
    /// annotations, never the slate.
    /// </summary>
    private async Task SyncSpineAsync(CancellationToken ct)
    {
        if (!_options.EffectiveSports.Contains("mlb", StringComparer.OrdinalIgnoreCase)
            || clock.GetUtcNow() - _spineSyncedAt < SpineInterval)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<MlbSpineService>() is not { } spine)
            return;

        _spineSyncedAt = clock.GetUtcNow();
        var today = LeagueClock.Today();

        foreach (var day in new[] { today, today.AddDays(1) })
        {
            try
            {
                var outcome = await spine.SyncAsync(day, ct);
                logger.LogInformation("MLB spine {Date}: {Matched}/{Scheduled} matched, {Probables} with probables",
                    day, outcome.Matched, outcome.Scheduled, outcome.Probables);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "MLB spine {Date} failed; the slate is unaffected", day);
            }
        }
    }

    /// <summary>When the injury report was last read. A club's word on a player changes a few
    /// times a week; one request per half hour is plenty however often the slate is pressed.</summary>
    private DateTimeOffset _injuriesSyncedAt = DateTimeOffset.MinValue;

    private static readonly TimeSpan InjuriesInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The league injury report for each configured league that has one, as its own run
    /// (<see cref="InjuryReportService.JobKey"/>). Best effort: ESPN down costs the statuses,
    /// never the slate.
    /// </summary>
    private async Task SyncInjuriesAsync(CancellationToken ct)
    {
        // The leagues with a report worth reading are the adapter's to say (EspnStatsAdapter.InjuryLeagues):
        // the NFL, whose starter the Board infers from a box score and so needs telling when the
        // club rules him out. MLB names its probable pitchers and a fight's card is its fighters.
        var sports = _options.EffectiveSports.Where(Adapters.EspnStatsAdapter.InjuryLeagues.Contains).ToList();

        if (sports.Count == 0 || clock.GetUtcNow() - _injuriesSyncedAt < InjuriesInterval)
            return;

        foreach (var sport in sports)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var injuries = scope.ServiceProvider.GetRequiredService<InjuryReportService>();
                var outcome = await injuries.RefreshAsync(sport, InjuryReportService.JobKey, ct);

                logger.LogInformation("Injury report {Sport}: {Status}, {Rows} players written",
                    sport, outcome.Status, outcome.RowsIngested);

                // The half hour starts from a report that was read, not from an attempt: a press
                // after a Failed run row asks ESPN again rather than waiting it out.
                if (outcome.Status != RunStatus.Failed)
                    _injuriesSyncedAt = clock.GetUtcNow();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Injury report {Sport} failed; the slate is unaffected", sport);
            }
        }
    }
}
