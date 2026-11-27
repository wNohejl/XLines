using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services;

/// <summary>
/// The pulls this platform can make, named individually.
///
/// <para>
/// There used to be one button called "Ingest now". It ran every odds source across every
/// sport and nothing else — no schedules, no results — so the one manual control on the desk
/// could not fetch the thing an operator most often wants, and gave no clue what it was about
/// to spend. "Pointless" was a fair description.
/// </para>
///
/// <para>
/// A job here is a unit of work an operator would actually ask for: get today's games, get the
/// results, get fresh lines. Each declares what it costs before it runs, so the menu can show
/// the price and the budget guard is a backstop rather than the only guard.
/// </para>
///
/// <para>
/// The scheduler runs these same jobs. That is the point of the catalog rather than a pile of
/// handlers: automatic and manual paths cannot drift, and a job fixed for one is fixed for both.
/// </para>
/// </summary>
public class IngestionJobs
{
    public const string EspnSlate = "espn:slate";

    /// <summary>
    /// The scoreboard for games being played (spec 2026-10-24 §5.2). Its own job rather than the
    /// slate run more often: the slate resolves every event on the day, refreshes every team's
    /// identity and syncs MLB's spine, which is right twice an hour and wasteful every fifteen
    /// seconds.
    /// </summary>
    public const string EspnLive = "espn:live";

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

    public const string EspnResults = "espn:results";

    /// <summary>
    /// A game in play's box score as it stands, written by the gamecast feed every
    /// <see cref="GamecastOptions.BoxPoll"/> (spec 2026-10-24 §6.1). Its own job, apart from the
    /// feed's final box (<see cref="GamecastFeed.JobKey"/>), because a line from it is not a
    /// final's stats: a final holding nothing newer is still owed (<see cref="StatsOwed"/>), or a
    /// final box that never came would leave the last mid-game box standing for good. The
    /// reliability layer's data rule names the same key (<c>DataQuality.LiveBoxJobKey</c>).
    /// </summary>
    public const string EspnGamecastLive = "espn:gamecast:live";

    /// <summary>
    /// Plays, drives and probabilities for finished games that have none — the games that ended
    /// before the feed existed — a batch a run, newest first (<see cref="GamecastBackport"/>).
    /// </summary>
    public const string EspnGamecastBackport = "espn:gamecast:backport";

    /// <summary>
    /// A league's games over a range of days the operator names, with each final's box score and
    /// its close (<see cref="RangePull"/>): history the backfill seasons do not reach, or a range
    /// that came in incomplete. Not in the catalogue the menu lists, since it cannot run without a
    /// league and dates; the desk asks for them in a sheet, the Worker as <c>--sport --from --to</c>.
    /// </summary>
    public const string EspnRange = "espn:range";
    public const string OddsLines = "odds:lines";
    public const string Settle = "settle";

    /// <summary>
    /// The default stats source's schedule (ADR 0026): The Odds API's <c>/events</c> for every league
    /// it schedules, eight days ahead. Free — the call does not count against the quota — so it runs
    /// on the schedule as ESPN's slate does. Never scores: those are <see cref="OddsScores"/>.
    /// </summary>
    public const string OddsFixtures = "odds:fixtures";

    /// <summary>
    /// The default stats source's final scores, automatically: each league owed one, at most one
    /// metered call a league a day, and only when the operator chose automatic scanning
    /// (<see cref="ScoresDueAsync"/>). Two credits a league a call.
    /// </summary>
    public const string OddsScores = "odds:scores";

    /// <summary>One league's scores on request, e.g. <c>odds:scores:nfl</c>: two credits, stated before the press.</summary>
    public const string OddsScoresPrefix = "odds:scores:";

    /// <summary>
    /// The closing lines a finished game is still owed, asked of ESPN's odds endpoint one game at
    /// a time. The backport for a season walked while the summary carried no odds block.
    /// </summary>
    public const string EspnClosingLines = "espn:closing-lines";


