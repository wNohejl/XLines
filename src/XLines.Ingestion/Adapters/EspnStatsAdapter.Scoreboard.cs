using System.Globalization;
using System.Text.Json;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Adapters;

/// <summary>
/// ESPN's scoreboard, read: each event's teams, status, season, score and the live state of a game
/// being played.
/// </summary>
public partial class EspnStatsAdapter
{
    internal static IEnumerable<CanonicalGame> ParseScoreboard(JsonElement root, string sportKey, DateTimeOffset now)
    {
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var e in events.EnumerateArray())
        {
            var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (id is null)
                continue;

            if (IsPreseason(e))
                continue;

            if (!e.TryGetProperty("competitions", out var comps)
                || comps.ValueKind != JsonValueKind.Array
                || comps.GetArrayLength() == 0)
                continue;

            var comp = comps[0];

            if (IsExhibition(comp))
                continue;

            if (!comp.TryGetProperty("competitors", out var competitors)
                || competitors.ValueKind != JsonValueKind.Array)
                continue;

            CanonicalTeamRef? home = null, away = null;
            int? homeScore = null, awayScore = null;
            IReadOnlyList<int>? homeLines = null, awayLines = null;

            foreach (var c in competitors.EnumerateArray())
            {
                var isHome = c.TryGetProperty("homeAway", out var ha) && ha.GetString() == "home";

                // ESPN gives a stable team id and the official abbreviation right here, on a
                // response we were already fetching. Reading only the display name meant the
                // resolver matched teams by wording and invented abbreviations from it.
                var reference = ReadTeam(c);

                int? score = c.TryGetProperty("score", out var sc)
                             && int.TryParse(sc.GetString(), out var parsed)
                    ? parsed
                    : null;

                if (isHome)
                {
                    home = reference;
                    homeScore = score;
                    homeLines = ReadLinescore(c);
                }
                else
                {
                    away = reference;
                    awayScore = score;
                    awayLines = ReadLinescore(c);
                }
            }

            if (home is null || away is null)
                continue;

            var startsAt = e.TryGetProperty("date", out var d)
                           && DateTimeOffset.TryParse(d.GetString(), CultureInfo.InvariantCulture,
                               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                               out var parsedDate)
                ? parsedDate
                : now;

            yield return new CanonicalGame(
                SourceGameId: id,
                SportKey: sportKey,
                HomeTeamName: home.Name,
                AwayTeamName: away.Name,
                StartsAt: startsAt,
                Status: ReadStatus(comp),
                HomeScore: homeScore,
                AwayScore: awayScore,
                Home: home,
                Away: away,
                SeasonYear: ReadSeasonYear(e) is { } espnYear ? Core.Analytics.SeasonCalendar.FromEspnYear(sportKey, espnYear) : null,
                SeasonType: ReadSeasonType(e),
                Live: ReadLive(comp, homeLines, awayLines));
        }
    }

    /// <summary>
    /// The state of play the scoreboard carries beside the score (spec 2026-10-24 §4.1): clock,
    /// period, ESPN's short status line and — while the game is in play — the situation.
    ///
    /// <para>
    /// This used to be fetched every poll and thrown away, which is why the desk showed a score and
    /// the word "Live" while ESPN's own page showed the down and the last play. Every field is read
    /// defensively: ESPN documents none of them, a situation is present only while a game is in
    /// play, and a field in a shape we did not expect reads as null rather than costing the slate.
    /// </para>
    ///
    /// <para>
    /// Before the start ESPN still sends period 0 and a "0:00" clock. Those are placeholders, not a
    /// state of play, so a game that has not begun carries only its status line ("11/1 - 8:20 PM").
    /// </para>
    /// </summary>
    internal static CanonicalLive? ReadLive(
        JsonElement competition, IReadOnlyList<int>? homeLines, IReadOnlyList<int>? awayLines)
    {
        if (!competition.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
            return null;

        var type = JsonRead.Child(status, "type");
        var state = JsonRead.String(type, "state");
        var detail = Fit(JsonRead.String(type, "shortDetail"), Game.StatusDetailLength);

        if (state == "pre")
            return new CanonicalLive(null, null, null, detail, null, null);

        var clock = status.TryGetProperty("clock", out var cl) && cl.ValueKind == JsonValueKind.Number
                    && cl.TryGetDouble(out var seconds)
            ? (int?)Math.Round(seconds)
            : null;

        var live = new CanonicalLive(
            Period: JsonRead.Int(status, "period"),
            DisplayClock: Fit(JsonRead.String(status, "displayClock"), Game.DisplayClockLength),
            ClockSeconds: clock,
            StatusDetail: detail,
            HomeLinescore: homeLines,
            AwayLinescore: awayLines);

        // ESPN sends a situation only while the game is in play — measured null on the final.
        if (!competition.TryGetProperty("situation", out var s) || s.ValueKind != JsonValueKind.Object)
            return live;

        return live with
        {
            PossessionSourceTeamId = s.TryGetProperty("possession", out var p)
                ? p.ValueKind switch
                {
                    JsonValueKind.String => p.GetString(),
                    JsonValueKind.Number => p.GetRawText(),
                    _ => null
                }
                : null,
            Down = JsonRead.Int(s, "down"),
            Distance = JsonRead.Int(s, "distance"),
            YardLine = JsonRead.Int(s, "yardLine"),
            DownDistanceText = Fit(JsonRead.String(s, "downDistanceText"), Game.DownDistanceTextLength),
            PossessionText = Fit(JsonRead.String(s, "possessionText"), Game.PossessionTextLength),
            IsRedZone = JsonRead.Bool(s, "isRedZone"),
            HomeTimeouts = JsonRead.Int(s, "homeTimeouts"),
            AwayTimeouts = JsonRead.Int(s, "awayTimeouts"),
            LastPlay = s.TryGetProperty("lastPlay", out var lp) && lp.ValueKind == JsonValueKind.Object
                ? JsonRead.String(lp, "text")
                : null,
            SituationJson = ReadDiamond(s)
        };
    }

    /// <summary>
    /// Baseball's situation — the count, the outs, the runners and the matchup — as a small json
    /// bag, since none of it has a column. Only the keys ESPN sent in the shape expected are kept;
    /// null when none were, which is every football game.
    /// </summary>
    private static string? ReadDiamond(JsonElement situation)
    {
        var bag = new System.Text.Json.Nodes.JsonObject();

        foreach (var key in (string[])["balls", "strikes", "outs"])
            if (JsonRead.Int(situation, key) is { } n)
                bag[key] = n;

        foreach (var key in (string[])["onFirst", "onSecond", "onThird"])
            if (JsonRead.Bool(situation, key) is { } b)
                bag[key] = b;

        foreach (var key in (string[])["batter", "pitcher"])
            if (situation.TryGetProperty(key, out var who) && who.ValueKind == JsonValueKind.Object
                && who.TryGetProperty("athlete", out var athlete) && athlete.ValueKind == JsonValueKind.Object
                && JsonRead.String(athlete, "displayName") is { } name)
                bag[key] = name;

        return bag.Count == 0 ? null : bag.ToJsonString();
    }

    /// <summary>
    /// A competitor's points by period, first period first. Null when ESPN sent none — before the
    /// start — or any entry is unreadable: a linescore with a hole in it would put the third
    /// quarter's points under the second.
    /// </summary>
    private static IReadOnlyList<int>? ReadLinescore(JsonElement competitor)
    {
        if (!competitor.TryGetProperty("linescores", out var lines)
            || lines.ValueKind != JsonValueKind.Array
            || lines.GetArrayLength() == 0)
            return null;

        var points = new List<int>(lines.GetArrayLength());

        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object
                || !line.TryGetProperty("value", out var v)
                || v.ValueKind != JsonValueKind.Number
                || !v.TryGetDouble(out var value))
                return null;

            points.Add((int)Math.Round(value));
        }

        return points;
    }

    /// <summary>
    /// A string cut to its column's length. ESPN documents none of the state-of-play strings, and
    /// one longer than its column would fail the save that carries the whole slate. A cut never
    /// splits a surrogate pair.
    /// </summary>
    private static string? Fit(string? value, int length)
        => ColumnText.Fit(value, length);

    /// <summary>
    /// The season ESPN stamps on the event itself — year and type — read from the event rather
    /// than the payload root for the same reason <see cref="IsPreseason"/> is: the root describes
    /// the season the query fell in, the event describes the game. A February playoff carries
    /// the prior year, which is exactly what a season filter needs and a date alone cannot say.
    /// </summary>
    internal static int? ReadSeasonYear(JsonElement e)
        => e.TryGetProperty("season", out var season)
           && season.TryGetProperty("year", out var year)
           && year.ValueKind == JsonValueKind.Number
            ? year.GetInt32()
            : null;

    /// <summary>ESPN's season types: 1 preseason, 2 regular, 3 postseason. Anything else is unknown.</summary>
    internal static SeasonType? ReadSeasonType(JsonElement e)
        => e.TryGetProperty("season", out var season)
           && season.TryGetProperty("type", out var type)
           && type.ValueKind == JsonValueKind.Number
            ? type.GetInt32() switch
            {
                1 => SeasonType.Preseason,
                2 => SeasonType.Regular,
                3 => SeasonType.Postseason,
                _ => null
            }
            : null;

    /// <summary>
    /// A competitor's team identity: id and official abbreviation alongside the name.
    /// Returns null when there is no usable name, which is the one field resolution cannot
    /// do without.
    /// </summary>
    private static CanonicalTeamRef? ReadTeam(JsonElement competitor)
    {
        if (!competitor.TryGetProperty("team", out var team))
            return null;

        var name = team.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;

        if (string.IsNullOrWhiteSpace(name))
            return null;

        var id = team.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var abbrev = team.TryGetProperty("abbreviation", out var ab) ? ab.GetString() : null;

        return new CanonicalTeamRef(name, id, abbrev);
    }

    /// <summary>
    /// Whether this event is an exhibition rather than a game that counted.
    ///
    /// <para>
    /// The scoreboard returns spring training beside the regular season without being asked:
    /// 24 March 2026 is preseason, 25 March is opening day, and both answer the same query. Those
    /// games have provisional rosters, minor-leaguers batting in the ninth and results nobody
    /// records — so ingesting them puts losses on a team's record that never happened and stat
    /// lines in a player's form from games that did not count.
    /// </para>
    ///
    /// <para>
    /// Read from the <i>event</i> rather than the payload root, which reports the season the
    /// query falls in rather than the one the game belongs to — on a spring training date the
    /// root already says "Regular Season" while every event on it says preseason. Only type 1 is
    /// excluded: the postseason is type 3 and unambiguously counts.
    /// </para>
    /// </summary>
    private static bool IsPreseason(JsonElement e)
        => e.TryGetProperty("season", out var season)
           && season.TryGetProperty("type", out var type)
           && type.ValueKind == JsonValueKind.Number
           && type.GetInt32() == 1;

    /// <summary>
    /// Whether the competition is an exhibition wearing a regular-season label.
    ///
    /// <para>
    /// The All-Star Game is reported inside the regular season — season type 2, on an ordinary
    /// July date — so <see cref="IsPreseason"/> passes it through. It then resolves normally and
    /// invents two franchises to hold it: "American All-Stars" and "National All-Stars" appeared
    /// in the team list beside the thirty real ones, each with a record of one game.
    /// </para>
    ///
    /// <para>
    /// The competition carries the distinction that the season does not: <c>ALLSTAR</c> against
    /// <c>STD</c> for a game that counts. Named types are rejected rather than unnamed ones
    /// accepted, because the costs are not symmetric — letting an exhibition through adds a row
    /// that can be found and removed, while requiring a known type would silently drop every
    /// postseason game the first time ESPN labels one differently.
    /// </para>
    /// </summary>
    private static bool IsExhibition(JsonElement competition)
        => competition.TryGetProperty("type", out var type)
           && type.TryGetProperty("abbreviation", out var abbrev)
           && abbrev.GetString() is "ALLSTAR";

    private static string? ReadStatus(JsonElement competition)
    {
        if (!competition.TryGetProperty("status", out var status)
            || !status.TryGetProperty("type", out var type)
            || !type.TryGetProperty("name", out var name))
            return null;

        // ESPN's coarse state beside the name: pre, in or post. It is what catches the in-game
        // statuses nobody listed — a rain delay, the end of an inning or a quarter, overtime —
        // which used to fall through to "scheduled" and walk a game in progress back to before
        // its first pitch.
        var state = type.TryGetProperty("state", out var s) ? s.GetString() : null;

        return name.GetString() switch
        {
            // A forfeit is a final with a score. A suspended or abandoned game is not
            // coming back on this date: folding it into "scheduled" kept its date owed
            // and the results sweep re-walking every box score on it for weeks.
            "STATUS_FINAL" or "STATUS_FORFEIT" => "final",
            "STATUS_IN_PROGRESS" or "STATUS_HALFTIME" => "live",
            "STATUS_POSTPONED" or "STATUS_CANCELED" or "STATUS_CANCELLED"
                or "STATUS_SUSPENDED" or "STATUS_ABANDONED" => "postponed",
            _ when state == "in" => "live",
            _ => "scheduled"
        };
    }
}
