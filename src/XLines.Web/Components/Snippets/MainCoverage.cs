using XLines.Ingestion.Adapters;
using XLines.Ingestion.Services;
using XLines.Web.Components.Panels;

namespace XLines.Web.Components.Snippets;

/// <summary>Why a main sportsbook has no price, as far as the desk can actually know it.</summary>
public enum MainGap
{
    /// <summary>The polled list is unknown, so no reason is given: the book is only named.</summary>
    Unknown,

    /// <summary>Not in any live provider's configured list (Caesars with no second-source key).</summary>
    NotPolled,

    /// <summary>Listed only on The Odds API, which sells it on a paid plan alone (<see cref="TheOddsApiBooks.PaidOnly"/>).</summary>
    PaidOnly,

    /// <summary>Polled, but no price has arrived for what is in view (Bally Bet before a pull).</summary>
    PolledNotPriced,
}

/// <summary>A main sportsbook with no price, by full name, and why where that is known.</summary>
public sealed record MainMissing(string Book, MainGap Gap)
{
    /// <summary>"not polled", "needs a paid plan", "polled, not priced"; empty when unknown.</summary>
    public string Reason => Gap switch
    {
        MainGap.NotPolled => "not polled",
        MainGap.PaidOnly => "needs a paid plan",
        MainGap.PolledNotPriced => "polled, not priced",
        _ => string.Empty
    };

    /// <summary>"Caesars (not polled)", or the name alone when the reason is unknown.</summary>
    public string Text => Reason.Length == 0 ? Book : $"{Book} ({Reason})";
}

/// <summary>
/// The direct count of main-sportsbook coverage: how many of the five main sportsbooks
/// (<see cref="BookNames.Main"/>) priced what is in view, and which are missing and why — so the
/// gap between the books wanted and the books priced is a number on the desk (ADR 0033, addendum
/// 2026-11-27). A main book counts when it prices anything in view, matched through the aliases
/// (<see cref="BookNames.MainRank"/>). A reason is given only where the desk knows it from the
/// polled list and the provider's paid-only list; with the polled list unknown there is just the count.
/// </summary>
/// <param name="Priced">How many of the five priced.</param>
/// <param name="Missing">The rest, in the fixed order.</param>
public sealed record MainCoverage(int Priced, IReadOnlyList<MainMissing> Missing)
{
    /// <summary>How many main sportsbooks there are.</summary>
    public static int Total => BookNames.Main.Count;

    /// <summary>
    /// The coverage of <paramref name="priced"/>. <paramref name="polled"/> is the polled list
    /// (<c>PolledBooks</c>; null or empty: unknown); <paramref name="paidOnly"/> the polled keys that
    /// reach the desk only on a paid plan (<see cref="PaidOnlyOf"/>).
    /// </summary>
    public static MainCoverage Of(IEnumerable<string> priced, IReadOnlyCollection<string>? polled,
        IReadOnlyCollection<string>? paidOnly = null)
    {
        var ranks = priced.Select(BookNames.MainRank).OfType<int>().ToHashSet();
        var known = polled is { Count: > 0 };

        var missing = BookNames.Main
            .Select((key, rank) => (key, rank))
            .Where(m => !ranks.Contains(m.rank))
            .Select(m => new MainMissing(BookNames.Display(m.key), known ? Gap(m.rank, polled!, paidOnly) : MainGap.Unknown))
            .ToList();

        return new MainCoverage(Total - missing.Count, missing);
    }

    private static MainGap Gap(int rank, IReadOnlyCollection<string> polled, IReadOnlyCollection<string>? paidOnly)
    {
        var keys = polled.Where(p => BookNames.MainRank(p) == rank).ToList();

        if (keys.Count == 0)
            return MainGap.NotPolled;

        return paidOnly is { Count: > 0 } && keys.All(k => paidOnly.Contains(k, StringComparer.OrdinalIgnoreCase))
            ? MainGap.PaidOnly
            : MainGap.PolledNotPriced;
    }

