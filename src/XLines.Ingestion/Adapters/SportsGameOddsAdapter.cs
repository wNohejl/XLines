using System.Globalization;
using System.Net;
using System.Text.Json;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Ingestion.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Adapters;

/// <summary>
/// SportsGameOdds (<c>api.sportsgameodds.com/v2</c>), the second price source (ADR 0033): the
/// books The Odds API's free plan does not carry — Caesars, by default — on the owner's own free
/// key, through the same ingestion pipeline and the same game matcher as every book.
///
/// <para>
/// <b>Not verified against the live service.</b> No key was held when this was written. The
/// request and the shapes read here follow the provider's public documentation as read on
/// 2026-11-26 (<see href="https://sportsgameodds.com/docs/endpoints/getEvents"/>,
/// <see href="https://sportsgameodds.com/docs/data-types/odds"/>,
/// <see href="https://sportsgameodds.com/docs/data-types/markets"/>), and the tests pin them against
/// a fixture built from those field names, not a captured response. Two things the documentation
/// is unclear on are read defensively: whether a book's entry carries <c>spread</c> and
/// <c>lastUpdatedAt</c> (one page lists them, another does not), and whether numbers arrive as
/// strings (documented) or as numbers (accepted too).
/// </para>
///
/// <para>
/// What a pull asks: <c>GET events</c> for one league with <c>bookmakerID</c> narrowed to the
/// configured books and <c>oddID</c> to the six full-game outcomes (moneyline, spread, total, each
/// side), games not yet started and starting within <see cref="SportsGameOddsOptions.LookAhead"/>,
/// at most <see cref="EventCeiling"/> events, following <c>nextCursor</c>. The key goes in the
/// <c>X-Api-Key</c> header — never the address, so it is never in a log line.
/// </para>
///
/// <para>
/// What it costs: the provider bills an <i>object</i> per event returned, however many books and
/// markets ride on it, and counts requests against a 10-a-minute limit. The cost reported is the
/// events this call returned — the provider sends no usage header — which the run row records as
/// its "credits", against this source's own row and budget (<see cref="Services.SportsGameOddsBudget"/>),
/// never The Odds API's.
/// </para>
///
/// <para>
/// How it fails: a refusal (401, 403, 429), an error envelope or an unreadable body throws
/// <see cref="OddsFetchFailedException"/> carrying the objects already returned, before anything is
/// written — every page is read before the result is handed back, so a pull is written whole or
/// not at all, and the run row says why.
/// </para>
/// </summary>
public class SportsGameOddsAdapter(
    HttpClient http,
    IOptions<IngestionOptions> options,
    ILogger<SportsGameOddsAdapter> logger) : IOddsSource, Services.IBookSidesByEvent
{
    public const string SourceKey = SourceAccounts.SportsGameOdds;

    public const string BaseUrl = "https://api.sportsgameodds.com/v2/";

    private readonly SportsGameOddsOptions _config = options.Value.SportsGameOdds;

    public string Key => SourceKey;

    public bool IsConfigured => _config.HasKey;

    public IReadOnlyList<string> SupportedMarkets => _config.EffectiveMarkets;

    public IReadOnlyList<string> MarketsFor(string sportKey) => _config.MarketsFor(sportKey);

    /// <summary>
    /// The most events this pull may return — the job sets it from what the budget has left above
    /// its reserve; by default the configured per-pull cap.
    /// </summary>
    public int EventCeiling { get; set; } = int.MaxValue;

    /// <summary>The leagues the free plan carries that the desk prices. No UFC: it is not on the free plan (pricing page).</summary>
    public static bool Covers(string sportKey) => League(sportKey) is not null;

    /// <summary>The provider's league id for one of the desk's, or null where it is not offered here.</summary>
    public static string? League(string sportKey) => sportKey.ToLowerInvariant() switch
    {
        "nfl" => "NFL",
        "nba" => "NBA",
        "mlb" => "MLB",
        _ => null
    };

    /// <summary>
    /// The desk's key for one of the provider's bookmaker ids. Caesars is filed as
    /// <c>williamhill_us</c>, the key The Odds API files it under, so a Caesars price is one book on
    /// the desk whichever source brought it (and a paid Odds API key would extend the same row of
    /// prices rather than start a second "Caesars"). The rest take The Odds API's spelling where it
    /// differs; any other id is kept as sent.
    /// </summary>
    public static string CanonicalBook(string bookmakerId) => bookmakerId.ToLowerInvariant() switch
    {
        "caesars" => "williamhill_us",
        "unibet" => "unibet_us",
        "pointsbet" => "pointsbetus",
        var other => other
    };

    /// <summary>The full-game outcomes read, by the provider's oddID: market, and which side.</summary>
    private static readonly Dictionary<string, (string Market, Side Side)> Outcomes = new(StringComparer.Ordinal)
    {
        ["points-home-game-ml-home"] = (Markets.Moneyline, Side.Home),
        ["points-away-game-ml-away"] = (Markets.Moneyline, Side.Away),
        ["points-home-game-sp-home"] = (Markets.Spread, Side.Home),
        ["points-away-game-sp-away"] = (Markets.Spread, Side.Away),
        ["points-all-game-ou-over"] = (Markets.Total, Side.Over),
        ["points-all-game-ou-under"] = (Markets.Total, Side.Under),
    };

    private enum Side { Home, Away, Over, Under }

    /// <summary>The oddIDs to ask for, for the markets asked.</summary>
    public static IReadOnlyList<string> OddIds(IEnumerable<string> markets)
    {
        var wanted = markets.ToHashSet(StringComparer.Ordinal);
        return Outcomes.Where(o => wanted.Contains(o.Value.Market)).Select(o => o.Key).ToList();
    }

    /// <summary>One page's address, relative to <see cref="BaseUrl"/>. No key: that is a header.</summary>
    public static string EventsUrl(
        string league, IEnumerable<string> bookmakerIds, IEnumerable<string> oddIds,
        DateTimeOffset startsBefore, int limit, string? cursor)
        => "events"
           + $"?leagueID={Uri.EscapeDataString(league)}"
           + $"&bookmakerID={Uri.EscapeDataString(string.Join(',', bookmakerIds))}"
           + $"&oddID={Uri.EscapeDataString(string.Join(',', oddIds))}"
           + "&started=false"
           + "&oddsAvailable=true"
           + $"&startsBefore={Uri.EscapeDataString(TheOddsApiWire.IsoSeconds(startsBefore))}"
           + $"&limit={limit.ToString(CultureInfo.InvariantCulture)}"
           + (cursor is { Length: > 0 } ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");

    /// <summary>The provider pages at up to 100 events (v2).</summary>
    public const int MaxPageSize = 100;

    public async Task<OddsFetchResult> FetchSlateAsync(string sportKey, IReadOnlyList<string> markets, CancellationToken ct)
    {
        var apiKey = _config.HasKey ? _config.ApiKey! : throw new InvalidOperationException($"{SourceKey}: no API key configured.");

        if (League(sportKey) is not { } league)
            throw new InvalidOperationException($"{SourceKey}: {sportKey.ToUpperInvariant()} is not offered on SportsGameOdds' free plan.");

        var ceiling = Math.Max(0, Math.Min(EventCeiling, _config.MaxEventsPerPull));
        var books = _config.RequestedBooks.Select(b => b.ToLowerInvariant()).ToArray();
        var oddIds = OddIds(markets);

        var games = new List<CanonicalGame>();
        var odds = new List<CanonicalOdds>();

        if (ceiling == 0 || oddIds.Count == 0 || books.Length == 0)
            return new OddsFetchResult(games, odds, new FetchCost(Requests: 0));

        var capturedAt = DateTimeOffset.UtcNow;
        var startsBefore = capturedAt + _config.LookAhead;

        var requests = 0;
        var objects = 0;
        string? cursor = null;

        do
        {
            // A cancelled pull throws rather than hand back the pages it has: written whole or not at all.
            ct.ThrowIfCancellationRequested();

            if (requests > 0 && _config.PageDelay > TimeSpan.Zero)
                await Task.Delay(_config.PageDelay, ct);

            var limit = Math.Min(MaxPageSize, ceiling - objects);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                EventsUrl(league, books, oddIds, startsBefore, limit, cursor));
            request.Headers.Add("X-Api-Key", apiKey);

            using var response = await http.SendAsync(request, ct);
            requests++;

            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new OddsFetchFailedException(
                    $"{SourceKey}: {league} was refused ({(int)response.StatusCode} {Refusal(response.StatusCode)}){ErrorOf(body)}. Nothing was written.",
                    Cost(requests, objects));

            Page page;
            try
            {
                page = ReadPage(body, sportKey, markets, books, capturedAt);
            }
            catch (Exception ex) when (ex is not OddsFetchFailedException)
            {
                // Billed for what it returned, which is unknown; the limit asked is the honest ceiling.
                throw new OddsFetchFailedException(
                    $"{SourceKey}: {league} answered and could not be read ({ex.GetType().Name}: {ex.Message}). Nothing was written.",
                    Cost(requests, objects + limit), ex);
            }

            if (page.Error is { } error)
                throw new OddsFetchFailedException(
                    $"{SourceKey}: {league} answered with an error: {error}. Nothing was written.",
                    Cost(requests, objects));

            objects += page.Listed;
            games.AddRange(page.Games);
            odds.AddRange(page.Odds);

            // Every event listed and none readable is a changed payload, not a quiet day (ADR 0003).
            if (page.Listed > 0 && page.Games.Count == 0)
                throw new OddsFetchFailedException(
                    $"{SourceKey}: {league} listed {page.Listed} events and none could be read — possible upstream schema drift. Nothing was written.",
                    Cost(requests, objects));

            cursor = page.Listed > 0 ? page.NextCursor : null;
        }
        while (cursor is not null && objects < ceiling);

        logger.LogInformation(
            "{Source}: {Sport} -> {Games} games, {Odds} prices, {Objects} objects in {Requests} requests",
            SourceKey, sportKey, games.Count, odds.Count, objects, requests);

        return new OddsFetchResult(games, odds, Cost(requests, objects));
    }

    /// <summary>What a pull cost: its requests, and its objects as the run's credits. The provider reports no remainder.</summary>
    private static FetchCost Cost(int requests, int objects) => new(Requests: requests, Credits: objects, CreditsRemaining: null);

    private static string Refusal(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "the key was not accepted",
        HttpStatusCode.Forbidden => "the key's plan does not include this request",
        HttpStatusCode.TooManyRequests => "rate limited: the free plan allows 10 requests a minute",
        _ => status.ToString()
    };

    /// <summary>": the provider's own words" from an error envelope, or nothing.</summary>
    private static string ErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return TheOddsApiWire.ReadString(doc.RootElement, "error") is { Length: > 0 } error ? $": {error}" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>One page, read.</summary>
    /// <param name="Listed">Events the page returned — the objects it was billed.</param>
    /// <param name="Error">The envelope's error where <c>success</c> is false.</param>
    public sealed record Page(int Listed, IReadOnlyList<CanonicalGame> Games, IReadOnlyList<CanonicalOdds> Odds, string? NextCursor, string? Error);

    /// <summary>
    /// One page of <c>/events</c> as games and prices. Public and pure so the tests pin the wire on
    /// its own. Never throws on a field of the wrong type: it skips the event, outcome or book.
    /// </summary>
    public static Page ReadPage(
        string body, string sportKey, IReadOnlyList<string> markets, IReadOnlyCollection<string> books, DateTimeOffset capturedAt)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return new Page(0, [], [], null, "the response was not an object");

        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            return new Page(0, [], [], null, TheOddsApiWire.ReadString(root, "error") ?? "success was false");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new Page(0, [], [], null, null);

        var wanted = books.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var games = new List<CanonicalGame>();
        var odds = new List<CanonicalOdds>();

        foreach (var e in data.EnumerateArray())
            ReadEvent(e, sportKey, markets, wanted, capturedAt, games, odds);

        return new Page(data.GetArrayLength(), games, odds, TheOddsApiWire.ReadString(root, "nextCursor"), null);
    }

    private static void ReadEvent(
        JsonElement e, string sportKey, IReadOnlyList<string> markets, IReadOnlySet<string> books,
        DateTimeOffset capturedAt, List<CanonicalGame> games, List<CanonicalOdds> odds)
    {
        if (TheOddsApiWire.ReadString(e, "eventID") is not { Length: > 0 } id)
            return;

        if (!e.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Object
            || TeamName(teams, "home") is not { } home
            || TeamName(teams, "away") is not { } away)
            return;

        if (!e.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object
            || TheOddsApiWire.ReadTimestamp(status, "startsAt") is not { } startsAt)
            return;

        // Asked for games not started; a game the provider has since started, or called off, is not priced.
        if (True(status, "started") || True(status, "cancelled") || True(status, "ended"))
            return;

        // Sides by the desk's own table where it knows the club, as the schedule adapter sends them,
        // so a book's game lands on the club another provider created rather than beside it.
        games.Add(new CanonicalGame(id, sportKey, home, away, startsAt,
            Home: TheOddsApiTeams.Ref(sportKey, home),
            Away: TheOddsApiTeams.Ref(sportKey, away)));

        if (!e.TryGetProperty("odds", out var oddsMap) || oddsMap.ValueKind != JsonValueKind.Object)
            return;

        foreach (var entry in oddsMap.EnumerateObject())
        {
            if (!Outcomes.TryGetValue(entry.Name, out var outcome) || !markets.Contains(outcome.Market))
                continue;

            if (entry.Value.ValueKind != JsonValueKind.Object
                || !entry.Value.TryGetProperty("byBookmaker", out var byBook)
                || byBook.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var book in byBook.EnumerateObject())
            {
                if (!books.Contains(book.Name) || book.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var quote = book.Value;

                // A line the book has taken down is not a price.
                if (quote.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False)
                    continue;

                if (Number(quote, "odds") is not { } american || american != Math.Truncate(american) || american == 0)
                    continue;

                decimal? line = outcome.Market switch
                {
                    Markets.Spread => Number(quote, "spread"),
                    Markets.Total => Number(quote, "overUnder"),
                    _ => null
                };

                // A spread or total with no number is not a bet the board can price.
                if (outcome.Market != Markets.Moneyline && line is null)
                    continue;

                odds.Add(new CanonicalOdds(
                    SourceGameId: id,
                    Book: CanonicalBook(book.Name),
                    Market: outcome.Market,
                    // The board's sides: the team for moneyline and spread, as the event names it
                    // (renamed to the desk's team on ingest, IBookSidesByEvent); "over"/"under" for a total.
                    Outcome: outcome.Side switch
                    {
                        Side.Home => home,
                        Side.Away => away,
                        Side.Over => "over",
                        _ => "under"
                    },
                    Line: line,
                    PriceAmerican: (int)american,
                    CapturedAt: TheOddsApiWire.ReadTimestamp(quote, "lastUpdatedAt") ?? capturedAt));
            }
        }
    }

    /// <summary>A side's full name — <c>names.long</c>, else <c>names.medium</c> — or null.</summary>
    private static string? TeamName(JsonElement teams, string side)
    {
        if (!teams.TryGetProperty(side, out var team) || team.ValueKind != JsonValueKind.Object
            || !team.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Object)
            return null;

        var name = TheOddsApiWire.ReadString(names, "long") ?? TheOddsApiWire.ReadString(names, "medium");
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static bool True(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// A number the provider writes as a string ("-110", "+3.5") — or, should it send one, as a
    /// number. Null where it is missing, blank or not a number.
    /// </summary>
    public static decimal? Number(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDecimal(out var n) ? n : null;

        if (value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (string.Equals(text, "even", StringComparison.OrdinalIgnoreCase))
            return 100;

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