    /// <summary>
    /// Fighter profiles owed (ADR 0021): fighters on a card ahead, and fighters who have fought
    /// since theirs was taken. Four requests a fighter, against ESPN's athlete pages.
    /// </summary>
    public const string EspnFighters = "espn:fighters";


    /// <summary>Requests one fighter's profile costs: athlete page, stat sheets, career rates, record.</summary>
    public const int RequestsPerFighter = 4;

    /// <summary>
    /// Prefix for a lines pull narrowed to one sport, e.g. <c>odds:lines:mlb</c>.
    ///
    /// A credit-billed provider charges per sport per call, so "fetch lines" is not one price —
    /// it is one price per league in season. Naming the sport in the job is what lets the menu
    /// quote a real cost and lets an operator spend on the league they are actually working.
    /// </summary>
    public const string OddsLinesPrefix = "odds:lines:";

    /// <summary>
    /// One league's prices from the second source, SportsGameOdds (ADR 0033), e.g.
    /// <c>sgo:lines:nfl</c>: the books The Odds API's free plan lacks (Caesars). Its own job, never
    /// part of <see cref="OddsLinesPrefix"/>'s loop over the odds sources and never run by the
    /// scheduler, because it spends a different allowance on a different key — objects, one per
    /// game returned — and is pulled only when someone presses it with the price in front of them.
    /// </summary>
    public const string SgoLinesPrefix = "sgo:lines:";

    /// <summary>
    /// The league an on-call drill should exercise: the one actually in play.
    ///
    /// <para>
    /// A drill with no fault injected performs a real pull against a metered provider, so the
    /// rows it buys should be rows somebody is about to read. Naming a league in the code
    /// instead meant the drill bought fixtures weeks away while the slate on screen went
    /// unpriced, and the volume it wrote was measured against pulls for a different sport.
    /// </para>
    ///
    /// <para>
    /// A league nobody configured is never chosen however busy it looks, which is the rule
    /// <see cref="OddsLinesJob"/> already applies to a named sport: quietly scanning a league
    /// the operator did not ask for is what a credit budget cannot absorb.
    /// </para>
    /// </summary>
    /// <param name="upcomingBySport">Games inside the movement window, keyed by sport slug.</param>
    /// <param name="configured">The configured leagues, in preference order.</param>
    /// <returns>The league to drill, or null when none is configured.</returns>
    public static string? DrillSport(
        IReadOnlyDictionary<string, int> upcomingBySport, IReadOnlyList<string> configured)
    {
        if (configured.Count == 0)
            return null;

        var busiest = upcomingBySport
            .Where(kv => kv.Value > 0
                         && configured.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        // Match the configured spelling rather than whatever case the games table holds, so the
        // key handed to an adapter is the one config named.
        return busiest is null
            ? configured[0]
            : configured.First(s => string.Equals(s, busiest, StringComparison.OrdinalIgnoreCase));
    }

    private readonly IReadOnlyList<IIngestionJob> _handlers;
    private readonly OddsLinesJob _lines;
    private readonly EspnResultsJob _results;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IngestionOptions _options;
    private readonly ILogger<IngestionJobs> logger;
    private readonly TimeProvider clock;

    /// <summary>
    /// The catalogue over its handlers (<see cref="IIngestionJob"/>), in the order the menu lists
    /// them. Registered by <see cref="IngestionServiceCollectionExtensions.AddIngestionJobs"/>.
    /// </summary>
    internal IngestionJobs(
        IEnumerable<IIngestionJob> handlers,
        IServiceScopeFactory scopeFactory,
        IOptions<IngestionOptions> options,
        ILogger<IngestionJobs> logger,
        TimeProvider clock)
    {
        _handlers = handlers.ToList();
        _lines = _handlers.OfType<OddsLinesJob>().Single();
        _results = _handlers.OfType<EspnResultsJob>().Single();
        this.scopeFactory = scopeFactory;
        _options = options.Value;
        this.logger = logger;
        this.clock = clock;
    }

    /// <summary>The one handler that runs <paramref name="key"/>, or null where none does.</summary>
    internal IIngestionJob? HandlerFor(string key) => _handlers.FirstOrDefault(h => h.Matches(key));

    /// <summary>Every handler, in menu order.</summary>
    internal IReadOnlyList<IIngestionJob> Handlers => _handlers;

    /// <summary>
    /// Every job, with what it would cost right now and whether it can run.
    ///
    /// Availability is computed from the registered adapters rather than configuration, so a
    /// provider that is configured but not registered — the usual "enabled with no key" case —
    /// reads as unavailable with a reason instead of failing when pressed.
    /// </summary>
    public async Task<IReadOnlyList<IngestionJob>> DescribeAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();

        var sports = _options.EffectiveSports;

        // ESPN's jobs are served by ESPN alone: the default source (ADR 0026) is a stats source too,
        // but it has no scoreboard, box score or fighter to serve them with.
        var stats = registry.Espn is null ? 0 : 1;
        var closers = registry.StatsSources.OfType<Core.Contracts.IClosingLineSource>().Count();

        // The Odds API's month, for every pull that spends it: its price and what is left are stated
        // before the press, a pull the guard would refuse is not offered, and a large one asks first.
        var allowance = await JobSupport.ReadAllowanceAsync(scope.ServiceProvider, ct);
        int? left = allowance is { Reported: true } ? allowance.Remaining : null;
        var resets = allowance?.ResetsOnText ?? "the 1st";

        // ESPN's pulls only where ESPN is turned on (EspnOptIn). Off, the default source's two jobs
        // stand in their place — fixtures, free; scores, metered — and the menu offers nothing that
        // could only be refused.
        var espn = _options.Espn.Enabled || registry.Espn is not null
                   || scope.ServiceProvider.GetService<Adapters.IGamecastSource>() is not null;

        var menu = new JobMenu(scope.ServiceProvider, registry, sports, espn, stats, closers, allowance, left, resets);
        var jobs = new List<IngestionJob>();

        // The handlers are registered in the order the menu draws them: ESPN's (or the default
        // source's), the lines, the second source's lines, then housekeeping.
        foreach (var handler in _handlers)
            jobs.AddRange(await handler.DescribeAsync(menu, ct));

        return jobs;
    }

