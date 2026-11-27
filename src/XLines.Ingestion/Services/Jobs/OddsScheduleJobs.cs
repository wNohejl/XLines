using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LeagueScores = XLines.Ingestion.Services.IngestionJobs.LeagueScores;
using ScoresOwed = XLines.Ingestion.Services.IngestionJobs.ScoresOwed;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>The default stats source's fixtures (ADR 0026), <see cref="JobKeys.OddsFixtures"/>: free.</summary>
internal sealed class OddsFixturesJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key) => key == JobKeys.OddsFixtures;

    /// <summary>Fixtures, free, under Today, on a desk whose menu is the default source's. Without a key it says where to enter one.</summary>
    public ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
    {
        if (menu.Espn)
            return JobSupport.Nothing();

        var scores = menu.Registry.Scores;
        var scheduled = menu.Sports.Where(s => !FightSports.IsFight(s)).Select(s => s.ToUpperInvariant()).ToList();

        return JobSupport.Describe(new IngestionJob(
            Key: JobKeys.OddsFixtures,
            Label: "Fixtures",
            Description: $"The schedule for the next eight days in {string.Join(", ", scheduled)} from The Odds API. "
                         + "No scores: those are their own pull.",
            EstimatedRequests: scores is null ? 0 : scheduled.Count,
            Available: scores is not null,
            Unavailable: scores is null ? IngestionJobs.NoOddsKey : null,
            Group: JobGroups.Today,
            Hint: "Calls The Odds API's events list, one request a league, which does not count against the 500 credits."));
    }

    /// <summary>
    /// The default source's fixtures for every league it schedules, from today (the league's day)
    /// eight days ahead (ADR 0026). Free: the guard is not asked, and the run rows say nothing was billed.
    /// </summary>
    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var rows = 0;
        var failures = 0;
        var leagues = 0;

        foreach (var sportKey in _options.EffectiveSports.Where(s => !FightSports.IsFight(s)))
        {
            if (ct.IsCancellationRequested)
                break;

            await using var scope = scopeFactory.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();

            if (registry.FindStats(Adapters.TheOddsApiScheduleAdapter.SourceKey) is not { } source)
                return new JobOutcome(JobKeys.OddsFixtures, false, 0, 0, JobSupport.NoScheduleSource(registry));

            var outcome = await scope.ServiceProvider.GetRequiredService<StatsIngestionService>()
                .IngestFixturesAsync(source, sportKey, JobKeys.OddsFixtures, LeagueClock.Today(), ct);

            rows += outcome.RowsIngested;
            leagues++;

            if (outcome.Status == RunStatus.Failed)
                failures++;
        }

        return new JobOutcome(JobKeys.OddsFixtures, failures == 0, rows, failures,
            $"Fixtures for {leagues} {(leagues == 1 ? "league" : "leagues")}, eight days ahead: {rows:N0} games read. No credits spent.");
    }
}

