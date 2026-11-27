using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Configuration;

/// <summary>
/// The <c>Ingestion</c> section checked for what the code already takes for granted, so a typo in
/// configuration is refused by name rather than quietly worked around.
///
/// <para>
/// Each rule is one the services assume: a cadence the scheduler or the gamecast feed waits on is
/// positive (they fall back to the default where it is not — <c>Positive()</c> in each — and keep
/// doing so for now); the pacing clamp's floor is not above its ceiling; a reserve is never
/// negative and SportsGameOdds' never exceeds its allowance; a pause between requests is never
/// negative.
/// </para>
///
/// <para>
/// <b>Every league the history backfill would walk has a season start</b> (CLAUDE.md, Scope: no
/// league without a season start in <c>Ingestion:Backfill:Seasons</c>). The leagues it would walk
/// are the ones <see cref="Services.HistoryBackfillService"/> walks: <c>Ingestion:Backfill:Sports</c>
/// when it names any, else <c>Ingestion:Sports</c>, else <see cref="IngestionOptions.DefaultSports"/>.
/// With no start such a league is walked from <see cref="BackfillOptions.Since"/>, else
/// <see cref="BackfillOptions.Days"/> back from today — the one way configuration can pull a league's
/// history from before its season (the NBA before 2026-10-20, UFC before UFC 300). The rule holds
/// with <see cref="BackfillOptions.Enabled"/> off, because Enabled only starts the walk by itself and
/// the History window starts the same walk on a press; and whatever the stats source, because which
/// sources are eligible is read from the source rows and the registry when the walk runs.
/// A league in <c>Ingestion:Sports</c> that a non-empty <c>Ingestion:Backfill:Sports</c> leaves out
/// needs none: no walk takes it (a range walk is limited to the backfill's leagues too), and the
/// other readers of <c>Seasons</c> — The Odds API's season floor and the gamecast backport — only
/// narrow what they keep where a start is given and never reach back for want of one.
/// </para>
/// </summary>
public sealed class IngestionOptionsValidator : IValidateOptions<IngestionOptions>
{
    public ValidateOptionsResult Validate(string? name, IngestionOptions options)
    {
        var failures = Failures(options).ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Each rule the section breaks, as a sentence naming its key.</summary>
    public static IEnumerable<string> Failures(IngestionOptions options)
    {
        const string root = IngestionOptions.SectionName;

        // Cadences: waited on between ticks, polls and passes.
        foreach (var (key, value) in new (string, TimeSpan)[]
                 {
                     ("TickInterval", options.TickInterval),
                     ("MovementWindow", options.MovementWindow),
                     ("LinePolling:MinimumInterval", options.LinePolling.MinimumInterval),
                     ("LinePolling:MaximumInterval", options.LinePolling.MaximumInterval),
                     ("GamePasses:LivePoll", options.GamePasses.LivePoll),
                     ("GamePasses:SlateRefresh", options.GamePasses.SlateRefresh),
                     ("GamePasses:ResultsRetry", options.GamePasses.ResultsRetry),
                     ("Gamecast:Poll", options.Gamecast.Poll),
                     ("Gamecast:ProbabilityPoll", options.Gamecast.ProbabilityPoll),
                     ("Gamecast:BoxPoll", options.Gamecast.BoxPoll),
                     ("Gamecast:ReadTimeout", options.Gamecast.ReadTimeout),
                     ("Gamecast:UnwatchedPoll", options.Gamecast.UnwatchedPoll),
                     ("Gamecast:BreakPoll", options.Gamecast.BreakPoll),
                     ("Gamecast:BackportInterval", options.Gamecast.BackportInterval),
                     ("Gamecast:BackportRetryAfter", options.Gamecast.BackportRetryAfter),
                     ("OddsRetention:Interval", options.OddsRetention.Interval),
                     ("SportsGameOdds:BudgetWindow", options.SportsGameOdds.BudgetWindow)
                 })
        {
            if (value <= TimeSpan.Zero)
                yield return $"{root}:{key} must be positive; it is {value}.";
        }

        if (options.LinePolling.MinimumInterval > options.LinePolling.MaximumInterval)
            yield return $"{root}:LinePolling:MinimumInterval ({options.LinePolling.MinimumInterval}) must not exceed "
                         + $"{root}:LinePolling:MaximumInterval ({options.LinePolling.MaximumInterval}).";

        // Pauses between requests: zero is "no pause", a negative one is no delay Task.Delay takes.
        foreach (var (key, value) in new (string, TimeSpan)[]
                 {
                     ("Espn:RequestDelay", options.Espn.RequestDelay),
                     ("MlbStatsApi:RequestDelay", options.MlbStatsApi.RequestDelay),
                     ("TheOddsApi:RequestDelay", options.TheOddsApi.RequestDelay),
                     ("OddsApiIo:RequestDelay", options.OddsApiIo.RequestDelay),
                     ("BallDontLie:RequestDelay", options.BallDontLie.RequestDelay),
                     ("SportsGameOdds:RequestDelay", options.SportsGameOdds.RequestDelay),
                     ("SportsGameOdds:PageDelay", options.SportsGameOdds.PageDelay),
                     ("Backfill:RequestDelay", options.Backfill.RequestDelay)
                 })
        {
            if (value < TimeSpan.Zero)
                yield return $"{root}:{key} must not be negative; it is {value}.";
        }

        // Reserves: held back from what may be spent, so never below nothing and never above the whole.
        foreach (var (key, value) in new (string, int)[]
                 {
                     ("LinePolling:DailyReserve", options.LinePolling.DailyReserve),
                     ("LinePolling:HourlyReserve", options.LinePolling.HourlyReserve),
                     ("LinePolling:MonthlyCreditReserve", options.LinePolling.MonthlyCreditReserve),
                     ("SportsGameOdds:ObjectReserve", options.SportsGameOdds.ObjectReserve)
                 })
        {
            if (value < 0)
                yield return $"{root}:{key} must not be negative; it is {value}.";
        }

        if (options.SportsGameOdds.MonthlyObjects < 0)
            yield return $"{root}:SportsGameOdds:MonthlyObjects must not be negative; it is {options.SportsGameOdds.MonthlyObjects}.";

        if (options.SportsGameOdds.ObjectReserve > options.SportsGameOdds.MonthlyObjects)
            yield return $"{root}:SportsGameOdds:ObjectReserve ({options.SportsGameOdds.ObjectReserve}) must not exceed "
                         + $"{root}:SportsGameOdds:MonthlyObjects ({options.SportsGameOdds.MonthlyObjects}).";

        foreach (var failure in SeasonFailures(options))
            yield return failure;
    }

    /// <summary>
    /// The leagues the history backfill walks, picked as <see cref="Services.HistoryBackfillService"/>
    /// picks them: <c>Backfill:Sports</c> when it names any, else <see cref="IngestionOptions.EffectiveSports"/>.
    /// </summary>
    public static IEnumerable<string> BackfillLeagues(IngestionOptions options)
        => (options.Backfill.Sports.Length > 0 ? options.Backfill.Sports : options.EffectiveSports)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each league the history backfill would walk (<see cref="BackfillLeagues"/>) with no season start
    /// in <c>Ingestion:Backfill:Seasons</c>, as a sentence naming the league and the key to set. Part of
    /// <see cref="Failures"/>; the class summary says why this is the rule.
    /// </summary>
    public static IEnumerable<string> SeasonFailures(IngestionOptions options)
    {
        const string root = IngestionOptions.SectionName;

        foreach (var league in BackfillLeagues(options))
        {
            if (!options.Backfill.Seasons.ContainsKey(league))
                yield return $"{root}:Backfill:Seasons:{league} must be set: the history backfill walks '{league}' "
                             + $"({root}:Backfill:Sports, else {root}:Sports, else the default leagues), and with no "
                             + $"season start it would reach back to {root}:Backfill:Since or {root}:Backfill:Days "
                             + $"rather than the season's opening day. Set its opening day, or leave it out of {root}:Backfill:Sports.";
        }
    }
}