    /// <summary>
    /// One league's lines pull, <c>odds:lines:{sport}</c>, exactly as <see cref="DescribeAsync"/>
    /// lists it, or null where the menu lists none (a league not configured, or not spelled as
    /// configured, since the menu's keys are matched ordinally).
    ///
    /// <para>
    /// A Board row press asks for this one entry, and building the whole menu for it ran some
    /// thirty queries — games in play, closes owed, plays owed, fighters owed — that the entry
    /// never reads. This reads only what it does: the registry and the month's allowance. Both
    /// paths build the entry through <see cref="OddsLinesJob.LinesJob"/>, so they cannot disagree.
    /// </para>
    /// </summary>
    public async Task<IngestionJob?> DescribeLinesAsync(string sportKey, CancellationToken ct = default)
    {
        var sport = _options.EffectiveSports.FirstOrDefault(s => string.Equals(s, sportKey, StringComparison.Ordinal));
        if (sport is null)
            return null;

        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var allowance = await JobSupport.ReadAllowanceAsync(scope.ServiceProvider, ct);

        return _lines.LinesJob(sport, registry, allowance);
    }

    /// <summary>
    /// What a metered pull with no key says as its reason and in its tooltip: where the key goes,
    /// the desk first and configuration second. Never "no source registered" — a person reads it.
    /// </summary>
    public const string NoOddsKey =
        "Needs a The Odds API key: enter yours under Ops → Odds feeds (or set THE_ODDS_API_KEY in .env).";

    /// <summary>Where the second source's key goes, in the words a refusal uses (ADR 0033).</summary>
    public const string NoSportsGameOddsKey =
        "Needs your own free SportsGameOdds key: set SPORTSGAMEODDS_API_KEY in .env, or Ingestion:SportsGameOdds:ApiKey for a host run.";