/// <summary>
/// The default stats source's final scores (ADR 0026): unattended, <see cref="JobKeys.OddsScores"/>,
/// and one league on request, <c>odds:scores:{sport}</c>.
/// </summary>
internal sealed class OddsScoresJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    SettleJob settle,
    TimeProvider clock) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key)
        => key == JobKeys.OddsScores || key.StartsWith(JobKeys.OddsScoresPrefix, StringComparison.Ordinal);

    /// <summary>One scores pull per league, metered, beside the lines. Without a key each says where to enter one.</summary>
    public ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
    {
        if (menu.Espn)
            return JobSupport.Nothing();

        var scores = menu.Registry.Scores;
        const string NoKey = IngestionJobs.NoOddsKey;
        var jobs = new List<IngestionJob>();

        foreach (var sport in menu.Sports)
        {
            var league = sport.ToUpperInvariant();
            var covered = scores?.CoversScores(sport) ?? !FightSports.IsFight(sport);
            var credits = scores?.CreditsPerScoresCall ?? Adapters.TheOddsApiScheduleAdapter.CreditsWithDaysFrom;
            var check = menu.Allowance?.Check(credits) ?? CreditCheck.Go;

            jobs.Add(new IngestionJob(
                Key: JobKeys.OddsScoresPrefix + sport,
                Label: $"Pull scores — {league}",
                Description: $"Final scores for {league} games completed in the last three days, then settles the journal. "
                             + "Older finals are beyond what The Odds API returns.",
                EstimatedRequests: covered && scores is not null ? 1 : 0,
                EstimatedCredits: credits,
                Available: scores is not null && covered && check.Allowed,
                Unavailable: scores is null ? NoKey
                    : !covered ? $"The Odds API keeps no {league} results; they need ESPN."
                    : check.Refusal,
                Group: JobGroups.Lines,
                Confirm: check.Question,
                Hint: $"Calls The Odds API's scores for {league} reaching back three days: {credits} credits. "
                      + JobSupport.MonthText(menu.Allowance, menu.Resets)
                      + (scores is null ? " " + IngestionJobs.NoOddsKey : ""),
                CreditsLeft: menu.Left,
                Metered: true,
                NeedsKey: scores is null));
        }

        return ValueTask.FromResult<IReadOnlyList<IngestionJob>>(jobs);
    }

    public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        => run.Key == JobKeys.OddsScores
            ? RunScoresAsync(null, unattended: true, ct)
            : RunScoresAsync(run.Key[JobKeys.OddsScoresPrefix.Length..], unattended: false, ct);

    /// <summary>
    /// Final scores from the default source (ADR 0026), at two credits a league a call.
    ///
    /// <para>
    /// On request (<paramref name="onlySport"/>): that league, whatever is owed — the person pressing
    /// it saw the price. Unattended (<see cref="JobKeys.OddsScores"/>): only when automatic scanning is chosen,
    /// only the leagues <see cref="ScoresDueAsync"/> names, and only while the month affords the call
    /// above the reserve held for pulls on request — so the scheduler never spends what an operator
    /// might want. Settles afterwards when a final arrived.
    /// </para>
    /// </summary>
    private async Task<JobOutcome> RunScoresAsync(string? onlySport, bool unattended, CancellationToken ct)
    {
        var jobKey = onlySport is null ? JobKeys.OddsScores : JobKeys.OddsScoresPrefix + onlySport;

        IScoreSource? source;
        AllowanceState? allowance;
        IReadOnlyList<string> sports;
        ScoresOwed owed;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
            var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

            source = registry.Scores;
            if (source is null)
                return new JobOutcome(jobKey, false, 0, 0, JobSupport.NoScheduleSource(registry));

            // Never metered unattended unless the operator chose automatic: the same stored choice
            // the lines obey, so On request means nothing is spent unasked, scores included.
            if (unattended && !await db.LinesRunUnattendedAsync(_options.LinePolling.RunsUnattended, ct))
                return new JobOutcome(jobKey, true, 0, 0, "Scores are pulled on request only; nothing was spent.");

            allowance = await JobSupport.ReadAllowanceAsync(scope.ServiceProvider, ct);
            owed = await ScoresOwedAsync(db, _options.EffectiveSports.Where(source.CoversScores).ToList(),
                clock.GetUtcNow(), _options.GamePasses.ResultsAfterStart, source.ScoresReachDays, ct);

            if (onlySport is not null)
            {
                var league = _options.EffectiveSports.FirstOrDefault(s => string.Equals(s, onlySport, StringComparison.OrdinalIgnoreCase));
                if (league is null)
                    return new JobOutcome(jobKey, false, 0, 0, $"'{onlySport}' is not a configured sport.");
                if (!source.CoversScores(league))
                    return new JobOutcome(jobKey, false, 0, 0, $"The Odds API keeps no {league.ToUpperInvariant()} results; they need ESPN.");

                sports = [league];
            }
            else
            {
                sports = await ScoresDueAsync(db, owed, clock.GetUtcNow(), ct);
            }
        }

        if (sports.Count == 0)
            return new JobOutcome(jobKey, true, 0, 0, "No league is owed scores now; nothing was spent." + Beyond(owed));

        var rows = 0;
        var failures = 0;
        var spent = 0;
        var held = new List<string>();

        foreach (var sportKey in sports)
        {
            if (ct.IsCancellationRequested)
                break;

            var cost = source.CreditsPerScoresCall;

            // Asked again before each league: the last one's spend is already counted.
            await using var scope = scopeFactory.CreateAsyncScope();
            var state = await JobSupport.ReadAllowanceAsync(scope.ServiceProvider, ct) ?? allowance;

            if (state is not null && (unattended ? !state.AffordsUnattended(cost) : !state.Check(cost).Allowed))
            {
                held.Add(sportKey.ToUpperInvariant());
                continue;
            }

            var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
            if (registry.Scores is not { } scores)
                break;

            var outcome = await scope.ServiceProvider.GetRequiredService<StatsIngestionService>()
                .IngestScoresAsync(scores, sportKey, JobKeys.OddsScoresPrefix + sportKey, ct);

            rows += outcome.RowsIngested;
            if (outcome.Status == RunStatus.Failed)
                failures++;

            var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();
            spent += await db.IngestionRuns.Where(r => r.Id == outcome.RunId).Select(r => r.CreditsSpent).FirstOrDefaultAsync(ct);
        }

        if (rows > 0)
            await settle.RunAsync(new JobRun(JobKeys.Settle), ct);

        var detail = $"{string.Join(", ", sports.Select(s => s.ToUpperInvariant()).Except(held))}: {rows:N0} games read, {spent:N0} credits spent"
                     + (rows > 0 ? ", then settled" : "")
                     + "."
                     + (held.Count > 0
                         ? unattended
                             ? $" Held back for {string.Join(", ", held)}: the call would dip into the credits kept for pulls on request."
                             : $" Not pulled for {string.Join(", ", held)}: the month cannot cover it."
                         : "")
                     + Beyond(owed);

        return new JobOutcome(jobKey, failures == 0, rows, failures, detail);
    }

    /// <summary>"3 games started more than three days ago have no final and cannot get one from The Odds API."</summary>
    private static string Beyond(ScoresOwed owed)
        => owed.Unrecoverable == 0
            ? ""
            : $" {owed.Unrecoverable:N0} {(owed.Unrecoverable == 1 ? "game" : "games")} started more than three days ago "
              + $"{(owed.Unrecoverable == 1 ? "has" : "have")} no final and cannot get one from The Odds API.";

    /// <summary>
    /// Per league, the games a scores call could finish now — started at least
    /// <paramref name="afterStart"/> ago, within the provider's reach, and neither final nor postponed;
    /// the games still being played; and, across leagues, the games beyond the reach that never went
    /// final here, which no call can finish (they are counted for the desk to say so, never asked for).
    /// </summary>
    public static async Task<ScoresOwed> ScoresOwedAsync(
        XLinesDbContext db, IReadOnlyList<string> sports, DateTimeOffset now, TimeSpan afterStart, int reachDays,
        CancellationToken ct = default)
    {
        var reach = now.AddDays(-reachDays);
        var settled = now - afterStart;
        var lookback = now.AddDays(-45);

        var open = await db.Games
            .Where(g => sports.Contains(g.Sport!.Key)
                        && g.StartsAt <= now
                        && g.StartsAt >= lookback
                        && g.Status != GameStatus.Final
                        && g.Status != GameStatus.Postponed)
            .Select(g => new { Sport = g.Sport!.Key, g.StartsAt })
            .ToListAsync(ct);

        var byLeague = sports.ToDictionary(
            s => s,
            s => new LeagueScores(
                Owed: open.Count(g => g.Sport == s && g.StartsAt <= settled && g.StartsAt > reach),
                Playing: open.Count(g => g.Sport == s && g.StartsAt > settled)),
            StringComparer.OrdinalIgnoreCase);

        return new ScoresOwed(byLeague, open.Count(g => g.StartsAt <= reach));
    }

    /// <summary>
    /// The leagues an unattended scores call is due for: owed a final, none of its games still being
    /// played (so one call takes the whole night's slate), and not yet asked on this league day — at
    /// most one call a league a day, manual pulls included.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ScoresDueAsync(
        XLinesDbContext db, ScoresOwed owed, DateTimeOffset now, CancellationToken ct = default)
    {
        var dayStart = LeagueClock.StartOf(LeagueClock.DateOf(now));
        var keys = owed.Leagues.Keys.Select(s => JobKeys.OddsScoresPrefix + s).ToList();

        var askedToday = (await db.IngestionRuns
                .Where(r => keys.Contains(r.JobKey) && r.StartedAt >= dayStart)
                .Select(r => r.JobKey)
                .Distinct()
                .ToListAsync(ct))
            .Select(k => k[JobKeys.OddsScoresPrefix.Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return owed.Leagues
            .Where(kv => kv.Value.Owed > 0 && kv.Value.Playing == 0 && !askedToday.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();
    }
}
