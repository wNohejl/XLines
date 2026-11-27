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
public partial class IngestionJobs
{
    // The keys, forwarded from JobKeys for the projects that name them here (the desk, the
    // Worker, the reliability layer and the tests). New code names JobKeys.
    public const string EspnSlate = JobKeys.EspnSlate;
    public const string EspnLive = JobKeys.EspnLive;
    public const string EspnResults = JobKeys.EspnResults;
    public const string EspnGamecastLive = JobKeys.EspnGamecastLive;
    public const string EspnGamecastBackport = JobKeys.EspnGamecastBackport;
    public const string EspnRange = JobKeys.EspnRange;
    public const string EspnClosingLines = JobKeys.EspnClosingLines;
    public const string EspnFighters = JobKeys.EspnFighters;
    public const string OddsLines = JobKeys.OddsLines;
    public const string OddsLinesPrefix = JobKeys.OddsLinesPrefix;
    public const string OddsFixtures = JobKeys.OddsFixtures;
    public const string OddsScores = JobKeys.OddsScores;
    public const string OddsScoresPrefix = JobKeys.OddsScoresPrefix;
    public const string SgoLinesPrefix = JobKeys.SgoLinesPrefix;
    public const string Settle = JobKeys.Settle;

    /// <inheritdoc cref="OddsLinesJob.DrillSport"/>
    public static string? DrillSport(
        IReadOnlyDictionary<string, int> upcomingBySport, IReadOnlyList<string> configured)
        => OddsLinesJob.DrillSport(upcomingBySport, configured);

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

    /// <inheritdoc cref="OddsScoresJob.ScoresOwedAsync"/>
    public static Task<ScoresOwed> ScoresOwedAsync(
        XLinesDbContext db, IReadOnlyList<string> sports, DateTimeOffset now, TimeSpan afterStart, int reachDays,
        CancellationToken ct = default)
        => OddsScoresJob.ScoresOwedAsync(db, sports, now, afterStart, reachDays, ct);

    /// <inheritdoc cref="OddsScoresJob.ScoresDueAsync"/>
    public static Task<IReadOnlyList<string>> ScoresDueAsync(
        XLinesDbContext db, ScoresOwed owed, DateTimeOffset now, CancellationToken ct = default)
        => OddsScoresJob.ScoresDueAsync(db, owed, now, ct);

    /// <inheritdoc cref="EspnResultsJob.DatesAwaitingResultsAsync"/>
    public Task<IReadOnlyList<DateOnly>> DatesAwaitingResultsAsync(
        TimeSpan settleAfter, CancellationToken ct = default)
        => _results.DatesAwaitingResultsAsync(settleAfter, ct);

    /// <inheritdoc cref="EspnResultsJob.StatsOwed"/>
    public static IQueryable<Game> StatsOwed(XLinesDbContext db, IReadOnlyList<string> sports)
        => EspnResultsJob.StatsOwed(db, sports);
}
