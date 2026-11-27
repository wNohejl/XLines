using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Adapters;

/// <summary>
/// ESPN's UFC: cards, bouts and fighters (ADR 0021).
///
/// <para>
/// A team sport's day is a scoreboard and a summary per game. MMA has no summary — ESPN answers
/// 404 for it — and the scoreboard carries neither the method nor the order of a card. The fight
/// center does: one request returns the whole card with every bout's segment, place, result and
/// fight stats, and each fighter's bio as it stood that night. So a fight day is the scoreboard,
/// to learn which cards fall on it, and the fight center once per card.
/// </para>
///
/// <para>
/// Fighters are read separately (<see cref="IFighterSource"/>), per fighter rather than per day:
/// the athlete endpoints hold a whole career, and a career changes only when its fighter fights.
/// </para>
/// </summary>
public partial class EspnStatsAdapter
{
    /// <summary>The fight center and the athlete pages live on the web API, not the site API the client is based on.</summary>
    private const string WebApi = "https://site.web.api.espn.com/apis/common/v3/sports/";

    private const string CoreApi = "https://sports.core.api.espn.com/v2/sports/";

    /// <summary>How far past the asked-for day the slate pass looks for cards: the rest of the week.</summary>
    public const int SlateDaysAhead = 6;

    /// <summary>
    /// Every bout on the cards that fall on one day, read from the fight center.
    /// </summary>
    /// <param name="withResults">
    /// The results pass: fight stat lines for finished bouts and each one's closing line, as a
    /// team sport's box-score pass brings its box scores. The slate pass stops at the bouts.
    /// </param>
    private async Task<StatsFetchResult> FetchCardsAsync(
        string path, string sportKey, DateOnly date, bool withResults, CancellationToken ct)
    {
        // A card is billed weeks out and bet all week, and no book can put one on the desk (books
        // only attach to bouts ESPN holds), so the slate pass reads the week ahead in the same one
        // request. The results pass and the backfill walk read the day they are asked for.
        var dates = withResults ? $"{date:yyyyMMdd}" : $"{date:yyyyMMdd}-{date.AddDays(SlateDaysAhead):yyyyMMdd}";

        using var board = await GetJsonAsync($"{path}/scoreboard?dates={dates}", ct);
        var requests = 1;

        var games = new List<CanonicalGame>();
        var players = new List<CanonicalPlayer>();
        var stats = new List<CanonicalPlayerStat>();
        var lines = new List<CanonicalOdds>();
        var cards = 0;

        foreach (var card in ReadScoreboardCards(board.RootElement))
        {
            ct.ThrowIfCancellationRequested();
            cards++;

            List<CanonicalGame> bouts;

            try
            {
                using var center = await GetJsonAsync(
                    $"{WebApi}{path}/fightcenter/{card.EventId}?region=us&lang=en&contentorigin=espn", ct);
                requests++;

                bouts = ParseFightCenter(center.RootElement, sportKey, card.Rounds,
                    withResults ? players : null, withResults ? stats : null).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The scoreboard has already read the card once. Without the fight center a bout
                // loses its method and its place on the card, not its fighters or its result.
                logger.LogWarning(ex, "{Source}: fight center unavailable for card {EventId}; reading the scoreboard's bouts",
                    SourceKey, card.EventId);

                bouts = ParseScoreboardBouts(board.RootElement, card.EventId, sportKey).ToList();
            }

            games.AddRange(bouts);

            if (!withResults)
                continue;

            foreach (var bout in bouts.Where(b => b.Status == "final"))
            {
                try
                {
                    lines.AddRange(await FetchClosingLinesAsync(sportKey,
                        new ClosingLineRequest(bout.SourceGameId, bout.HomeTeamName, bout.AwayTeamName, bout.StartsAt, card.EventId), ct));
                    requests++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A line is a reference; its absence must not cost the card.
                    logger.LogDebug(ex, "{Source}: no closing odds for bout {GameId}", SourceKey, bout.SourceGameId);
                }
            }
        }

        logger.LogInformation("{Source}: {Sport} {Date} -> {Cards} cards, {Bouts} bouts",
            SourceKey, sportKey, date, cards, games.Count);

        return new StatsFetchResult(games, players, stats, new FetchCost(requests), lines);
    }

