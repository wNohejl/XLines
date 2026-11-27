using System.Globalization;
using System.Text.Json;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Adapters;

/// <summary>
/// ESPN's game summary, read: the game's context (venue, weather, officials, the predictor)
/// and its box score, one stat line a player.
/// </summary>
public partial class EspnStatsAdapter
{
    /// <summary>
    /// The circumstances of one game as its summary states them: the venue and its surface, the
    /// weather, the crowd and the officials from <c>gameInfo</c>, and ESPN's own pre-game chances
    /// from <c>predictor</c>.
    ///
    /// <para>
    /// What is in the document depends on when in the game's life it is read, and nothing here
    /// fills the gaps (ADR 0003). Measured: a final carries the venue, the attendance and the
    /// officials, and has dropped both the weather and the predictor; a scheduled game carries the
    /// venue, the predictor and — outdoors in the NFL — the weather, but no crowd and no officials.
    /// The weather states a temperature, a <c>conditionId</c> code and a precipitation figure; the
    /// condition in words is read only where ESPN writes it, as the scoreboard does, because a code
    /// table of ours would be words ESPN never said. No wind was seen on any summary, so none is read.
    /// </para>
    ///
    /// <para>
    /// The surface is read for football and baseball alone. ESPN sends <c>grass: false</c> for a
    /// basketball arena as well, and a hardwood court recorded as turf would be a fabrication with
    /// the provider's name on it. Whether the venue is indoor is not in the summary at all — it is
    /// on the scoreboard's venue — so it is read here only should ESPN ever add it.
    /// </para>
    ///
    /// <para>
    /// The predictor's <c>gameProjection</c> is a string ("94.8") and is kept as the percent it is,
    /// not divided down to a fraction (see <see cref="CanonicalGameContext.HomeWinPercent"/>).
    /// </para>
    /// </summary>
    /// <returns>The context, or null where the summary states none of it.</returns>
    internal static CanonicalGameContext? ParseGameContext(JsonElement root, string sportKey, string sourceGameId)
    {
        var info = JsonRead.Child(root, "gameInfo");
        var venue = JsonRead.Child(info, "venue");
        var address = JsonRead.Child(venue, "address");
        var weather = JsonRead.Child(info, "weather");

        var predictor = JsonRead.Child(root, "predictor");

        // Grass or not is a field's question; see the remarks for the arena that answers it anyway.
        var path = EspnLeagues.PathFor(sportKey);
        var playedOnAField = path is not null
                             && (path.StartsWith("football/", StringComparison.Ordinal)
                                 || path.StartsWith("baseball/", StringComparison.Ordinal));
        var grass = playedOnAField ? JsonRead.Bool(venue, "grass") : null;

        var context = new CanonicalGameContext(sourceGameId)
        {
            VenueName = JsonRead.Words(venue, "fullName"),
            VenueCity = JsonRead.Words(address, "city"),
            VenueState = JsonRead.Words(address, "state"),
            Indoor = JsonRead.Bool(venue, "indoor"),
            Surface = grass switch
            {
                true => GameSurfaces.Grass,
                false => GameSurfaces.Turf,
                null => null
            },
            WeatherCondition = JsonRead.Words(weather, "displayValue") ?? JsonRead.Words(weather, "condition"),
            TemperatureF = WholeNumber(weather, "temperature"),
            PrecipitationPercent = WholeNumber(weather, "precipitation"),

            // ESPN writes nought where it has no count, before a game and for some it never had;
            // a crowd of nobody is not a figure it measured.
            Attendance = WholeNumber(info, "attendance") is > 0 and var crowd ? crowd : null,
            Officials = ReadOfficials(info),
            HomeWinPercent = Number(JsonRead.Child(predictor, "homeTeam"), "gameProjection"),
            AwayWinPercent = Number(JsonRead.Child(predictor, "awayTeam"), "gameProjection"),
            MatchupQuality = Number(predictor, "matchupQuality")
        };

        // A context that states nothing is not news to anyone. The officials count: a summary that
        // names the crew and nothing else is still worth the write.
        return context.StatesAnything ? context : null;
    }

