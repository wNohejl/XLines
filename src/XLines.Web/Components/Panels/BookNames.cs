namespace XLines.Web.Components.Panels;

/// <summary>
/// The name a book goes by, from the key the odds feed files it under.
///
/// The feed writes a bookmaker as a lowercase key — <c>draftkings</c>, <c>williamhill_us</c> —
/// and the windows that list books printed that key as the book's name. A key is for joining
/// rows; a person reads "DraftKings". The board's dense cells keep their two-letter monograms
/// (<c>PriceCell.Monogram</c>); anywhere a book is a row's label or named in a sentence, it goes
/// by this. A key with no entry here reads as its words capitalised, and one the feed already
/// cased ("DraftKings" from a provider that names its books) is left as written.
/// </summary>
public static class BookNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ballybet"] = "Bally Bet",
        ["bet365"] = "bet365",
        ["betfair_ex_us"] = "Betfair",
        ["betmgm"] = "BetMGM",
        ["betonlineag"] = "BetOnline.ag",
        ["betrivers"] = "BetRivers",
        ["betus"] = "BetUS",
        ["bovada"] = "Bovada",
        ["caesars"] = "Caesars",
        ["circa"] = "Circa",
        ["draftkings"] = "DraftKings",
        ["espn"] = "ESPN",
        // The feed kept the key when ESPN BET became theScore Bet.
        ["espnbet"] = "theScore Bet",
        ["fanatics"] = "Fanatics",
        ["fanduel"] = "FanDuel",
        ["hardrockbet"] = "Hard Rock Bet",
        ["lowvig"] = "LowVig.ag",
        ["mybookieag"] = "MyBookie.ag",
        ["pinnacle"] = "Pinnacle",
        ["pointsbet"] = "PointsBet",
        ["pointsbetus"] = "PointsBet",
        ["superbook"] = "SuperBook",
        ["unibet_us"] = "Unibet",
        ["williamhill_us"] = "Caesars",
        ["wynnbet"] = "WynnBET",
    };

    /// <summary>
    /// A book's two-letter mark, for the Board's dense cells. Colour means state on this desk, so a
    /// book is told apart typographically (ADR 0008), which only works if no two books share a
    /// mark: three keys start "BE" and two "BO", and the feed's two keys for one book
    /// (<c>pointsbet</c>/<c>pointsbetus</c>, <c>caesars</c>/<c>williamhill_us</c>) must read alike. So
    /// every book this class names has its mark written here, and an unknown key falls back to
    /// its first two letters.
    /// </summary>
    public static string Code(string book)
    {
        if (string.IsNullOrWhiteSpace(book))
            return "—";

        var key = book.Trim();
        return Codes.TryGetValue(key, out var code)
            ? code
            : key.Length >= 2 ? key[..2].ToUpperInvariant() : key.ToUpperInvariant();
    }

    /// <summary>The keys this class names, for the tests that hold every one to a name and a distinct mark.</summary>
    public static IReadOnlyCollection<string> Known => Names.Keys;

    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ballybet"] = "BB",
        ["bet365"] = "B3",
        ["betfair_ex_us"] = "BF",
        ["betmgm"] = "MG",
        ["betonlineag"] = "BO",
        ["betrivers"] = "BR",
        ["betus"] = "BU",
        ["bovada"] = "BV",
        ["caesars"] = "CZ",
        ["circa"] = "CI",
        ["draftkings"] = "DK",
        ["espn"] = "ES",
        ["espnbet"] = "TS",
        // ESPN's own closing lines name the book as it was then, cased; "ES" would read as ESPN.
        ["ESPN BET"] = "EB",
        ["fanatics"] = "FN",
        ["fanduel"] = "FD",
        ["hardrockbet"] = "HR",
        ["lowvig"] = "LV",
        ["mybookieag"] = "MB",
        ["pinnacle"] = "PN",
        ["pointsbet"] = "PB",
        ["pointsbetus"] = "PB",
        ["superbook"] = "SB",
        ["unibet_us"] = "UB",
        ["williamhill_us"] = "CZ",
        ["wynnbet"] = "WB",
    };

    /// <summary>
    /// The main sportsbooks, in the fixed order every list of books leads with: DraftKings,
    /// FanDuel, BetMGM, Bally Bet, Caesars — the owner's five, each with a free, permitted source
    /// (the first four on The Odds API's list, Caesars through SportsGameOdds; ADR 0033, addendum
    /// 2026-11-27). Fanatics and bet365 are not among them: no free source for either has been
    /// verified yet, so a "main" book with no price would only ever be absent.
    /// Each is the key the desk files it under; <see cref="MainRank"/> matches the aliases.
    /// </summary>
    public static readonly IReadOnlyList<string> Main = ["draftkings", "fanduel", "betmgm", "ballybet", "williamhill_us"];

    /// <summary>
    /// Every spelling of a main book, folded to lowercase letters and digits, with its place in
    /// <see cref="Main"/>: the feeds' keys (<c>williamhill_us</c> and <c>caesars</c> are one book),
    /// the names they go by, and the cased names ESPN's closing lines carry ("Caesars Sportsbook",
    /// "William Hill", "DraftKings").
    /// </summary>
    private static readonly Dictionary<string, int> MainSpellings = new(StringComparer.Ordinal)
    {
        ["draftkings"] = 0,
        ["draftkingssportsbook"] = 0,
        ["fanduel"] = 1,
        ["fanduelsportsbook"] = 1,
        ["betmgm"] = 2,
        ["betmgmsportsbook"] = 2,
        ["ballybet"] = 3,
        ["ballys"] = 3,
        ["ballysportsbook"] = 3,
        ["caesars"] = 4,
        ["caesarssportsbook"] = 4,
        ["williamhill"] = 4,
        ["williamhillus"] = 4,
    };

    private static string Fold(string text)
        => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// The book's place among the main sportsbooks (0 for DraftKings … 4 for Caesars), by its key,
    /// any alias of it, or the name it goes by; null for every other book.
    /// </summary>
    public static int? MainRank(string? book)
    {
        if (string.IsNullOrWhiteSpace(book))
            return null;

        if (MainSpellings.TryGetValue(Fold(book), out var rank))
            return rank;

        return MainSpellings.TryGetValue(Fold(Display(book)), out var byName) ? byName : null;
    }

    /// <summary>Whether the book is one of the main sportsbooks (<see cref="Main"/>).</summary>
    public static bool IsMain(string? book) => MainRank(book) is not null;

    /// <summary>
    /// Books as a picker lists them: the main sportsbooks in their fixed order, then the rest
    /// alphabetically by the name they go by. Duplicates (one book under two keys) are kept: a
    /// picker's values are the keys it was given.
    /// </summary>
    public static IReadOnlyList<string> MainFirst(IEnumerable<string> books)
        => books
            .OrderBy(b => MainRank(b) ?? int.MaxValue)
            .ThenBy(b => Display(b), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>"DraftKings" for <c>draftkings</c>; a key it does not know, in capitalised words.</summary>
    public static string Display(string? book)
    {
        if (string.IsNullOrWhiteSpace(book))
            return "—";

        if (Names.TryGetValue(book.Trim(), out var name))
            return name;

        // Already cased by whoever wrote it: their spelling is the book's own.
        if (book.Any(char.IsUpper))
            return book.Trim();

        return string.Join(' ', book.Trim()
            .Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