    /// <summary>
    /// The polled keys that only The Odds API lists and that it sells on a paid plan alone, so on
    /// the free plan they never price: no other live provider lists the same book.
    /// </summary>
    public static IReadOnlyList<string> PaidOnlyOf(IEnumerable<OddsProviderState> feeds)
    {
        var live = feeds.Where(f => f.IsLive).ToList();

        return live
            .Where(f => f.Key == "the-odds-api")
            .SelectMany(f => f.Books)
            .Where(b => TheOddsApiBooks.PaidOnly.ContainsKey(b))
            .Where(b => !live.Any(f => f.Key != "the-odds-api" && f.Books.Any(o => BookCoverage.Same(o, b))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"3 of 5 main sportsbooks priced".</summary>
    public string CountText => $"{Priced} of {Total} main sportsbooks priced";

    /// <summary>"3 / 5", the Board tile's figure.</summary>
    public string Figure => $"{Priced} / {Total}";

    /// <summary>"Main sportsbooks missing: Bally Bet, Caesars." Empty when all five priced.</summary>
    public string MissingText => Missing.Count == 0 ? string.Empty
        : $"Main sportsbooks missing: {string.Join(", ", Missing.Select(m => m.Book))}.";

    /// <summary>"Missing: Bally Bet (polled, not priced), Caesars (not polled)". Empty when all five priced.</summary>
    public string ReasonsText => Missing.Count == 0 ? string.Empty
        : $"Missing: {string.Join(", ", Missing.Select(m => m.Text))}";

    /// <summary>"3 of 5 main sportsbooks priced (missing Bally Bet, Caesars)", or the count alone when none is missing.</summary>
    public string Phrase => Missing.Count == 0 ? CountText
        : $"{CountText} (missing {string.Join(", ", Missing.Select(m => m.Book))})";
}

/// <summary>One main sportsbook as Ops sees it: whether a live feed can price it now, and if not, why.</summary>
/// <param name="Book">Its full name.</param>
/// <param name="State">"polled", "paid plan only", "needs a key for the second source", "not polled".</param>
/// <param name="Obtainable">Whether a live feed lists it and can price it on the plan held.</param>
public sealed record MainSource(string Book, string State, bool Obtainable)
{
    /// <summary>"Caesars (needs a key for the second source)".</summary>
    public string Text => $"{Book} ({State})";

    /// <summary>Each of the five, in the fixed order, from the feeds Ops describes (<see cref="OddsFeedStatus"/>).</summary>
    public static IReadOnlyList<MainSource> Of(IEnumerable<OddsProviderState> feeds)
    {
        var list = feeds.ToList();
        var live = list.Where(f => f.IsLive).ToList();
        var paid = MainCoverage.PaidOnlyOf(list);

        return BookNames.Main.Select((key, rank) =>
        {
            var name = BookNames.Display(key);
            var keys = live.SelectMany(f => f.Books).Where(b => BookNames.MainRank(b) == rank).ToList();

            if (keys.Count > 0 && keys.Any(k => !paid.Contains(k, StringComparer.OrdinalIgnoreCase)))
                return new MainSource(name, "polled", true);

            if (keys.Count > 0)
                return new MainSource(name, "paid plan only", false);

            // Caesars' free route is the second source (SportsGameOdds), on the owner's own key.
            return rank == BookNames.MainRank("williamhill_us")
                ? new MainSource(name, "needs a key for the second source", false)
                : new MainSource(name, "not polled", false);
        }).ToList();
    }

    /// <summary>"Main sportsbooks: 4 of 5 obtainable now".</summary>
    public static string CountText(IReadOnlyList<MainSource> sources)
        => $"Main sportsbooks: {sources.Count(s => s.Obtainable)} of {sources.Count} obtainable now";

    /// <summary>The two books the owner wanted that are not among the five, and why — not a claim that it is impossible.</summary>
    public const string NotAmongText = "Fanatics and bet365 are not polled: no free source has been verified.";
}
