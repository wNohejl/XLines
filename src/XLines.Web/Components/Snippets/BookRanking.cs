using XLines.Core.Analytics;
using XLines.Data.CrossReference;
using XLines.Web.Components.Desk;
using XLines.Web.Components.Panels;

namespace XLines.Web.Components.Snippets;

/// <summary>
/// Which books a market's table shows, in what order, and which fold behind the More chip.
///
/// <para>
/// The same game is read three ways — the Board row's More bets, the Game window's market opened
/// in place, and the Line movement window — and each lists its books in one order:
/// <b>the main sportsbooks first</b>, in their fixed order (<see cref="BookNames.Main"/>:
/// DraftKings, FanDuel, BetMGM, Bally Bet, Caesars), then <b>every other book</b>, ranked among
/// themselves by the book holding the best price on either side first, then the lowest hold (the
/// cheapest book to bet into), a book quoting only one side (no hold) after those that quote both,
/// and the name last, so a tie reads the same way every time (ADR 0033, addendum 2026-11-27).
/// </para>
///
/// <para>
/// A table shows the main sportsbooks' rows and folds the rest behind a chip that says how many
/// (<see cref="MoreBooks"/>, <see cref="BookFold"/>). The fold never hides the best price
/// silently: where it sits behind the chip the count line says so. A market no main sportsbook
/// priced has nothing to fold behind and shows every row.
/// </para>
///
/// <para>
/// Only books with a price are ranked: a polled book that has not priced this market has no row,
/// and is not counted; a main sportsbook with no price is simply absent, never an invented row.
/// </para>
/// </summary>
public static class BookRanking
{
    /// <summary>The most books the price tooltip names (<c>PriceCell.TipBooks</c>): the cell's own and the main sportsbooks.</summary>
    public const int Top = 5;

    /// <summary>The order, in the words the tables' footnotes use.</summary>
    public const string OrderText = "main sportsbooks first";

