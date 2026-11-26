namespace XLines.Ingestion.Adapters;

/// <summary>
/// What The Odds API says about its bookmakers that bears on what a configured list will buy:
/// which keys its free plan never returns, and which region each of the desk's usual books is
/// filed under. From the provider's bookmaker list
/// (<see href="https://the-odds-api.com/sports-odds-data/bookmaker-apis.html"/>, read 2026-11-26).
/// The provider can change either at any time; this is the desk's copy, written down so Ops can
/// say so before a pull rather than after one comes back without the book.
/// </summary>
public static class TheOddsApiBooks
{
    /// <summary>
    /// Keys the provider marks "Only available on paid subscriptions". Asked for on the free
    /// plan they come back absent — no error, no price — while still holding one of the ten
    /// slots a region unit names.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PaidOnly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["williamhill_us"] = "Caesars",
        ["fanatics"] = "Fanatics",
        ["courtside"] = "Courtside",
        ["rebet"] = "ReBet",
        ["bet365_au"] = "bet365 (Australia)",
        ["dabble_au"] = "Dabble (Australia)",
        ["bet99_ca_on"] = "BET99 (Ontario)",
    };

    /// <summary>
    /// The region each known key is filed under. Only <c>us</c> books survive the fallback to
    /// <c>regions=us</c> that a list longer than ten causes; the rest are lost from it.
    /// </summary>
    private static readonly Dictionary<string, string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pinnacle"] = "eu",
        ["draftkings"] = "us",
        ["fanduel"] = "us",
        ["betmgm"] = "us",
        ["betrivers"] = "us",
        ["bovada"] = "us",
        ["betonlineag"] = "us",
        ["lowvig"] = "us",
        ["mybookieag"] = "us",
        ["betus"] = "us",
        ["williamhill_us"] = "us",
        ["fanatics"] = "us",
        ["espnbet"] = "us2",
        ["hardrockbet"] = "us2",
        ["ballybet"] = "us2",
        ["courtside"] = "us2",
        ["rebet"] = "us2",
        ["bet365_au"] = "au",
        ["dabble_au"] = "au",
        ["bet99_ca_on"] = "ca",
    };

    /// <summary>The books one region unit names: ten (the provider's billing rule).</summary>
    public const int PerRegionUnit = 10;

    /// <summary>The configured books the free plan will not return, by display name, in configured order.</summary>
    public static IReadOnlyList<string> PaidOnlyIn(IEnumerable<string> books)
        => books.Where(PaidOnly.ContainsKey).Select(b => PaidOnly[b]).Distinct().ToList();

    /// <summary>
    /// The configured books a list longer than ten loses: anything not filed under <c>us</c>, and
    /// a key whose region the desk does not know (it may be anywhere). Empty for ten or fewer.
    /// </summary>
    public static IReadOnlyList<string> LostPastTen(IReadOnlyCollection<string> books)
        => books.Count <= PerRegionUnit
            ? []
            : books.Where(b => !Regions.TryGetValue(b, out var r) || r != "us").ToList();

    /// <summary>
    /// The plain sentences Ops shows beside the feed for the configured list, or none when it buys
    /// what it names: paid-only books named, and a list too long to send by name.
    /// </summary>
    public static IReadOnlyList<string> Notes(IReadOnlyCollection<string> books)
    {
        var notes = new List<string>();

        if (PaidOnlyIn(books) is { Count: > 0 } paid)
            notes.Add($"{Join(paid)} {(paid.Count == 1 ? "needs" : "need")} a paid plan at The Odds API; "
                      + $"on the free plan {(paid.Count == 1 ? "it returns" : "they return")} no prices, "
                      + "and still take a slot of the ten.");

        if (books.Count > PerRegionUnit)
        {
            var lost = LostPastTen(books);
            notes.Add($"{books.Count} books are configured and one region unit names ten, so the named list is ignored "
                      + "and every pull asks for regions=us at the same price"
                      + (lost.Count > 0 ? $": {Join(lost)} {(lost.Count == 1 ? "is" : "are")} not in that region and will not be priced." : ".")
                      + " Keep the list to ten.");
        }

        return notes;
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string Join(IReadOnlyList<string> names)
        => names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