    /// <summary>
    /// The officials as <c>gameInfo</c> lists them. The NFL and NBA give a <c>fullName</c>, MLB only a
    /// <c>displayName</c> — some with a double space in it ("Nate  Tomlinson"), which is collapsed so
    /// one umpire is not two people by spacing. The role is the position's display name ("Referee",
    /// "Home Plate Umpire"); the order is ESPN's own. Null where nobody is named.
    /// </summary>
    private static IReadOnlyList<CanonicalOfficial>? ReadOfficials(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("officials", out var officials)
            || officials.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<CanonicalOfficial>();

        foreach (var official in officials.EnumerateArray())
        {
            if (official.ValueKind != JsonValueKind.Object
                || (JsonRead.Words(official, "fullName") ?? JsonRead.Words(official, "displayName")) is not { } name)
                continue;

            var position = JsonRead.Child(official, "position");

            list.Add(new CanonicalOfficial(
                string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                JsonRead.Words(position, "displayName") ?? JsonRead.Words(position, "name"),
                JsonRead.Int(official, "order")));
        }

        return list.Count == 0 ? null : list;
    }

    /// <summary>
    /// A figure ESPN may write as a number (<c>65</c>) or as a string (<c>"94.8"</c>) — the predictor
    /// does the second. Null where it is neither.
    /// </summary>
    private static decimal? Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    /// <summary>
    /// <see cref="Number"/> rounded to a whole one, for degrees, percents and heads. Kept here rather
    /// than in <see cref="JsonRead"/> because it is not MLB's whole number: ESPN writes figures meant
    /// whole with a decimal in them and this rounds "82.5" up, where MLB's reader refuses it.
    /// </summary>
    private static int? WholeNumber(JsonElement parent, string name)
        => Number(parent, name) is { } n && n is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Round(n, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>
    /// Box-score shapes differ per sport, so the per-player stat line is kept as jsonb
    /// rather than forced into a fixed column set that would only fit one sport.
    ///
    /// <para>
    /// ESPN reports a player once per <i>category</i> — a batter appears under batting and
    /// again under fielding, a quarterback under passing and rushing. Storage allows one row
    /// per player per game per source (<c>ux_player_game_stat</c>), so categories are merged
    /// into a single line here rather than emitted as several. Emitting several put two
    /// inserts with the same key into one transaction, and the day failed on a duplicate-key
    /// violation the moment a player did two things in a game.
    /// </para>
    ///
    /// <para>
    /// Merging is only safe if collisions are handled, because categories reuse short labels —
    /// <c>H</c> is hits under batting and hits allowed under pitching. Keys are therefore kept
    /// plain until two groups disagree about one, at which point <i>both</i> are qualified with
    /// their group (<c>batting.H</c>, <c>pitching.H</c>). Qualifying everything unconditionally
    /// was the first attempt and was worse: ESPN leaves the group unnamed for some sports —
    /// MLB among them — so it produced no prefix exactly where the collisions are, and ugly
    /// ones everywhere else.
    /// </para>
    ///
    /// <para>
    /// The line stays a flat string map, which is what the Players panel deserialises and
    /// derives its columns from.
    /// </para>
    /// </summary>
    internal static void ParseBoxScore(
        JsonElement root,
        string sportKey,
        string sourceGameId,
        List<CanonicalPlayer> players,
        List<CanonicalPlayerStat> stats)
    {
        if (!root.TryGetProperty("boxscore", out var boxscore)
            || !boxscore.TryGetProperty("players", out var teamGroups)
            || teamGroups.ValueKind != JsonValueKind.Array)
            return;

        // One accumulating line per player, for the duration of this game.
        var lines = new Dictionary<string, PlayerLine>();
        var groupOrdinal = 0;

        // The side each player's line was listed under. The box score is grouped by team, so
        // this is what the player played for in this game — which the player list above cannot
        // say, because it is deduplicated across a whole day's payload.
        var sides = new Dictionary<string, (string? Id, string? Name)>();

        foreach (var teamGroup in teamGroups.EnumerateArray())
        {
            var teamName = teamGroup.TryGetProperty("team", out var team)
                           && team.TryGetProperty("displayName", out var dn)
                ? dn.GetString()
                : null;

            var teamId = teamGroup.TryGetProperty("team", out var t)
                         && t.TryGetProperty("id", out var tid)
                ? tid.GetString()
                : null;

            if (!teamGroup.TryGetProperty("statistics", out var statGroups)
                || statGroups.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var statGroup in statGroups.EnumerateArray())
            {
                var labels = statGroup.TryGetProperty("labels", out var l)
                             && l.ValueKind == JsonValueKind.Array
                    ? l.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray()
                    : [];

                var category = statGroup.TryGetProperty("name", out var cat) ? cat.GetString() : null;

                // ESPN names the group for some sports and not others — NHL gives "forwards"
                // and "goalies", MLB gives nothing at all. An unnamed group still needs an
                // identity, because it is what a collision gets qualified by; it just should
                // not be reported as a category the operator can read anything into.
                var named = !string.IsNullOrWhiteSpace(category);
                var groupLabel = named ? category! : $"g{groupOrdinal}";
                groupOrdinal++;

                if (!statGroup.TryGetProperty("athletes", out var athletes)
                    || athletes.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var athlete in athletes.EnumerateArray())
                {
                    if (!athlete.TryGetProperty("athlete", out var a))
                        continue;

                    var playerId = a.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                    var fullName = a.TryGetProperty("displayName", out var pn) ? pn.GetString() : null;

                    if (playerId is null || fullName is null)
                        continue;

                    // ESPN lists the club itself among some groups' athletes (team fumbles and
                    // the like) under a negative id and the name " Team". That is not a person,
                    // and taking it as one put a "Team" on a roster with a stat line.
                    if (playerId.StartsWith('-'))
                        continue;

                    var position = a.TryGetProperty("position", out var pos)
                                   && pos.TryGetProperty("abbreviation", out var pa)
                        ? pa.GetString()
                        : null;

                    if (!players.Any(p => p.SourcePlayerId == playerId))
                    {
                        players.Add(new CanonicalPlayer(
                            playerId, sportKey, fullName, position, teamId, teamName));
                    }

                    var values = athlete.TryGetProperty("stats", out var s)
                                 && s.ValueKind == JsonValueKind.Array
                        ? s.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray()
                        : [];

                    if (values.Length == 0)
                        continue;

                    if (!lines.TryGetValue(playerId, out var line))
                        lines[playerId] = line = new PlayerLine();

                    sides.TryAdd(playerId, (teamId, teamName));

                    for (var i = 0; i < values.Length && i < labels.Length; i++)
                        line.Merge(groupLabel, labels[i], values[i], named);
                }
            }
        }

        foreach (var (playerId, line) in lines)
        {
            // "category" rather than "categories": the Players panel already treats that key
            // as metadata and keeps it out of the derived column set. Omitted entirely when
            // the provider named no groups, rather than recorded as a row of placeholders.
            if (line.Categories.Count > 0)
                line.Values["category"] = string.Join(',', line.Categories);

            var side = sides.GetValueOrDefault(playerId);

            stats.Add(new CanonicalPlayerStat(
                playerId, sourceGameId, JsonSerializer.Serialize(line.Values), side.Id, side.Name));
        }
    }

    /// <summary>
    /// A player's stat line, assembled across however many groups the provider reports them in.
    ///
    /// Keys stay plain until two groups disagree about one; then both sides are qualified with
    /// their group name so neither is lost. See <see cref="ParseBoxScore"/> for why that is
    /// on-demand rather than always.
    /// </summary>
    private sealed class PlayerLine
    {
        /// <summary>Which group each unqualified key came from.</summary>
        private readonly Dictionary<string, string> _origin = [];

        /// <summary>Keys that have already collided; every later group qualifies them too.</summary>
        private readonly HashSet<string> _qualified = [];

        public Dictionary<string, string> Values { get; } = [];

        /// <summary>Named groups only, ordered and de-duplicated.</summary>
        public SortedSet<string> Categories { get; } = [];

        public void Merge(string group, string key, string value, bool named)
        {
            if (named)
                Categories.Add(group);

            if (_qualified.Contains(key))
            {
                Values[$"{group}.{key}"] = value;
                return;
            }

            if (!_origin.TryGetValue(key, out var owner))
            {
                Values[key] = value;
                _origin[key] = group;
                return;
            }

            // The same group restating a value: nothing is lost by letting it stand.
            if (owner == group)
            {
                Values[key] = value;
                return;
            }

            // A second group reporting the key. Qualify the incumbent as well as the newcomer,
            // so the line never contains a bare key whose meaning depends on which group won —
            // even when the two agree: a back with 15 rushing and 15 receiving yards was stored
            // as one bare YDS, and the back's line had no rushing yards to show.
            Values[$"{owner}.{key}"] = Values[key];
            Values.Remove(key);
            _origin.Remove(key);
            _qualified.Add(key);

            Values[$"{group}.{key}"] = value;
        }
    }
}