    /// <summary>Whether anything serves ESPN's jobs: a stats source or a gamecast source.</summary>
    private async Task<bool> HasEspnAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return scope.ServiceProvider.GetService<SourceRegistry>() is not { } registry
               || registry.Espn is not null
               || scope.ServiceProvider.GetService<Adapters.IGamecastSource>() is not null;
    }

    /// <summary>Runs one job by key. Unknown keys are refused rather than silently ignored.</summary>
    public Task<JobOutcome> RunAsync(string key, CancellationToken ct = default)
        => RunAsync(key, closingScanDay: null, ct);

    /// <summary>
    /// One league's line scan, <c>odds:lines:{sport}</c>, taken as its closing scan: the same job
    /// a press runs, with its run rows stamped with the league day the scan counts against, which
    /// is where the daily cap is counted from (<see cref="LinePollPlanner.ClosingScansDueAsync"/>).
    /// </summary>
    public Task<JobOutcome> RunClosingScanAsync(string sport, DateOnly leagueDay, CancellationToken ct = default)
        => RunAsync(OddsLinesPrefix + sport, leagueDay, ct);

    /// <summary>
    /// One league's line scan as the scheduler takes it, paced (<paramref name="closingScanDay"/>
    /// null) or as its closing scan: the same job a press runs, but only when the operator chose
    /// automatic scanning and only while the month affords it above the reserve held for pulls on
    /// request (<see cref="AllowanceState.AffordsUnattended"/>), the rule the scores obey. Held
    /// back, it writes no run and makes no request. A press goes through <see cref="RunAsync(string, CancellationToken)"/>
    /// and is unaffected: its price was stated first.
    /// </summary>
    public async Task<JobOutcome> RunUnattendedLinesAsync(string sport, DateOnly? closingScanDay = null, CancellationToken ct = default)
    {
        var jobKey = OddsLinesPrefix + sport;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

            if (!await db.LinesRunUnattendedAsync(_options.LinePolling.RunsUnattended, ct))
                return new JobOutcome(jobKey, true, 0, 0, "Lines are pulled on request only; nothing was spent.");

            var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
            var billsCredits = registry.FindOdds(Adapters.TheOddsApiAdapter.SourceKey) is not null;

            if (billsCredits && await JobSupport.ReadAllowanceAsync(scope.ServiceProvider, ct) is { } allowance)
            {
                var cost = _lines.LineCredits(sport, registry);

                if (!allowance.AffordsUnattended(cost))
                {
                    logger.LogInformation(
                        "{Job} held back: {Cost} credits would leave {After} of {Budget}, under the {Reserve} kept for pulls on request",
                        jobKey, cost, allowance.Remaining - cost, allowance.Budget, allowance.Reserve);

                    return new JobOutcome(jobKey, true, 0, 0,
                        $"Held back for {sport.ToUpperInvariant()}: the scan would dip into the credits kept for pulls on request.");
                }
            }
        }

        return await RunAsync(jobKey, closingScanDay, ct);
    }

    /// <summary>
    /// The bookkeeping every run shares, whichever handler does the work: ESPN's jobs refused by
    /// name where ESPN is off, the one log line with rows, failures and time, and the catch that
    /// turns a throw into a failed outcome. A key no handler matches is refused as unknown.
    /// </summary>
    private async Task<JobOutcome> RunAsync(string key, DateOnly? closingScanDay, CancellationToken ct)
    {
        var started = clock.GetUtcNow();

        // ESPN is opt-in (EspnOptIn). Off, nothing serves its jobs, and they are refused by name,
        // saying how to turn it on, rather than reported as succeeding with nothing to read from.
        // Asked of the registry, as DescribeAsync is: what is registered is what can run.
        if (EspnOptIn.IsEspnJob(key) && key != EspnRange && !await HasEspnAsync())
            return new JobOutcome(key, false, 0, 0, EspnOptIn.Refusal(key));

        try
        {
            var outcome = HandlerFor(key) is { } handler
                ? await handler.RunAsync(new JobRun(key, closingScanDay), ct)
                : JobSupport.Unknown(key);

            logger.LogInformation("Job {Job}: {Rows} rows, {Failures} failures in {Elapsed:N0}ms",
                key, outcome.Rows, outcome.Failures, (clock.GetUtcNow() - started).TotalMilliseconds);

            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {Job} failed", key);
            return new JobOutcome(key, false, 0, 1, RunRecord.Describe(ex));
        }
    }

    /// <summary>
    /// "0 closing lines written; 0 of 3 games asked got a close; 520 set aside until their retry date."
    /// </summary>
    internal static string ClosingLinesSentence(int rows, int asked, int closed, OwedCloses after)
        => EspnClosingLinesJob.Sentence(rows, asked, closed, after);

    /// <summary>
    /// Finals without the close they are owed; how many of them a press would ask now (within a
    /// run's ceiling); and how many are set aside until their retry date.
    /// </summary>
    internal readonly record struct OwedCloses(int WithoutClose, int ToAsk, int SetAside = 0);

    /// <summary>
    /// Per league, the games a scores call could finish now — started at least
    /// <paramref name="afterStart"/> ago, within the provider's reach, and neither final nor postponed;
    /// the games still being played; and, across leagues, the games beyond the reach that never went
    /// final here, which no call can finish (they are counted for the desk to say so, never asked for).
    /// </summary>
    public static Task<ScoresOwed> ScoresOwedAsync(
        XLinesDbContext db, IReadOnlyList<string> sports, DateTimeOffset now, TimeSpan afterStart, int reachDays,
        CancellationToken ct = default)
        => OddsScoresJob.ScoresOwedAsync(db, sports, now, afterStart, reachDays, ct);

    /// <summary>
    /// The leagues an unattended scores call is due for: owed a final, none of its games still being
    /// played (so one call takes the whole night's slate), and not yet asked on this league day — at
    /// most one call a league a day, manual pulls included.
    /// </summary>
    public static Task<IReadOnlyList<string>> ScoresDueAsync(
        XLinesDbContext db, ScoresOwed owed, DateTimeOffset now, CancellationToken ct = default)
        => OddsScoresJob.ScoresDueAsync(db, owed, now, ct);

    /// <summary>Each league's scores standing, and the games across them no scores call can finish.</summary>
    public sealed record ScoresOwed(IReadOnlyDictionary<string, LeagueScores> Leagues, int Unrecoverable);

    /// <summary>Games a scores call could finish now, and games still being played.</summary>
    public readonly record struct LeagueScores(int Owed, int Playing);

    /// <summary>
    /// Days we are still owed results for: a game that has started and is not final here, or a
    /// final in a sport we ingest that has no box score.
    ///
    /// <para>
    /// The second half is the one that was missing. The live poll reads the scoreboard, which
    /// carries the score and the final status but not the box score; a game it saw go final was
    /// never owed again, so the results sweep never fetched its stats. All fifteen games of
    /// 22 September 2026 sat final without a stat line until this — the data-quality alert
    /// named them and nothing healed them. A final ESPN never gives a box score for is retried on
    /// the sweep's own back-off, not every tick.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<DateOnly>> DatesAwaitingResultsAsync(
        TimeSpan settleAfter, CancellationToken ct = default)
        => _results.DatesAwaitingResultsAsync(settleAfter, ct);

    /// <summary>
    /// Finals in a configured league still owed their stats: no stat line from a run other than
    /// the live box's (<see cref="EspnGamecastLive"/>). A mid-game box is not a final's, so a game
    /// whose final box never came stays owed until the sweep's or the feed's final write lands —
    /// and that write re-stamps every line it touches, unchanged ones included, so it settles the
    /// debt (<see cref="StatsIngestionService"/>). Any window is the caller's.
    /// </summary>
    public static IQueryable<Game> StatsOwed(XLinesDbContext db, IReadOnlyList<string> sports)
        => db.Games.Where(g => g.Status == GameStatus.Final
                               && sports.Contains(g.Sport!.Key)
                               && !db.PlayerGameStats.Any(s => s.GameId == g.Id
                                   && !db.IngestionRuns.Any(r => r.Id == s.IngestionRunId
                                                                 && r.JobKey == EspnGamecastLive)));
}