    /// <summary>
    /// Whether a card on ESPN's UFC scoreboard is a UFC event. ESPN files the promotion's
    /// development shows under the same league — Dana White's Contender Series, a tryout whose
    /// winners are offered contracts, and Road to UFC — and their fighters are not yet UFC
    /// fighters. The desk holds the UFC only.
    /// </summary>
    internal static bool IsUfcCard(string? name)
        => name is not null
           && !name.Contains("Contender Series", StringComparison.OrdinalIgnoreCase)
           && !name.StartsWith("Road to UFC", StringComparison.OrdinalIgnoreCase);

    /// <summary>A card on the scoreboard, and the scheduled rounds of each of its bouts, which only the scoreboard states.</summary>
    internal sealed record ScoreboardCard(string EventId, IReadOnlyDictionary<string, int> Rounds);

    internal static IEnumerable<ScoreboardCard> ReadScoreboardCards(JsonElement root)
    {
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var e in events.EnumerateArray())
        {
            if (JsonRead.String(e, "id") is not { } id || !IsUfcCard(JsonRead.String(e, "name")))
                continue;

            var rounds = new Dictionary<string, int>();

            if (e.TryGetProperty("competitions", out var comps) && comps.ValueKind == JsonValueKind.Array)
            {
                foreach (var comp in comps.EnumerateArray())
                {
                    if (JsonRead.String(comp, "id") is { } compId && ScheduledRoundsOf(comp) is { } n)
                        rounds[compId] = n;
                }
            }

            yield return new ScoreboardCard(id, rounds);
        }
    }

    /// <summary>
    /// Every bout on one card, from the fight center: the card's segments in order, each bout with
    /// its place, weight, distance, result and two fighters.
    /// </summary>
    /// <param name="rounds">Scheduled rounds by competition id, from the scoreboard; the fight center does not say.</param>
    /// <param name="players">When given, each fighter of a finished bout is added, with…</param>
    /// <param name="stats">…their fight stat line for that bout.</param>
    internal static IEnumerable<CanonicalGame> ParseFightCenter(
        JsonElement root,
        string sportKey,
        IReadOnlyDictionary<string, int>? rounds = null,
        List<CanonicalPlayer>? players = null,
        List<CanonicalPlayerStat>? stats = null)
    {
        if (!root.TryGetProperty("event", out var ev) || JsonRead.String(ev, "id") is not { } eventId)
            yield break;

        var card = new Card(
            eventId,
            JsonRead.String(ev, "name") ?? JsonRead.String(ev, "shortName") ?? $"Event {eventId}",
            JsonRead.String(ev, "shortName"),
            root.TryGetProperty("season", out var season) ? ReadSeason(season) : default);

        if (!root.TryGetProperty("cards", out var segments) || segments.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var segment in segments.EnumerateObject())
        {
            if (!segment.Value.TryGetProperty("competitions", out var comps) || comps.ValueKind != JsonValueKind.Array)
                continue;

            var segmentName = JsonRead.String(segment.Value, "displayName");

            foreach (var comp in comps.EnumerateArray())
            {
                if (ReadBout(comp, card, sportKey, segmentName, rounds, players, stats) is { } bout)
                    yield return bout;
            }
        }
    }

    /// <summary>
    /// The scoreboard's own reading of one card: fighters, weight, distance and result, without
    /// the method or the card order. The fallback when the fight center cannot be read.
    /// </summary>
    internal static IEnumerable<CanonicalGame> ParseScoreboardBouts(JsonElement root, string eventId, string sportKey)
    {
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var e in events.EnumerateArray())
        {
            if (JsonRead.String(e, "id") != eventId
                || !e.TryGetProperty("competitions", out var comps)
                || comps.ValueKind != JsonValueKind.Array)
                continue;

            var card = new Card(eventId, JsonRead.String(e, "name") ?? $"Event {eventId}", JsonRead.String(e, "shortName"), ReadSeason(e));

            foreach (var comp in comps.EnumerateArray())
            {
                if (ReadBout(comp, card, sportKey, null, null, null, null) is { } bout)
                    yield return bout;
            }
        }
    }

    private readonly record struct Card(string EventId, string Name, string? ShortName, (int? Year, SeasonType? Type) Season);

    private static (int? Year, SeasonType? Type) ReadSeason(JsonElement holder)
    {
        var season = holder.TryGetProperty("season", out var nested) ? nested : holder;

        int? year = season.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;

        // The fight center nests the type an object deeper than the scoreboard does.
        int? type = season.TryGetProperty("type", out var t)
            ? t.ValueKind == JsonValueKind.Number ? t.GetInt32()
              : t.ValueKind == JsonValueKind.Object && t.TryGetProperty("type", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32()
              : null
            : null;

        return (year, type switch
        {
            1 => SeasonType.Preseason,
            2 => SeasonType.Regular,
            3 => SeasonType.Postseason,
            _ => null
        });
    }

    /// <summary>One fighter's corner of a bout, read from either the fight center or the scoreboard.</summary>
    private sealed record Corner(
        string Id, string Name, string? Surname, int Order, bool Winner, string? Record, JsonElement? Stats);

    private static CanonicalGame? ReadBout(
        JsonElement comp,
        Card card,
        string sportKey,
        string? segmentName,
        IReadOnlyDictionary<string, int>? rounds,
        List<CanonicalPlayer>? players,
        List<CanonicalPlayerStat>? stats)
    {
        if (JsonRead.String(comp, "id") is not { } id
            || !comp.TryGetProperty("competitors", out var competitors)
            || competitors.ValueKind != JsonValueKind.Array)
            return null;

        var corners = competitors.EnumerateArray()
            .Select((c, index) => ReadCorner(c, index + 1))
            .OfType<Corner>()
            .OrderBy(c => c.Order)
            .ToList();

        if (corners.Count != 2)
            return null;

        // ESPN's order 1 is the red corner — the champion, or the favoured billing — and it is
        // also the side its odds call "home". Keeping the two the same is what pairs a close.
        var (red, blue) = (corners[0], corners[1]);

        var weightClass = comp.TryGetProperty("type", out var type)
            ? JsonRead.String(type, "text") ?? JsonRead.String(type, "abbreviation")
            : null;

        var note = JsonRead.String(comp, "note");

        var title = (comp.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array
                     && types.EnumerateArray().Any(t => JsonRead.String(t, "text")?.Contains("Title", StringComparison.OrdinalIgnoreCase) == true))
                    || note?.Contains("Title Fight", StringComparison.OrdinalIgnoreCase) == true;

        int? matchNumber = comp.TryGetProperty("matchNumber", out var mn) && mn.ValueKind == JsonValueKind.Number
            ? mn.GetInt32()
            : null;

        var scheduled = rounds?.GetValueOrDefault(id) is > 0 and var known ? known
            : ScheduledRoundsOf(comp) ?? (title || matchNumber == 1 ? 5 : 3);

        var status = ReadStatus(comp);
        var result = ReadResult(comp, status, red, blue);

        var startsAt = JsonRead.String(comp, "date") is { } raw
                       && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : (DateTimeOffset?)null;

        if (startsAt is null)
            return null;

        var bout = new CanonicalBout(
            EventId: card.EventId,
            EventName: card.Name,
            EventShortName: card.ShortName,
            CardSegment: comp.TryGetProperty("cardSegment", out var seg) ? JsonRead.String(seg, "description") ?? segmentName : segmentName,
            MatchNumber: matchNumber,
            WeightClass: weightClass,
            ScheduledRounds: scheduled,
            TitleFight: title,
            HomeRecord: red.Record,
            AwayRecord: blue.Record,
            Outcome: result.Outcome,
            Method: result.Method,
            MethodDetail: result.Detail,
            EndRound: result.Round,
            EndSeconds: result.Seconds);

        if (players is not null && stats is not null && status == "final")
        {
            foreach (var corner in new[] { red, blue })
            {
                if (FightStatLine(corner) is not { } line)
                    continue;

                if (!players.Any(p => p.SourcePlayerId == corner.Id))
                {
                    players.Add(new CanonicalPlayer(
                        corner.Id, sportKey, corner.Name, Truncate(weightClass, 32), corner.Id, corner.Name));
                }

                stats.Add(new CanonicalPlayerStat(corner.Id, id, line, corner.Id, corner.Name));
            }
        }

        return new CanonicalGame(
            SourceGameId: id,
            SportKey: sportKey,
            HomeTeamName: red.Name,
            AwayTeamName: blue.Name,
            StartsAt: startsAt.Value,
            Status: status,
            HomeScore: result.HomeScore,
            AwayScore: result.AwayScore,
            Home: FighterRef(red, weightClass),
            Away: FighterRef(blue, weightClass),
            SeasonYear: card.Season.Year,
            SeasonType: card.Season.Type,
            Bout: bout);
    }

    /// <summary>
    /// A fighter as a side (ADR 0021): their ESPN athlete id, their name, the surname as the
    /// abbreviation, and the weight class to tell them from another fighter of the same name.
    /// </summary>
    private static CanonicalTeamRef FighterRef(Corner corner, string? weightClass)
        => new(corner.Name, corner.Id, Truncate(corner.Surname, 16)) { Qualifier = weightClass };

    private static Corner? ReadCorner(JsonElement competitor, int position)
    {
        var athlete = competitor.TryGetProperty("athlete", out var a) ? a : default;

        var id = (athlete.ValueKind == JsonValueKind.Object ? JsonRead.String(athlete, "id") : null) ?? JsonRead.String(competitor, "id");
        var name = athlete.ValueKind == JsonValueKind.Object
            ? JsonRead.String(athlete, "displayName") ?? JsonRead.String(athlete, "fullName")
            : null;

        if (id is null || string.IsNullOrWhiteSpace(name))
            return null;

        var surname = athlete.ValueKind == JsonValueKind.Object ? JsonRead.String(athlete, "lastName") : null;
        surname ??= TeamNames.Short(new Team { Name = name }, name);

        int order = competitor.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : position;

        var winner = competitor.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.True;

        // The fight center states the record going in; the scoreboard lists it among records.
        var record = JsonRead.String(competitor, "displayRecord");
        if (record is null && competitor.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
            record = records.EnumerateArray().Select(r => JsonRead.String(r, "summary")).FirstOrDefault(s => s is not null);

        JsonElement? stats = competitor.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0
            ? s
            : null;

        return new Corner(id, name.Trim(), surname, order, winner, record, stats);
    }

    private readonly record struct BoutResult(
        BoutOutcome? Outcome, string? Method, string? Detail, int? Round, int? Seconds, int? HomeScore, int? AwayScore);

    /// <summary>
    /// How a bout ended. A finished bout with a winner is a win, and scores 1–0 to the winner so
    /// every reader of a record and a moneyline can read it as a score; with no winner it is a
    /// draw (0–0) or a no contest (no score, and its bets void). A bout not yet finished has only
    /// its round and clock, which a live one is worth showing.
    /// </summary>
    private static BoutResult ReadResult(JsonElement comp, string? status, Corner red, Corner blue)
    {
        if (!comp.TryGetProperty("status", out var st))
            return default;

        int? round = st.TryGetProperty("period", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() > 0
            ? p.GetInt32()
            : null;

        var seconds = ReadClock(st);

        var result = st.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        var method = result.ValueKind == JsonValueKind.Object ? NonEmpty(JsonRead.String(result, "displayName")) : null;
        var detail = result.ValueKind == JsonValueKind.Object
            ? NonEmpty(JsonRead.String(result, "displayDescription") ?? JsonRead.String(result, "description"))
            : null;

        if (status != "final")
            return new BoutResult(null, method, detail, round, seconds, null, null);

        var said = $"{(result.ValueKind == JsonValueKind.Object ? JsonRead.String(result, "name") : null)} {method}".ToLowerInvariant();

        if (red.Winner != blue.Winner)
            return new BoutResult(BoutOutcome.Win, method, detail, round, seconds, red.Winner ? 1 : 0, blue.Winner ? 1 : 0);

        if (said.Contains("no contest") || said.Contains("nocontest") || said.Contains("no-contest") || said.Contains("overturn"))
            return new BoutResult(BoutOutcome.NoContest, method, detail, round, seconds, null, null);

        // Final, nobody won, and a method on record: the judges could not separate them.
        if (method is not null || said.Contains("draw"))
            return new BoutResult(BoutOutcome.Draw, method ?? "Draw", detail, round, seconds, 0, 0);

        // Final with no winner and no result at all: ESPN has not said yet. Nothing is invented.
        return new BoutResult(null, null, null, round, seconds, null, null);
    }

    /// <summary>Seconds into the round: the fight center writes "194.0", the scoreboard 194.0, both write "3:14".</summary>
    private static int? ReadClock(JsonElement status)
    {
        if (status.TryGetProperty("clock", out var clock))
        {
            if (clock.ValueKind == JsonValueKind.Number)
                return (int)Math.Round(clock.GetDouble());

            if (clock.ValueKind == JsonValueKind.String
                && double.TryParse(clock.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && value > 0)
                return (int)Math.Round(value);
        }

        return JsonRead.String(status, "displayClock") is { } shown && ParseClock(shown) is { } fromText ? fromText : null;
    }

    /// <summary>"3:14" as 194 seconds. Null for anything that is not a clock.</summary>
    internal static int? ParseClock(string? shown)
        => shown?.Split(':') is [var m, var s]
           && int.TryParse(m, out var minutes) && int.TryParse(s, out var secs)
            ? minutes * 60 + secs
            : null;

    private static int? ScheduledRoundsOf(JsonElement comp)
        => comp.TryGetProperty("format", out var format)
           && format.TryGetProperty("regulation", out var regulation)
           && regulation.TryGetProperty("periods", out var periods)
           && periods.ValueKind == JsonValueKind.Number
            ? periods.GetInt32()
            : null;

    /// <summary>
    /// The fight center's per-bout stats as the flat line a box score stores, under the short
    /// labels a fight sheet uses. "24/30" stays "24/30": landed of attempted is how the sport
    /// writes it, and splitting it would invent a precision the column does not need.
    /// </summary>
    private static string? FightStatLine(Corner corner)
    {
        if (corner.Stats is not { } stats)
            return null;

        var line = new Dictionary<string, string>();

        foreach (var stat in stats.EnumerateArray())
        {
            var name = JsonRead.String(stat, "name");
            var value = JsonRead.String(stat, "displayValue");

            if (name is null || value is null)
                continue;

            line[FightStatKeys.GetValueOrDefault(name) ?? JsonRead.String(stat, "abbreviation") ?? name] = value;
        }

        return line.Count == 0 ? null : JsonSerializer.Serialize(line);
    }

    private static readonly Dictionary<string, string> FightStatKeys = new(StringComparer.Ordinal)
    {
        ["knockDowns"] = "KD",
        ["sigStrikes"] = "SIG",
        ["totalStrikes"] = "TOT",
        ["headStrikes"] = "HEAD",
        ["bodyStrikes"] = "BODY",
        ["legStrikes"] = "LEG",
        ["takedowns"] = "TD",
        ["submissions"] = "SUB",
        ["timeInControl"] = "CTRL"
    };
}
