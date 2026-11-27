using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>Results owed, <see cref="IngestionJobs.EspnResults"/>: box scores for every day still missing them.</summary>
internal sealed class EspnResultsJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    TimeProvider clock) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key) => key == IngestionJobs.EspnResults;

    public ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
        => !menu.Espn
            ? JobSupport.Nothing()
            : JobSupport.Describe(new IngestionJob(
                Key: IngestionJobs.EspnResults,
                Label: "Results owed",
                Description: "Final scores and box scores for every day still missing them.",
                // A results pass is one scoreboard call per sport plus one per finished game,
                // so the estimate is a floor rather than a promise.
                EstimatedRequests: menu.Stats * menu.Sports.Length,
                Available: menu.Stats > 0,
                Unavailable: menu.Stats > 0 ? null : "No stats source registered.",
                Group: JobGroups.CatchUp));

    /// <summary>
    /// Box scores for every day still missing them, not merely for yesterday.
    ///
    /// <para>
    /// This used to fetch a hardcoded <c>UtcNow.AddDays(-1)</c> while the scheduler decided
    /// whether to run it by asking <see cref="DatesAwaitingResultsAsync"/> which days were
    /// actually owed. The two disagreed, and the disagreement was silent: a day whose results
    /// were missed — the host down, ESPN briefly unavailable, a game finishing after the sweep —
    /// kept the trigger permanently true and kept re-fetching yesterday, which already had its
    /// results. The gap never closed and nothing said so.
    /// </para>
    ///
    /// <para>
    /// Walking the owed days is what makes the daily poll self-healing rather than merely
    /// repetitive.
    /// </para>
    /// </summary>
    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var owed = await DatesAwaitingResultsAsync(_options.GamePasses.ResultsAfterStart, ct);

        // Nothing outstanding still means yesterday, so pressing the button by hand does
        // something sensible on a desk that is already up to date.
        if (owed.Count == 0)
            owed = [LeagueClock.Today().AddDays(-1)];

        // A date that has been swept and is still owed is one a sweep does not heal — a game
        // ESPN never lists as final, or one whose event has moved to another date. Walking it
        // every retry re-fetched every box score on that date, tens of thousands of requests
        // across the lookback from one row. Each fruitless sweep doubles the wait before the
        // next, up to a day, and a date that heals leaves the table.
        var due = owed.Where(d => !_sweptDates.TryGetValue(d, out var s) || now >= s.NextTry).ToList();

        var rows = 0;
        var failures = 0;

        foreach (var date in due)
        {
            if (ct.IsCancellationRequested)
                break;

            var outcome = await JobSupport.RunStatsAsync(scopeFactory, _options, IngestionJobs.EspnResults, date, schedulesOnly: false, ct);

            rows += outcome.Rows;
            failures += outcome.Failures;
        }

        var stillOwed = (await DatesAwaitingResultsAsync(_options.GamePasses.ResultsAfterStart, ct)).ToHashSet();

        foreach (var date in due)
        {
            if (!stillOwed.Contains(date))
            {
                _sweptDates.TryRemove(date, out _);
                continue;
            }

            var attempts = _sweptDates.TryGetValue(date, out var s) ? s.Attempts + 1 : 1;
            var wait = TimeSpan.FromTicks(Math.Min(
                _options.GamePasses.ResultsRetry.Ticks * (1L << Math.Min(attempts, 8)),
                TimeSpan.FromDays(1).Ticks));

            _sweptDates[date] = (attempts, clock.GetUtcNow() + wait);
        }

        var deferred = owed.Count - due.Count;

        return new JobOutcome(
            IngestionJobs.EspnResults, failures == 0, rows, failures,
            $"{due.Count} {(due.Count == 1 ? "day" : "days")} swept, {rows:N0} rows"
            + (deferred > 0 ? $", {deferred} still owed and backing off." : "."));
    }

    /// <summary>Dates swept without healing, and when each is worth trying again.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DateOnly, (int Attempts, DateTimeOffset NextTry)> _sweptDates = new();

    /// <summary>The days still owed results (<see cref="IngestionJobs.DatesAwaitingResultsAsync"/>).</summary>
    public async Task<IReadOnlyList<DateOnly>> DatesAwaitingResultsAsync(
        TimeSpan settleAfter, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

        var cutoff = clock.GetUtcNow() - settleAfter;
        var sports = _options.EffectiveSports;

        var statsOwed = IngestionJobs.StatsOwed(db, sports);

        var starts = await db.Games
            .Where(g => g.StartsAt <= cutoff
                        && g.StartsAt >= cutoff - _options.GamePasses.ResultsLookback
                        && g.Status != GameStatus.Postponed
                        && (g.Status != GameStatus.Final || statsOwed.Any(o => o.Id == g.Id)))
            .Select(g => g.StartsAt)
            .ToListAsync(ct);

        return starts
            .Select(LeagueClock.DateOf)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
    }
}
