using XLines.Ingestion.Configuration;

namespace XLines.Ingestion.Services;

/// <summary>
/// The keys the catalogue (<see cref="IngestionJobs"/>) runs jobs by, and the prefixes a key
/// narrowed to one league starts with. A key is the name a run is recorded under, so a key
/// changed here is a history that stops matching.
/// </summary>
public static class JobKeys
{
    public const string EspnSlate = "espn:slate";

    /// <summary>
    /// The scoreboard for games being played (spec 2026-10-24 §5.2). Its own job rather than the
    /// slate run more often: the slate resolves every event on the day, refreshes every team's
    /// identity and syncs MLB's spine, which is right twice an hour and wasteful every fifteen
    /// seconds.
    /// </summary>
    public const string EspnLive = "espn:live";

    public const string EspnResults = "espn:results";

    /// <summary>
    /// A game in play's box score as it stands, written by the gamecast feed every
    /// <see cref="GamecastOptions.BoxPoll"/> (spec 2026-10-24 §6.1). Its own job, apart from the
    /// feed's final box (<see cref="GamecastFeed.JobKey"/>), because a line from it is not a
    /// final's stats: a final holding nothing newer is still owed (<see cref="IngestionJobs.StatsOwed"/>), or a
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
    /// (<see cref="IngestionJobs.ScoresDueAsync"/>). Two credits a league a call.
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
}
