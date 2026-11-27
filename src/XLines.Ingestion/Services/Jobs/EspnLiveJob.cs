using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>Games in play, <see cref="JobKeys.EspnLive"/>: the scoreboard for each league-day with a game on.</summary>
internal sealed class EspnLiveJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    TimeProvider clock) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    /// <summary>
    /// Games in play: started within the last day and neither final nor postponed here, in a
    /// configured league. The one definition of "a game is on", shared by the live job (which
    /// league-days to fetch) and the scheduler (whether it is due, and how fast to tick).
    ///
    /// <para>
    /// Keyed on start time having passed rather than on <see cref="GameStatus.Live"/>, because a
    /// game that just started is not yet marked live here — finding that out is what the next
    /// poll is for. Bounded to the last day so a stuck fixture cannot pin the fast cadence on for
    /// ever, and to configured leagues because a league nobody polls cannot be refreshed by it.
    /// </para>
    /// </summary>
    public static IQueryable<Game> InPlay(XLinesDbContext db, DateTimeOffset now, IReadOnlyList<string> sports)
        => db.Games.Where(g => g.StartsAt <= now
                               && g.StartsAt >= now.AddDays(-1)
                               && g.Status != GameStatus.Final
                               && g.Status != GameStatus.Postponed
                               && sports.Contains(g.Sport!.Key));

    public bool Matches(string key) => key == JobKeys.EspnLive;

    public async ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
    {
        if (!menu.Espn)
            return JobSupport.None;

        return
        [
            new IngestionJob(
                Key: JobKeys.EspnLive,
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

        var inPlay = await EspnLiveJob.InPlay(db, now, sports)
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
            return new JobOutcome(JobKeys.EspnLive, true, 0, 0, "No game in play.");

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
                var outcome = await stats.IngestLiveAsync(source, sportKey, JobKeys.EspnLive, day, ct);
                rows += outcome.RowsIngested;

                if (outcome.Status == RunStatus.Failed)
                    failures++;
            }
        }

        return new JobOutcome(JobKeys.EspnLive, failures == 0, rows, failures,
            $"{leagueDays.Count} {(leagueDays.Count == 1 ? "league-day" : "league-days")} in play, {rows:N0} rows.");
    }
}
