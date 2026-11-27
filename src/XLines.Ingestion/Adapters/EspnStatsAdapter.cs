using System.Globalization;
using System.Text.Json;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Adapters;

/// <summary>
/// ESPN's undocumented public JSON endpoints: schedules, scores, rosters and box scores,
/// free and unauthenticated across every sport we track.
///
/// These are unofficial and can change without notice, which is a deliberate trade here:
/// nothing critical depends solely on ESPN, and when it does break it produces a genuine
/// incident to triage rather than a hypothetical one.
/// </summary>
public partial class EspnStatsAdapter(HttpClient http, ILogger<EspnStatsAdapter> logger, TimeProvider clock, EspnPacer? pacer = null)
    : IStatsSource, IClosingLineSource, IFighterSource, IGameDaySource
{
    public const string SourceKey = "espn";

    public string Key => SourceKey;

    /// <summary>
    /// Pause between calls, for an adapter made without the host's <see cref="EspnPacer"/> — a
    /// test's, or a tool's. The host registers one pacer for the process, at
    /// <c>Ingestion:Espn:RequestDelay</c>, and every adapter it makes waits there instead.
    ///
    /// ESPN publishes no rate limit, which is a reason for more care rather than less: there
    /// is no documented ceiling to stay under, so the only safe assumption is to stay quiet.
    /// It matters most under history backfill, where a slate day is one call per finished
    /// game and the walk covers hundreds of days — see <c>HistoryBackfillService</c>.
    /// </summary>
    public TimeSpan RequestDelay { get; set; } = TimeSpan.Zero;

    /// <summary>This adapter's own gate, made at <see cref="RequestDelay"/> on first use, when no host pacer was given.</summary>
    private EspnPacer? _ownPacer;

    private EspnPacer Pacer => pacer ?? (_ownPacer ??= new EspnPacer(RequestDelay));

    /// <summary>Nothing fetched, nothing spent — for a league this port does not serve.</summary>
    private static StatsFetchResult Empty => new([], [], [], new FetchCost(0));

    /// <summary>
    /// ESPN paths are {sport}/{league}, which does not match our flat sport keys. The mapping
    /// lives in <see cref="EspnLeagues"/> so adding a league does not mean editing parsing code.
    /// </summary>
    private string? MapPath(string sportKey)
    {
        var path = EspnLeagues.PathFor(sportKey);

        if (path is null)
            logger.LogDebug("{Source}: no league mapping for '{Sport}'", SourceKey, sportKey);

        return path;
    }

    public async Task<StatsFetchResult> FetchScheduleAsync(
        string sportKey, DateOnly date, CancellationToken ct)
    {
        if (MapPath(sportKey) is not { } path)
            return Empty;

        // A card is read whole from the fight center; the scoreboard alone has no method or order.
        if (FightSports.IsFight(sportKey))
            return await FetchCardsAsync(path, sportKey, date, withResults: false, ct);

        var url = $"{path}/scoreboard?dates={date:yyyyMMdd}";

        using var doc = await GetJsonAsync(url, ct);
        var games = ParseScoreboard(doc.RootElement, sportKey, clock.GetUtcNow()).ToList();

        logger.LogInformation("{Source}: {Sport} {Date} -> {Count} games",
            SourceKey, sportKey, date, games.Count);

        return new StatsFetchResult(games, [], [], new FetchCost(1));
    }

    /// <summary>
    /// Deliberately does nothing, and returns no players.
    ///
    /// <para>
    /// It used to call <c>/teams</c> and throw the response away, which read as an implemented
    /// roster sync and was not one. Implementing it properly would be worse: a roster is every
    /// player on the books, and storing a player who has not appeared in a box score is exactly
    /// the allocation <c>StatsIngestionService</c> now refuses — those rows would be created
    /// and immediately discarded.
    /// </para>
    ///
    /// <para>
    /// Players enter through box scores, because appearing in one is what makes a player worth
    /// a row. Team identity, the other thing <c>/teams</c> would have given us, already arrives
    /// on the scoreboard at no extra request.
    /// </para>
    /// </summary>
    public Task<StatsFetchResult> FetchRosterAsync(string sportKey, CancellationToken ct)
        => Task.FromResult(new StatsFetchResult([], [], [], new FetchCost(0)));

    public async Task<StatsFetchResult> FetchBoxScoresAsync(
        string sportKey, DateOnly date, CancellationToken ct)
    {
        if (MapPath(sportKey) is not { } path)
            return Empty;

        // MMA has no summary endpoint: the fight center is the box score, one request a card.
        if (FightSports.IsFight(sportKey))
            return await FetchCardsAsync(path, sportKey, date, withResults: true, ct);

        var scoreboard = await FetchScheduleAsync(sportKey, date, ct);
        var finals = scoreboard.Games.Where(g => g.Status == "final").ToList();

        var players = new List<CanonicalPlayer>();
        var stats = new List<CanonicalPlayerStat>();
        var lines = new List<CanonicalOdds>();
        var contexts = new List<CanonicalGameContext>();
        var requests = scoreboard.Cost.Requests;

        foreach (var game in finals)
        {
            ct.ThrowIfCancellationRequested();
            requests += await FetchSummaryBoxAsync(path, sportKey, game, players, stats, lines, contexts, ct);
        }

        return new StatsFetchResult(scoreboard.Games, players, stats, new FetchCost(requests), lines)
        {
            GameContexts = contexts
        };
    }

    /// <summary>
    /// One game's box score: its row from the day's scoreboard, then its summary, read exactly as
    /// <see cref="FetchBoxScoresAsync"/> reads every final on the day. Two requests where the
    /// day's walk is one per final — the price of the gamecast feed landing the box the moment the
    /// game it follows ends (spec 2026-10-24 §6.3.4) rather than on the next results sweep.
    /// </summary>
    public async Task<StatsFetchResult> FetchBoxScoreAsync(
        string sportKey, DateOnly date, string sourceGameId, CancellationToken ct)
    {
        if (FightSports.IsFight(sportKey) || MapPath(sportKey) is not { } path)
            return Empty;

        var scoreboard = await FetchScheduleAsync(sportKey, date, ct);
        var game = scoreboard.Games.FirstOrDefault(g => g.SourceGameId == sourceGameId && g.Status == "final");

        if (game is null)
            return Empty with { Cost = scoreboard.Cost };

        var players = new List<CanonicalPlayer>();
        var stats = new List<CanonicalPlayerStat>();
        var lines = new List<CanonicalOdds>();
        var contexts = new List<CanonicalGameContext>();
        var requests = scoreboard.Cost.Requests
                       + await FetchSummaryBoxAsync(path, sportKey, game, players, stats, lines, contexts, ct);

        return new StatsFetchResult([game], players, stats, new FetchCost(requests), lines)
        {
            GameContexts = contexts
        };
    }

    /// <summary>
    /// One final's summary into the players, stat lines, closing lines and game context being
    /// gathered. A box score that will not come is logged and skipped, so one game cannot cost a
    /// slate.
    /// </summary>
    /// <returns>The requests it made.</returns>
    private async Task<int> FetchSummaryBoxAsync(
        string path, string sportKey, CanonicalGame game,
        List<CanonicalPlayer> players, List<CanonicalPlayerStat> stats, List<CanonicalOdds> lines,
        List<CanonicalGameContext> contexts, CancellationToken ct)
    {
        var requests = 0;

        try
        {
            using var doc = await GetJsonAsync($"{path}/summary?event={game.SourceGameId}", ct);
            requests++;

            ParseBoxScore(doc.RootElement, sportKey, game.SourceGameId, players, stats);

            // The venue, the crowd and the officials are in the same document, so they are read
            // from it rather than thrown away with the rest of what the box score does not need.
            if (ParseGameContext(doc.RootElement, sportKey, game.SourceGameId) is { } context)
                contexts.Add(context);

            // The same response carries what the line closed at, so the historical
            // reference is free — no second request, no second walk.
            var closed = ParseClosingLines(
                doc.RootElement, game.SourceGameId,
                game.HomeTeamName, game.AwayTeamName, game.StartsAt).ToList();

            // Except where the summary has dropped its odds block, which it does for older
            // games. The odds endpoint still has the close; one request buys it back.
            if (closed.Count == 0)
            {
                try
                {
                    closed.AddRange(await FetchClosingLinesAsync(sportKey,
                        new ClosingLineRequest(game.SourceGameId, game.HomeTeamName, game.AwayTeamName, game.StartsAt), ct));
                    requests++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A line is a reference; its absence must not cost the box score.
                    logger.LogDebug(ex, "{Source}: no closing odds for {GameId}", SourceKey, game.SourceGameId);
                }
            }

            lines.AddRange(closed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // One unavailable box score must not cost us the rest of the slate.
            logger.LogWarning(ex, "{Source}: box score unavailable for {GameId}",
                SourceKey, game.SourceGameId);
        }

        return requests;
    }

    /// <summary>
    /// What one finished game closed at, from ESPN's odds endpoint rather than the summary.
    ///
    /// <para>
    /// The summary's <c>pickcenter</c> is empty for most of a season gone by — the 2025 NFL
    /// season had none before late November — while
    /// <c>sports.core.api.espn.com/…/competitions/{id}/odds</c> still states the open and the
    /// close for every game. Same provider, same book, same rule: a reference for a game that
    /// has finished, never the market (ADR 0011).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<CanonicalOdds>> FetchClosingLinesAsync(
        string sportKey, ClosingLineRequest game, CancellationToken ct)
    {
        if (MapPath(sportKey) is not { } path || path.Split('/') is not [var sport, var league])
            return [];

        var url = $"https://sports.core.api.espn.com/v2/sports/{sport}/leagues/{league}"
                  + $"/events/{game.SourceEventId ?? game.SourceGameId}/competitions/{game.SourceGameId}/odds";

        using var doc = await GetJsonAsync(url, ct);

        return ParseCoreOdds(doc.RootElement, game.SourceGameId, game.HomeTeamName, game.AwayTeamName, game.StartsAt)
            .ToList();
    }


    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        var read = GamecastRequest.Current;

        // One gate for every ESPN request in the process, a watched game's first (EspnPacer).
        // Spacing is measured from turn to turn rather than slept unconditionally, so a slow
        // response has already paid the gap and does not pay it twice.
        await Pacer.WaitTurnAsync(read?.Watched == true, ct);

        if (read is null)
            return await SendAsync(relativeUrl, retry: true, ct);

        // A gamecast read: asked once, and given up on after its timeout, headers and body alike.
        // A linked token here and a flag the resilience handler reads, rather than a second named
        // client without the handler: the port keeps one HttpClient, its User-Agent and its
        // pacing, and the circuit breaker still sees the feed's failures with everyone else's. The
        // per-attempt timeout stays the handler's; this one is shorter, so it is the one that bites.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(read.Timeout);

        try
        {
            return await SendAsync(relativeUrl, retry: false, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{SourceKey}: {relativeUrl} took longer than {read.Timeout.TotalSeconds:0.#} s; the next poll asks again", ex);
        }
    }

    private async Task<JsonDocument> SendAsync(string relativeUrl, bool retry, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);

        if (!retry)
            request.Options.Set(GamecastRequest.NoRetry, true);

        // Headers first, then the body straight into the parser. A game summary is close to half a
        // megabyte of JSON and the gamecast feed reads one per followed game every poll; buffering
        // each whole before parsing it held the document in memory twice.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
