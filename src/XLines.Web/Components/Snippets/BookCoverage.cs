using XLines.Web.Components.Panels;

namespace XLines.Web.Components.Snippets;

/// <summary>
/// How a market's priced books stand against the books the desk polls, so every polled book is
/// accounted for wherever a market is read book by book.
///
/// <para>
/// A polled book with no price for a market has no row, and before this nothing said it was
/// absent: a game priced by four of ten books read as a four-book market. The tables now count
/// priced of polled and name the absent books in one footnote line, by their full names; the
/// price tooltip counts them; nothing here ever draws a row or a price for a book that has none.
/// </para>
///
/// <para>
/// A book matches a polled one by its key, ignoring case, or by the name it goes by
/// (<see cref="BookNames.Display"/>), so the feed's two keys for one book (<c>caesars</c> and
/// <c>williamhill_us</c>) are one book. A priced book that is not polled — ESPN's closing line, a
/// book since dropped from the list — keeps its row and is simply not counted as polled.
/// </para>
/// </summary>
/// <param name="Priced">How many books priced the market, polled or not: the table's rows.</param>
/// <param name="Polled">How many books the desk polls.</param>
/// <param name="Absent">The polled books with no price here, by full name, in the polled list's order.</param>
public sealed record BookCoverage(int Priced, int Polled, IReadOnlyList<string> Absent)
{
    /// <summary>How many of the polled books priced the market.</summary>
    public int PolledPriced => Polled - Absent.Count;

    /// <summary>Whether every polled book priced the market.</summary>
    public bool Complete => Absent.Count == 0;

    /// <summary>
    /// The coverage of <paramref name="priced"/> against <paramref name="polled"/>; null when the
    /// polled list is unknown (null or empty), so a caller without one draws exactly as before.
    /// </summary>
    public static BookCoverage? Of(IEnumerable<string> priced, IReadOnlyCollection<string>? polled)
    {
        if (polled is not { Count: > 0 })
            return null;

        var books = priced.Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // One book once, however many keys the list files it under.
        var list = polled.Where(b => !string.IsNullOrWhiteSpace(b))
            .DistinctBy(b => BookNames.Display(b), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
            return null;

        var absent = list
            .Where(p => !books.Any(b => Same(b, p)))
            .Select(p => BookNames.Display(p))
            .ToList();

        return new BookCoverage(books.Count, list.Count, absent);
    }

    /// <summary>Every book priced on any side of any of a game's markets.</summary>
    public static IEnumerable<string> Books(XLines.Core.Analytics.BoardRow row)
        => new[] { row.Moneyline, row.Spread, row.Total }
            .SelectMany(m => new[] { m.First, m.Second })
            .SelectMany(o => o?.Rungs ?? [])
            .Select(r => r.Book);

    /// <summary>Whether two keys name one book: the same key, or the same name.</summary>
    public static bool Same(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)
           || string.Equals(BookNames.Display(a), BookNames.Display(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>"10 books polled".</summary>
    public string PolledText => $"{Polled} {(Polled == 1 ? "book" : "books")} polled";

    /// <summary>"Not priced here: Pinnacle, Bovada." Empty when every polled book priced it.</summary>
    public string AbsentText => Complete ? string.Empty : $"Not priced here: {string.Join(", ", Absent)}.";

    /// <summary>"2 of 10 polled books not priced", for the price tooltip. Empty when none is missing.</summary>
    public string UnpricedText => Complete ? string.Empty : $"{Absent.Count} of {Polled} polled books not priced";
}
