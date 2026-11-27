using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>Games in play, <see cref="IngestionJobs.EspnLive"/>: the scoreboard for each league-day with a game on.</summary>
internal sealed class EspnLiveJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    TimeProvider clock) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key) => key == IngestionJobs.EspnLive;

    public async ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
    {
        if (!menu.Espn)
            return JobSupport.None;

        return
        [
            new IngestionJob(
                Key: IngestionJobs.EspnLive,
                Label: "Games in play",
                Description: "Live scores and status for games under way, and the final for any that have just finished. "
                             + "Nothing is fetched when no game is on.",
                EstimatedRequests: menu.Stats * (await LeagueDaysInPlayAsync(
                    menu.Services.GetRequiredService<XLinesDbContext>(), clock.GetUtcNow(), ct)).Count,
                Available: menu.Stats > 0,
                Unavailable: menu.Stats > 0 ? null : "No stats source registered.",
                Group: JobGroups.Today)
        ];
    }

    /// <summary>The league-days with a game in play, each one scoreboard request per stats source.</summary>
    private async Task<IReadOnlyList<(string Sport, DateOnly Day)>> LeagueDaysInPlayAsync(
        XLinesDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var sports = _options.EffectiveSports;

        var inPlay = await IngestionJobs.InPlay(db, now, sports)
            .Select(g => new { Sport = g.Sport!.Key, g.StartsAt })
            .ToListAsync(ct);

        // The configured spelling, so the key handed to the adapter is the one config named.
        return inPlay
            .Select(g => (Sport: sports.First(s => string.Equals(s, g.Sport, StringComparison.OrdinalIgnoreCase)),
                          Day: LeagueClock.DateOf(g.StartsAt)))
            .Distinct()
            .OrderBy(x => x.Sport, StringComparer.Ordinal)
            .ThenBy(x => x.Day)
            .ToList();
    }

    /// <summary>
    /// The scoreboard for each league-day with a game in play, written only for the events being
    /// played or just finished (<see cref="StatsIngestionService.IngestLiveAsync"/>).
    ///
    /// <para>
    /// Only the leagues and days that have a game on: a Sunday of NFL with no MLB game live is
    /// one request, not one per league. No team-identity refresh and no MLB spine — both are
    /// the slate's, and neither changes between one pitch and the next. A desk with nothing in
    /// play makes no request at all.
    /// </para>
    /// </summary>
    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        IReadOnlyList<(string Sport, DateOnly Day)> leagueDays;

        await using (var scope = scopeFactory.CreateAsyncScope())
            leagueDays = await LeagueDaysInPlayAsync(
                scope.ServiceProvider.GetRequiredService<XLinesDbContext>(), clock.GetUtcNow(), ct);

        if (leagueDays.Count == 0)
            return new JobOutcome(IngestionJobs.EspnLive, true, 0, 0, "No game in play.");

        var rows = 0;
        var failures = 0;

        foreach (var (sportKey, day) in leagueDays)
        {
            if (ct.IsCancellationRequested)
                break;

            // A scope per league-day, as the slate does per sport: the change tracker stays small.
            await using var scope = scopeFactory.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
            var stats = scope.ServiceProvider.GetRequiredService<StatsIngestionService>();

            foreach (var source in registry.StatsSources)
            {
                var outcome = await stats.IngestLiveAsync(source, sportKey, IngestionJobs.EspnLive, day, ct);
                rows += outcome.RowsIngested;

                if (outcome.Status == RunStatus.Failed)
                    failures++;
            }
        }

        return new JobOutcome(IngestionJobs.EspnLive, failures == 0, rows, failures,
            $"{leagueDays.Count} {(leagueDays.Count == 1 ? "league-day" : "league-days")} in play, {rows:N0} rows.");
    }
}
