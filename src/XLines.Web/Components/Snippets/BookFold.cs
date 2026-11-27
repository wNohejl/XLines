using XLines.Data.CrossReference;
using XLines.Web.Components.Panels;

namespace XLines.Web.Components.Snippets;

/// <summary>
/// How a market's table folds: how many of its books are main sportsbooks (the rows it shows),
/// how many sit behind the More chip, and which best prices are among those.
///
/// <para>
/// The best price on a side is often at a book that is not a main sportsbook — Pinnacle,
/// BetOnline.ag — and the fold must never hide that silently, so <see cref="BestUnderMore"/> names
/// it for the count line ("best price at Pinnacle, under More") and the chip's dot. A best price
/// that is not in this table at all (the other side's, in a one-side table) is not behind the chip.
/// </para>
/// </summary>
/// <param name="Total">How many books priced the market: the table's rows.</param>
/// <param name="Main">How many of them are main sportsbooks (<see cref="BookNames.IsMain"/>).</param>
/// <param name="BestUnderMore">The books behind the chip holding a side's best price, by full name.</param>
public sealed record BookFold(int Total, int Main, IReadOnlyList<string> BestUnderMore)
{
    /// <summary>How many books sit behind the chip.</summary>
    public int More => Total - Main;

    /// <summary>Whether there is anything to fold: main rows to show and other rows to put behind the chip.</summary>
    public bool Folds => Main > 0 && More > 0;

    /// <summary>Whether books priced the market but no main sportsbook did, so every row shows.</summary>
    public bool NoMain => Total > 0 && Main == 0;

    /// <summary>The fold of a table whose rows are <paramref name="books"/>, against the market's two sides.</summary>
    public static BookFold Of(IEnumerable<string> books, BestOffer? first, BestOffer? second)
    {
        var list = books.Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var main = list.Count(BookNames.IsMain);

        IReadOnlyList<string> best = main == 0 ? [] : new[] { first, second }
            .Where(s => s is not null && !BookNames.IsMain(s.Book)
                        && list.Contains(s.Book, StringComparer.OrdinalIgnoreCase))
            .Select(s => BookNames.Display(s!.Book))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BookFold(list.Count, main, best);
    }

    /// <summary>
    /// "best price at Pinnacle, under More", or "best prices at Pinnacle and BetOnline.ag, under
    /// More"; empty when no best price is behind the chip.
    /// </summary>
    public string BestText => BestUnderMore.Count switch
    {
        0 => string.Empty,
        1 => $"best price at {BestUnderMore[0]}, under More",
        _ => $"best prices at {string.Join(" and ", BestUnderMore)}, under More"
    };
}
