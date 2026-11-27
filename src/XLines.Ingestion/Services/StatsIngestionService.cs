using System.Text.Json;
using System.Text.Json.Nodes;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Services;

/// <summary>
/// Ingests schedules, scores, rosters and box scores.
///
/// Mirrors <see cref="OddsIngestionService"/>: same run recording, same budget guard, same
/// failure semantics — so the reliability layer treats stats and odds sources identically
/// and one KPI dashboard covers both.
/// </summary>
public partial class StatsIngestionService(
    XLinesDbContext db,
    EntityResolver resolver,
    CreditBudgetGuard budget,
    ILogger<StatsIngestionService> logger,
    TimeProvider clock)
{
    /// <summary>Schedules, scores and box scores for a day.</summary>
    public Task<IngestionOutcome> IngestAsync(
        IStatsSource source, string sportKey, string jobKey, DateOnly date, CancellationToken ct)
        => IngestAsync(source, sportKey, jobKey, date, schedulesOnly: false, ct);

    /// <summary>
    /// Schedules and scores only — no per-game box scores.
    ///
    /// A box-score pass costs one request per finished game on top of the scoreboard, and
    /// before the games are played there is nothing to summarise. Fetching a slate for today
    /// and a slate for yesterday are therefore very different prices, and only one of them is
    /// worth paying twice a day.
    /// </summary>
    public Task<IngestionOutcome> IngestScheduleAsync(
        IStatsSource source, string sportKey, string jobKey, DateOnly date, CancellationToken ct)
        => IngestAsync(source, sportKey, jobKey, date, schedulesOnly: true, ct);

    /// <summary>
    /// A fixtures pull from a source whose schedule call is free though the source is metered (The
    /// Odds API's <c>/events</c>, ADR 0026): the schedule pass without the budget guard, so a spent
    /// month still keeps the fixtures current. Its run row says what the provider billed — nothing.
    /// </summary>
    public Task<IngestionOutcome> IngestFixturesAsync(
        IStatsSource source, string sportKey, string jobKey, DateOnly date, CancellationToken ct)
        => IngestAsync(source.Key, source, sportKey, jobKey,
            c => source.FetchScheduleAsync(sportKey, date, c), ct, guarded: false);

    /// <summary>
    /// A league's final scores from a metered score source (<see cref="IScoreSource"/>): through the
    /// same run row, budget guard and persist as any stats pass, so the finals it writes are graded
    /// by settlement exactly as a box-score pass's are.
    /// </summary>
    public Task<IngestionOutcome> IngestScoresAsync(
        IScoreSource source, string sportKey, string jobKey, CancellationToken ct)
        => IngestAsync(source.Key, source, sportKey, jobKey, c => source.FetchScoresAsync(sportKey, c), ct);

    /// <summary>
    /// The scoreboard for a day, written only for the events being played or just finished.
    ///
    /// <para>
    /// Same request as <see cref="IngestScheduleAsync"/> — the scoreboard already carries every
    /// live score — but run every few seconds while a game is on, so it does none of the slate's
    /// other work. An event is kept when the provider says it is live, or says it is over while
    /// our row still has it in play: that last poll is what lands the final score and status.
    /// Everything else on the board — tonight's games, last night's finals — is the slate's and
    /// the results sweep's, and resolving it here would repeat their writes on every poll.
    /// </para>
    ///
    /// <para>
    /// A game we already hold is resolved without its team references, so the resolver does
    /// not re-read both sides' identity for it: names, ids and abbreviations do not change
    /// mid-game, and the slate keeps them current. A live event we have never seen keeps its
    /// references, because creating its teams from names alone would lose the provider's ids.
    /// </para>
    /// </summary>
    public Task<IngestionOutcome> IngestLiveAsync(
        IStatsSource source, string sportKey, string jobKey, DateOnly date, CancellationToken ct)
        => IngestAsync(source, sportKey, jobKey, date, schedulesOnly: true, ct,
            narrow: (result, sourceKey, c) => InPlayAsync(result, sportKey, sourceKey, c), quietPoll: true);

    /// <summary>
    /// One day of a range the operator named (<see cref="RangePull"/>): the box-score pass for a
    /// day gone by, the schedule pass for today and after, through the same run row, budget guard
    /// and persist as either — except that a game we hold as final is not walked back by it.
    ///
    /// <para>
    /// A range reaches into history, and ESPN's view of an old fixture is not always the one it
    /// had the night it was played: an event moved or relisted reads as scheduled, scoreless. The
    /// resolver takes whatever status and score it is handed (only a missing score is left
    /// alone), so a re-pull would put a graded game back before its first play. A held final
    /// therefore keeps its status, score and state of play unless the provider still calls it
    /// final, when its score — a correction — is taken as the results sweep takes it.
    /// </para>
    /// </summary>
    public Task<IngestionOutcome> IngestRangeDayAsync(
        IStatsSource source, string sportKey, string jobKey, DateOnly date, bool schedulesOnly, CancellationToken ct)
        => IngestAsync(source, sportKey, jobKey, date, schedulesOnly, ct,
            narrow: async (result, sourceKey, c) => result.Games.Count == 0
                ? result
                : result with { Games = KeepHeldFinals(result.Games, await HeldStatusesAsync(result, sportKey, sourceKey, c)) });

    /// <summary>
    /// A scoreboard with every game we hold as final kept so: one the provider no longer calls
    /// final carries no status, score or state of play, which the resolver reads as no news. See
    /// <see cref="IngestRangeDayAsync"/>.
    /// </summary>
    internal static IReadOnlyList<CanonicalGame> KeepHeldFinals(
        IReadOnlyList<CanonicalGame> scoreboard, IReadOnlyDictionary<string, GameStatus> ours)
        => scoreboard
            .Select(g => ours.TryGetValue(g.SourceGameId, out var held) && held == GameStatus.Final
                         && EntityResolver.MapStatus(g.Status) != GameStatus.Final
                ? g with { Status = null, HomeScore = null, AwayScore = null, Live = null }
                : g)
            .ToList();

    private async Task<StatsFetchResult> InPlayAsync(
        StatsFetchResult result, string sportKey, string sourceKey, CancellationToken ct)
    {
        if (result.Games.Count == 0)
            return result;

        return result with { Games = SelectInPlay(result.Games, await HeldStatusesAsync(result, sportKey, sourceKey, ct)) };
    }

    /// <summary>Our status for each event on a scoreboard that we hold, keyed by the provider's event id.</summary>
    private async Task<Dictionary<string, GameStatus>> HeldStatusesAsync(
        StatsFetchResult result, string sportKey, string sourceKey, CancellationToken ct)
    {
        var from = result.Games.Min(g => g.StartsAt).AddDays(-2);
        var to = result.Games.Max(g => g.StartsAt).AddDays(2);

        return (await db.Games
                .Where(g => g.Sport!.Key == sportKey && g.StartsAt > from && g.StartsAt < to)
                .Select(g => new { g.ExternalIds, g.Status })
                .ToListAsync(ct))
            .Where(g => g.ExternalIds.ContainsKey(sourceKey))
            .GroupBy(g => g.ExternalIds[sourceKey])
            .ToDictionary(g => g.Key, g => g.First().Status);
    }

    /// <summary>
    /// The events a live pass writes, from a scoreboard and our status for each event we hold
    /// (keyed by the provider's event id). See <see cref="IngestLiveAsync"/>.
    /// </summary>
    internal static IReadOnlyList<CanonicalGame> SelectInPlay(
        IReadOnlyList<CanonicalGame> scoreboard, IReadOnlyDictionary<string, GameStatus> ours)
    {
        var kept = new List<CanonicalGame>();

        foreach (var game in scoreboard)
        {
            var held = ours.TryGetValue(game.SourceGameId, out var status);
            var stillInPlayHere = held && status is not (GameStatus.Final or GameStatus.Postponed);

            var keep = EntityResolver.MapStatus(game.Status) switch
            {
                GameStatus.Live => true,
                // Just finished: over on the provider, still in play here. A final we already
                // hold is the results sweep's, corrections included.
                GameStatus.Final or GameStatus.Postponed => stillInPlayHere,
                _ => false
            };

            if (keep)
                kept.Add(held ? game with { Home = null, Away = null } : game);
        }

        return kept;
    }

    /// <summary>
    /// One game's box score, written exactly as a day's is (<see cref="IngestAsync(IStatsSource, string, string, DateOnly, CancellationToken)"/>):
    /// the same run row, budget guard and persist, so the final score, every player's line and
    /// the close land as the results sweep would land them. The gamecast feed's, for the game it
    /// was following, the moment that game is final (spec 2026-10-24 §6.3.4).
    /// </summary>
    public Task<IngestionOutcome> IngestBoxScoreAsync(
        IGamecastSource source, string sportKey, string jobKey, DateOnly date, string sourceGameId, CancellationToken ct)
        => IngestAsync(source.Key, source, sportKey, jobKey,
            c => source.FetchBoxScoreAsync(sportKey, date, sourceGameId, c), ct);

    /// <summary>
    /// A game in play's box score as it stands, from a summary the gamecast feed has already read
    /// (<see cref="IGamecastSource.FetchGamecastWithBoxAsync"/>): the players and their lines,
    /// through the same run row, budget guard and persist as the final box.
    ///
    /// <para>
    /// Only the players and their lines. Whatever games or lines the box states are dropped
    /// here, so a live write cannot move the game: its status and score are the scoreboard
    /// pass's, and a box that called it final, or scoreless, would undo them. The lines resolve
    /// to the game we already hold by its event id, as a stat line for a game the payload does
    /// not carry always does.
    /// </para>
    ///
    /// <para>
    /// Quiet the way a live scoreboard poll is: on an unmetered source, a box that changed
    /// nothing — every line as stored, no player new — writes no run row and raises no notice.
    /// </para>
    /// </summary>
    public Task<IngestionOutcome> IngestLiveBoxScoreAsync(
        string sourceKey, string sportKey, string jobKey, StatsFetchResult box, CancellationToken ct)
        => IngestAsync(sourceKey, adapter: null, sportKey, jobKey,
            _ => Task.FromResult(box with { Games = [], ClosingLines = null }), ct, live: true);

    private Task<IngestionOutcome> IngestAsync(
        IStatsSource source,
        string sportKey,
        string jobKey,
        DateOnly date,
        bool schedulesOnly,
        CancellationToken ct,
        Func<StatsFetchResult, string, CancellationToken, Task<StatsFetchResult>>? narrow = null,
        bool quietPoll = false)
        => IngestAsync(source.Key, source, sportKey, jobKey,
            c => schedulesOnly
                ? source.FetchScheduleAsync(sportKey, date, c)
                : source.FetchBoxScoresAsync(sportKey, date, c),
            ct, narrow, quietPoll: quietPoll);

    private async Task<IngestionOutcome> IngestAsync(
        string sourceKey,
        object? adapter,
        string sportKey,
        string jobKey,
        Func<CancellationToken, Task<StatsFetchResult>> fetch,
        CancellationToken ct,
        Func<StatsFetchResult, string, CancellationToken, Task<StatsFetchResult>>? narrow = null,
        bool live = false,
        bool quietPoll = false,
        bool guarded = true)
    {
        var sourceRow = await RunRecord.SourceRowAsync(db, sourceKey, ct);

        var run = new IngestionRun
        {
            SourceId = sourceRow.Id,
            JobKey = jobKey,
            StartedAt = clock.GetUtcNow(),
            Status = RunStatus.Running
        };

        // A live poll runs every fifteen seconds all game, and most see the board the last one
        // saw. Its run is written only once there is something to say — a change, a failure, a
        // refusal — so a poll that moved nothing leaves no row and raises no notice: a run row is
        // a `runs` notice, and every open Ops, Runs and History window reloaded on each one. A
        // metered source keeps its row every time, because the budget guard counts requests from
        // the runs and a request with no row would be spent without being counted. A live box
        // score is quiet on the same terms; a range day, narrowed too, is not a poll and keeps its row.
        var quiet = (quietPoll || live) && IsUnmetered(sourceRow);

        if (!quiet)
            await RecordAsync(run, ct);

        var saved = 0;
        void Count(object? sender, SavedChangesEventArgs e) => saved += e.EntitiesSavedCount;

        // What the fetch cost, once it has answered: a failure after that point still spent it.
        FetchCost? spent = null;

        try
        {
            if (guarded && !await budget.TryReserveAsync(sourceRow, estimatedRequests: 2, ct))
            {
                run.Status = RunStatus.Partial;
                run.Error = RunRecord.BudgetRefused;
                run.FinishedAt = clock.GetUtcNow();
                await RecordAsync(run, ct);
                return new IngestionOutcome(run.Id, run.Status, 0, run.Error);
            }

            if (adapter is IFailureInjectable injectable)
                injectable.FailureMode = sourceRow.FailureMode;

            var result = await fetch(ct);
            spent = result.Cost;

            if (narrow is not null)
                result = await narrow(result, sourceRow.Key, ct);

            // Including what the provider says is left on the key, where it says (The Odds API does;
            // ESPN never does, so its runs keep null as before) — the count MonthlyCredits reconciles
            // against.
            run.Charge(result.Cost);

            // Every save in the pass, the resolver's own included: what tells a poll that changed
            // something from one that only read.
            db.SavedChanges += Count;
            int rows;

            try
            {
                // Stat lines carry the run's id, so the first line written or changed records a
                // quiet pass's run; a pass whose lines all stand as stored needs none.
                rows = await PersistAsync(sourceRow, result, sportKey, async c =>
                {
                    if (run.Id == 0)
                        await RecordAsync(run, c);

                    return run.Id;
                }, live, ct);
            }
            finally
            {
                db.SavedChanges -= Count;
            }

            if (quiet && run.Id == 0 && saved == 0 && !db.ChangeTracker.HasChanges())
            {
                logger.LogDebug("{Source}/{Sport}: live poll, nothing moved", sourceKey, sportKey);
                return new IngestionOutcome(0, RunStatus.Success, 0, null);
            }

            run.RowsIngested = rows;
            run.FinishedAt = clock.GetUtcNow();
            run.Status = RunStatus.Success;

            await RecordAsync(run, ct);

            logger.LogInformation("{Source}/{Sport}: {Rows} stat rows", sourceKey, sportKey, rows);
            return new IngestionOutcome(run.Id, run.Status, rows, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A persist that threw leaves the change tracker holding exactly the entities that
            // could not be saved. Recording the failure means calling SaveChanges again, which
            // would retry them and throw a second time — out of the catch block, past the run
            // row, and into the caller. That turned one bad day into a dead backfill. Discard
            // the pending work first, then re-read the run to carry the outcome.
            db.ChangeTracker.Clear();

            var error = RunRecord.Describe(ex);

            // A live poll's run not written yet is written now: a failure is always recorded.
            var failed = run.Id == 0
                ? run
                : await db.IngestionRuns.FirstOrDefaultAsync(r => r.Id == run.Id, ct);

            if (failed is not null)
            {
                failed.Status = RunStatus.Failed;
                failed.Error = error;
                failed.FinishedAt = clock.GetUtcNow();

                // A call that was answered and billed still spent the month though nothing came of
                // it, as the odds pass records: the cost the provider's exception carries, or the
                // answered fetch's when the failure came after it. The re-read row never saw the
                // charge, which lived only on the discarded tracked copy. Where neither says, the
                // run records no cost rather than a guess.
                if (((ex as OddsFetchFailedException)?.Cost ?? spent) is { } cost)
                    failed.Charge(cost);

                await RecordAsync(failed, ct);
            }

            logger.LogError(ex, "{Source}/{Sport}: stats ingestion failed", sourceKey, sportKey);
            return new IngestionOutcome(failed?.Id ?? run.Id, RunStatus.Failed, 0, error);
        }
    }

    /// <summary>The live box's runs (<see cref="JobKeys.EspnGamecastLive"/>) behind this source's lines for these games.</summary>
    private async Task<HashSet<long>> LiveRunsAsync(Source sourceRow, List<int> gameIds, CancellationToken ct)
        => (await db.PlayerGameStats
                .Where(s => gameIds.Contains(s.GameId) && s.SourceId == sourceRow.Id
                            && db.IngestionRuns.Any(r => r.Id == s.IngestionRunId
                                                         && r.JobKey == JobKeys.EspnGamecastLive))
                .Select(s => s.IngestionRunId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

    /// <summary>Saves the run, adding it first if it has no row yet.</summary>
    private async Task RecordAsync(IngestionRun run, CancellationToken ct)
    {
        if (run.Id == 0)
            db.IngestionRuns.Add(run);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether a stored stat line says what a parsed one says. The column is jsonb, which Postgres
    /// hands back re-spaced and with its keys in its own order, so the stored string is never the
    /// parsed one; compared as strings every re-walk rewrote every line, and a live box that had
    /// not moved wrote a run regardless. Compared as JSON, falling back to the strings for a line
    /// that is not JSON.
    /// </summary>
    public static bool SameStatLine(string stored, string parsed)
    {
        if (stored == parsed)
            return true;

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(stored), JsonNode.Parse(parsed));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A source with no request or credit ceiling, whose spend the budget guard never reads.</summary>
    private static bool IsUnmetered(Source source)
        => source.RateLimitPerHour is null && source.RateLimitPerDay is null && source.MonthlyCreditBudget is null;
}
