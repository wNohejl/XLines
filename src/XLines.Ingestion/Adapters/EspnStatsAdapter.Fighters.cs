using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Adapters;
/// <summary>
/// ESPN's UFC fighters (ADR 0021): one fighter's athlete page, stat sheets, career rates and record,
/// read per fighter rather than per day (<see cref="IFighterSource"/>).
/// </summary>
public partial class EspnStatsAdapter
{
    /// <summary>
    /// One fighter in full: the athlete page (bio, record splits and every recorded fight), the
    /// per-fight stat sheets, the career rates, and the record's breakdown. Four requests; only
    /// the first is essential, and a failure on any other costs that part rather than the fighter.
    /// </summary>
    public async Task<FighterFetchResult> FetchFighterAsync(string sportKey, string sourceFighterId, CancellationToken ct)
    {
        if (MapPath(sportKey) is not { } path || path.Split('/') is not [var sport, _] || !FightSports.IsFight(sportKey))
            return new FighterFetchResult(null, new FetchCost(0));

        var id = Uri.EscapeDataString(sourceFighterId);
        var requests = 1;

        using var athlete = await GetJsonAsync($"{WebApi}{sport}/athletes/{id}?region=us&lang=en", ct);

        Dictionary<string, List<FighterStat>> sheets = [];
        List<FighterStat> rates = [];
        JsonDocument? records = null;

        try
        {
            requests++;
            using var stats = await GetJsonAsync($"{WebApi}{sport}/athletes/{id}/stats?region=us&lang=en", ct);
            sheets = ParseFightSheets(stats.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Source}: no fight sheets for fighter {Id}", SourceKey, sourceFighterId);
        }

        try
        {
            requests++;
            using var career = await GetJsonAsync($"{CoreApi}{sport}/athletes/{id}/statistics", ct);
            rates = ParseCareerRates(career.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Source}: no career rates for fighter {Id}", SourceKey, sourceFighterId);
        }

        try
        {
            requests++;
            records = await GetJsonAsync($"{CoreApi}{sport}/athletes/{id}/records", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Source}: no record breakdown for fighter {Id}", SourceKey, sourceFighterId);
        }

        using (records)
        {
            var fighter = ParseFighter(athlete.RootElement, sheets, rates, records?.RootElement);
            return new FighterFetchResult(fighter, new FetchCost(requests));
        }
    }