    /// <summary>
    /// The books in ranked order: the main sportsbooks in their fixed order, then the rest — best
    /// on either side, then lowest hold, then one-sided books, then by name. Duplicates (a book on
    /// both sides) appear once.
    /// </summary>
    public static IReadOnlyList<string> Rank(IEnumerable<string> books, BestOffer? first, BestOffer? second)
        => books
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(b => BookNames.MainRank(b) ?? int.MaxValue)
            .ThenBy(b => IsBest(b, first) || IsBest(b, second) ? 0 : 1)
            .ThenBy(b => Hold(b, first, second) is null ? 1 : 0)
            .ThenBy(b => Hold(b, first, second) ?? 0)
            .ThenBy(b => BookNames.Display(b), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Every book priced on either side of the market, ranked.</summary>
    public static IReadOnlyList<string> Priced(BestOffer? first, BestOffer? second)
        => Rank((first?.Rungs ?? []).Select(r => r.Book).Concat((second?.Rungs ?? []).Select(r => r.Book)), first, second);

    /// <summary>
    /// Rows of any shape in ranked order, by the book each names: the Line movement window's
    /// rows and the Game window's moves are one side's, ranked on the whole market.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> rows, Func<T, string> book, BestOffer? first, BestOffer? second)
    {
        var list = rows.ToList();
        var rank = Rank(list.Select(book), first, second)
            .Select((b, i) => (b, i))
            .ToDictionary(x => x.b, x => x.i, StringComparer.OrdinalIgnoreCase);

        return list.OrderBy(r => rank.GetValueOrDefault(book(r), int.MaxValue)).ToList();
    }

    /// <summary>
    /// The rows a table shows: all of them expanded, otherwise the main sportsbooks' — or every row
    /// where no main sportsbook priced the market, since there is nothing to fold them behind.
    /// </summary>
    public static IReadOnlyList<T> Visible<T>(IReadOnlyList<T> ranked, Func<T, string> book, bool expanded)
    {
        if (expanded)
            return ranked;

        var main = ranked.Where(r => BookNames.IsMain(book(r))).ToList();
        return main.Count == 0 ? ranked : main;
    }

    /// <summary>The books a table shows (<see cref="Visible{T}"/> on the books themselves).</summary>
    public static IReadOnlyList<string> Visible(IReadOnlyList<string> ranked, bool expanded)
        => Visible(ranked, b => b, expanded);

    /// <summary>Whether the book holds the side's best price — the one price a table marks on it.</summary>
    public static bool IsBest(string book, BestOffer? side)
        => side is not null && string.Equals(side.Book, book, StringComparison.OrdinalIgnoreCase);

    /// <summary>The book's margin on the market, read from whichever side it quotes a pair on.</summary>
    public static double? Hold(string book, BestOffer? first, BestOffer? second)
        => Rung(book, first)?.Hold ?? Rung(book, second)?.Hold;

    /// <summary>The book's rung on a side, or null where it does not quote it.</summary>
    public static BookPrice? Rung(string book, BestOffer? side)
        => side?.Rungs.FirstOrDefault(r => string.Equals(r.Book, book, StringComparison.OrdinalIgnoreCase));

    /// <summary>The chip's words: "More · 5 books" (one: "More · 1 book"), or "Fewer" once they show.</summary>
    public static string KeyLabel(int hidden, bool expanded)
        => expanded ? "Fewer" : $"More · {hidden} {(hidden == 1 ? "book" : "books")}";

    /// <summary>What a market's table says when no main sportsbook priced it, and so nothing is folded.</summary>
    public const string NoMainText = "No main sportsbook priced this market";

    /// <summary>
    /// The sentence under a market's table. It leads with the direct count of the main sportsbooks
    /// (<see cref="MainCoverage"/>), then the books priced and, with the polled list, how many are
    /// polled — the same folded and open:
    /// <list type="bullet">
    /// <item>"3 of 5 main sportsbooks priced · 9 books priced · 10 polled.";</item>
    /// <item>folded, where a best price is behind the chip: "… · best price at Pinnacle, under More.";</item>
    /// <item>open: "…: main sportsbooks first.";</item>
    /// <item>no main sportsbook: "No main sportsbook priced this market (0 of 5) · 3 books priced · 10 polled.".</item>
    /// </list>
    /// </summary>
    public static string CountLine(BookFold fold, BookCoverage? coverage, MainCoverage main, bool expanded)
    {
        var lead = fold.NoMain ? $"{NoMainText} (0 of {MainCoverage.Total})" : main.CountText;
        var line = $"{lead} · {fold.Total} {(fold.Total == 1 ? "book" : "books")} priced"
                   + (coverage is null ? "" : $" · {coverage.Polled} polled");

        if (fold.Folds && expanded)
            return $"{line}: {OrderText}.";

        return fold.Folds && fold.BestText is { Length: > 0 } best ? $"{line} · {best}." : $"{line}.";
    }

    /// <summary>
    /// A price as every book table writes it: the number where the market has one, then the price —
    /// "+3.5 -110", "o47.5 -105", "-128" (the Line movement window's <c>Quote</c>).
    /// </summary>
    public static string Quote(BookPrice rung, string outcome)
        => (rung.Line is { } line ? OddsMath.FormatLine(line, outcome) + " " : "") + OddsMath.FormatPrice(rung.PriceAmerican);

    /// <summary>
    /// The fair line under a market, spelled one way on every surface: "Fair Chiefs -136 · Broncos
    /// +115 (Pinnacle, no-vig)", or "(4-book consensus, no-vig)". Empty where neither side has one.
    /// </summary>
    public static string FairText(BestOffer? first, BestOffer? second)
    {
        var sides = new[] { first, second }
            .Where(o => o?.Fair is not null)
            .Select(o => $"{o!.Outcome}{(o.Line is { } l ? " " + OddsMath.FormatLine(l, o.Outcome) : "")} {OddsMath.FormatPrice(o.Fair!.PriceAmerican)}")
            .ToList();

        if (sides.Count == 0)
            return string.Empty;

        var basis = (first?.Fair ?? second?.Fair)!;
        return $"Fair {string.Join(" · ", sides)} ({Checks.Basis(basis)}, no-vig)";
    }
}
