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
/// The repository's rule that every configured league has a season start (CLAUDE.md, Scope) is
/// <see cref="SeasonFailures"/>, checked against the shipped files by a test rather than here.
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

    }

    /// <summary>
    /// Each league <c>Ingestion:Sports</c> or <c>Ingestion:Backfill:Sports</c> names, as written rather
    /// than through their fallbacks, that has no season start in <c>Ingestion:Backfill:Seasons</c>.
    ///
    /// <para>
    /// Not part of <see cref="Validate"/>: the code itself does not depend on it (a league with no
    /// season start backfills from <see cref="BackfillOptions.Since"/>, then <see cref="BackfillOptions.Days"/>),
    /// and hosts built in tests name leagues with no seasons. It is the repository's rule for the
    /// shipped configuration, and a test holds both shipped files to it.
    /// </para>
    /// </summary>
    public static IEnumerable<string> SeasonFailures(IngestionOptions options)
    {
        const string root = IngestionOptions.SectionName;

        var leagues = options.Sports.Concat(options.Backfill.Sports)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var league in leagues)
        {
            if (!options.Backfill.Seasons.ContainsKey(league))
                yield return $"{root}:Backfill:Seasons has no season start for '{league}', which "
                             + $"{root}:Sports or {root}:Backfill:Sports names.";
        }
    }
}