    /// <summary>The fighter from the athlete page, with the parts read from the other three requests folded in.</summary>
    internal static CanonicalFighter? ParseFighter(
        JsonElement root,
        IReadOnlyDictionary<string, List<FighterStat>> sheets,
        IReadOnlyList<FighterStat> rates,
        JsonElement? records)
    {
        if (!root.TryGetProperty("athlete", out var a) || JsonRead.String(a, "id") is not { } id)
            return null;

        var name = JsonRead.String(a, "displayName") ?? JsonRead.String(a, "fullName");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var record = new List<FighterStat>();
        string? wld = null;

        if (a.TryGetProperty("statsSummary", out var summary)
            && summary.TryGetProperty("statistics", out var summaryStats)
            && summaryStats.ValueKind == JsonValueKind.Array)
        {
            foreach (var stat in summaryStats.EnumerateArray())
            {
                if (JsonRead.String(stat, "name") is not { } key || JsonRead.String(stat, "displayValue") is not { } value)
                    continue;

                record.Add(new FighterStat(key, JsonRead.String(stat, "displayName") ?? key, value));

                if (key == "wins-losses-draws")
                    wld = value;
            }
        }

        if (records is { } r && r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("stats", out var splits) || splits.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var stat in splits.EnumerateArray())
                {
                    if (JsonRead.String(stat, "name") is { } key && JsonRead.String(stat, "displayValue") is { } value
                        && record.All(x => x.Key != key))
                        record.Add(new FighterStat(key, JsonRead.String(stat, "displayName") ?? key, value));
                }

                wld ??= JsonRead.String(item, "summary");
            }
        }

        return new CanonicalFighter(
            SourceFighterId: id,
            FullName: name.Trim(),
            Nickname: NonEmpty(JsonRead.String(a, "nickname")),
            Country: NonEmpty(JsonRead.String(a, "citizenship")),
            Association: a.TryGetProperty("association", out var gym) ? NonEmpty(JsonRead.String(gym, "name")) : null,
            Stance: a.TryGetProperty("stance", out var stance) ? NonEmpty(JsonRead.String(stance, "text")) : null,
            WeightClass: a.TryGetProperty("weightClass", out var wc) ? NonEmpty(JsonRead.String(wc, "text")) : null,
            HeightInches: ParseInches(JsonRead.String(a, "displayHeight")),
            ReachInches: ParseInches(JsonRead.String(a, "displayReach")),
            BornOn: DateOnly.TryParseExact(JsonRead.String(a, "displayDOB"), "M/d/yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var born) ? born : null,
            Record: wld,
            Career: new FighterCareer(rates, record),
            History: ParseFightHistory(root, sheets));
    }

    /// <summary>
    /// Every recorded fight, newest first, from the athlete page's event map — UFC and before —
    /// each with its stat sheet where ESPN kept one.
    /// </summary>
    internal static IReadOnlyList<FighterFight> ParseFightHistory(
        JsonElement root, IReadOnlyDictionary<string, List<FighterStat>> sheets)
    {
        if (!root.TryGetProperty("eventsMap", out var map) || map.ValueKind != JsonValueKind.Object)
            return [];

        // The athlete page orders the fights in "events"; the map is keyed but unordered.
        var order = root.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList()
            : map.EnumerateObject().Select(p => p.Name).ToList();

        var fights = new List<FighterFight>();

        foreach (var uid in order)
        {
            if (!map.TryGetProperty(uid, out var e))
                continue;

            var status = e.TryGetProperty("status", out var s) ? s : default;
            var result = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("result", out var res) ? res : default;

            fights.Add(new FighterFight(
                Id: CompetitionOf(uid) ?? uid,
                EventId: JsonRead.String(e, "id"),
                Event: JsonRead.String(e, "name"),
                Ufc: uid.Contains("~l:3321~", StringComparison.Ordinal),
                Date: JsonRead.String(e, "gameDate") is { } date
                      && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at.ToUniversalTime()
                    : null,
                OpponentId: e.TryGetProperty("opponent", out var opp) ? JsonRead.String(opp, "id") : null,
                Opponent: e.TryGetProperty("opponent", out var opp2) ? JsonRead.String(opp2, "displayName") : null,
                Result: NonEmpty(JsonRead.String(e, "gameResult")),
                Method: result.ValueKind == JsonValueKind.Object ? NonEmpty(JsonRead.String(result, "displayName")) : null,
                MethodDetail: result.ValueKind == JsonValueKind.Object ? NonEmpty(JsonRead.String(result, "description")) : null,
                Round: status.ValueKind == JsonValueKind.Object && status.TryGetProperty("period", out var p)
                       && p.ValueKind == JsonValueKind.Number && p.GetInt32() > 0
                    ? p.GetInt32()
                    : null,
                Clock: status.ValueKind == JsonValueKind.Object ? NonEmpty(JsonRead.String(status, "displayClock")) : null,
                Title: e.TryGetProperty("titleFight", out var t) && t.ValueKind == JsonValueKind.True,
                Stats: sheets.GetValueOrDefault(uid) ?? []));
        }

        return fights;
    }

    /// <summary>
    /// ESPN's per-fight stat sheet for every recorded fight, keyed by the fight's uid: striking,
    /// clinch and ground, all 36 columns, under ESPN's own labels. A fight ESPN kept no sheet for
    /// writes "-" in every column, and is left with none rather than a row of dashes.
    /// </summary>
    internal static Dictionary<string, List<FighterStat>> ParseFightSheets(JsonElement root)
    {
        var sheets = new Dictionary<string, List<FighterStat>>();

        if (!root.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
            return sheets;

        foreach (var category in categories.EnumerateArray())
        {
            var labels = Strings(category, "labels");
            var names = Strings(category, "displayNames");

            if (labels.Count == 0 || !category.TryGetProperty("statistics", out var rows) || rows.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var row in rows.EnumerateArray())
            {
                if (JsonRead.String(row, "uid") is not { } uid)
                    continue;

                var values = Strings(row, "stats");

                if (!sheets.TryGetValue(uid, out var sheet))
                    sheets[uid] = sheet = [];

                for (var i = 0; i < labels.Count && i < values.Count; i++)
                {
                    var value = values[i].Trim();
                    if (value is "" or "-")
                        continue;

                    var key = labels[i].Trim();
                    if (sheet.Any(x => x.Key == key))
                        continue;

                    sheet.Add(new FighterStat(key, i < names.Count ? names[i].Trim() : key, value));
                }
            }
        }

        foreach (var empty in sheets.Where(s => s.Value.Count == 0).Select(s => s.Key).ToList())
            sheets.Remove(empty);

        return sheets;
    }

    /// <summary>The career rates from the core statistics endpoint, under ESPN's names and as ESPN writes them.</summary>
    internal static List<FighterStat> ParseCareerRates(JsonElement root)
    {
        var rates = new List<FighterStat>();

        if (!root.TryGetProperty("splits", out var splits)
            || !splits.TryGetProperty("categories", out var categories)
            || categories.ValueKind != JsonValueKind.Array)
            return rates;

        foreach (var category in categories.EnumerateArray())
        {
            if (!category.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var stat in stats.EnumerateArray())
            {
                if (JsonRead.String(stat, "name") is { } key && JsonRead.String(stat, "displayValue") is { } value && rates.All(r => r.Key != key))
                    rates.Add(new FighterStat(key, JsonRead.String(stat, "displayName") ?? key, value));
            }
        }

        return rates;
    }

    /// <summary>"6' 4\"" as 76, "79\"" as 79, "72.5\"" as 72.5.</summary>
    internal static decimal? ParseInches(string? shown)
    {
        if (string.IsNullOrWhiteSpace(shown))
            return null;

        var match = FeetInches().Match(shown);

        if (match.Success)
        {
            var feet = match.Groups["feet"].Success ? decimal.Parse(match.Groups["feet"].Value, CultureInfo.InvariantCulture) : 0m;
            var inches = match.Groups["inches"].Success ? decimal.Parse(match.Groups["inches"].Value, CultureInfo.InvariantCulture) : 0m;
            var total = feet * 12 + inches;
            return total > 0 ? total : null;
        }

        return null;
    }

    [GeneratedRegex("""^\s*(?:(?<feet>\d+)\s*'\s*)?(?:(?<inches>\d+(?:\.\d+)?)\s*")?\s*$""")]
    private static partial Regex FeetInches();

    /// <summary>The competition id from a uid, "s:3301~l:3321~e:600041053~c:401619309" → "401619309".</summary>
    private static string? CompetitionOf(string uid)
    {
        var at = uid.LastIndexOf("~c:", StringComparison.Ordinal);
        return at < 0 ? null : uid[(at + 3)..];
    }

    private static List<string> Strings(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList()
            : [];

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max)
        => ColumnText.Fit(value, max);
}
