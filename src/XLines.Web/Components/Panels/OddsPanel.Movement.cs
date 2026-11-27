using Microsoft.AspNetCore.Components;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.Changes;
using XLines.Data.CrossReference;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Desk.Windowing;
using XLines.Web.Components.Desk;
using XLines.Web.Services;
using XLines.Web.Windowing;

namespace XLines.Web.Components.Panels;

public partial class OddsPanel
{
    // ---- The movement, as data -------------------------------------------------------------
    // Static and free of the database, so the arithmetic a reader trusts — what counts as a
    // move, which way it went, how far — is tested on its own (OddsMovementTests).

    /// <summary>
    /// One book's prices for the side in focus, open to now.
    ///
    /// <para>
    /// <see cref="Moves"/> is every observation after the first: prices are written only when
    /// they change, so each later row is a move and the first is where the book opened.
    /// <see cref="Trend"/> is what the sparkline draws, with <see cref="Positions"/> placing
    /// each value on the time axis the whole table shares; it ends with the last value held to
    /// the right edge, because a price written on change still stands until the next one.
    /// </para>
    /// </summary>
    public sealed record MovementRow(
        string Book,
        OddsSnapshot Open,
        OddsSnapshot Now,
        int Moves,
        bool TrendsLine,
        IReadOnlyList<double> Trend,
        IReadOnlyList<double>? Positions)
    {
        /// <summary>The price's change in cents, now less open. Null where either reading is broken.</summary>
        public int? PriceCents => Cents(Now.PriceAmerican) - Cents(Open.PriceAmerican);

        /// <summary>The line's change, now less open. Null on a moneyline.</summary>
        public decimal? LineChange => Now.Line - Open.Line;

        /// <summary>When the price last changed; null for a book seen once, which has not moved.</summary>
        public DateTimeOffset? LastMoved => Moves > 0 ? Now.CapturedAt : null;

        /// <summary>Whether open and now differ. A book can move and come back, which is not a move.</summary>
        public bool Moved => (PriceCents ?? 0) != 0 || (LineChange ?? 0) != 0;
    }

    /// <summary>
    /// An American price on a scale with no hole in it, in cents: +105 is 5, -105 is -5, and
    /// the ten cents between them are ten here rather than the 210 their numbers differ by.
    ///
    /// <para>
    /// American odds have nothing between -100 and +100, so subtracting them raw makes a move
    /// through even money look twenty times its size, and a sparkline of them draws a cliff.
    /// This keeps the order the numbers are written in — -120 below -110 below +105 — so the
    /// arrow and the sparkline go the way the printed number did, and closes the gap, so the
    /// distance is the one a bettor means by "ten cents". Zero is not a price (see
    /// <see cref="Implied"/>), so it has no place on the scale.
    /// </para>
    /// </summary>
    public static int? Cents(int american) => american switch
    {
        0 => null,
        > 0 => american - 100,
        _ => american + 100
    };

    /// <summary>
    /// A market's outcomes in the order the desk reads them: away before home, as the
    /// matchup reads ("Cowboys at Eagles"), and Over before Under. Anything else follows,
    /// alphabetically, so an outcome the provider named differently is offered, not lost.
    /// </summary>
    public static IReadOnlyList<string> Sides(IEnumerable<string> outcomes, string market, string? away, string? home)
    {
        static bool Is(string outcome, string? name)
            => name is not null && string.Equals(outcome, name, StringComparison.OrdinalIgnoreCase);

        int Order(string outcome) => market == Markets.Total
            ? Is(outcome, "over") ? 0 : Is(outcome, "under") ? 1 : 2
            : Is(outcome, away) ? 0 : Is(outcome, home) ? 1 : 2;

        return outcomes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(Order)
            .ThenBy(o => o, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every book's row for one outcome. Snapshots for other outcomes are ignored, so the
    /// whole market can be handed over and the side chosen here.
    /// </summary>
    public static IReadOnlyList<MovementRow> MovementRows(IEnumerable<OddsSnapshot> snapshots, string outcome)
    {
        var side = snapshots
            .Where(s => string.Equals(s.Outcome, outcome, StringComparison.Ordinal))
            .OrderBy(s => s.CapturedAt)
            .ToList();

        if (side.Count == 0)
            return [];

        // One time axis for the table: the first reading at any book to the last. A side seen
        // at a single instant has no axis, and its sparklines are spaced evenly instead.
        var start = side[0].CapturedAt;
        var length = (side[^1].CapturedAt - start).TotalSeconds;

        double At(DateTimeOffset t) => (t - start).TotalSeconds / length;

        return side
            .GroupBy(s => s.Book, StringComparer.Ordinal)
            .Select(book =>
            {
                var seen = book.ToList();

                // A handicap market draws its number when the number moved, and its price when
                // only the juice did: a flat line beside "6¢" would say nothing happened.
                var trendsLine = seen.All(s => s.Line is not null)
                                 && seen.Select(s => s.Line).Distinct().Count() > 1;

                var plotted = seen
                    .Select(s => (s.CapturedAt, Value: trendsLine ? (double?)(double)s.Line!.Value : Cents(s.PriceAmerican)))
                    .Where(p => p.Value is not null)
                    .ToList();

                var values = plotted.Select(p => p.Value!.Value).ToList();
                List<double>? positions = length > 0 ? plotted.Select(p => At(p.CapturedAt)).ToList() : null;

                // The last price stands until the latest reading anywhere on the side.
                if (positions is not null && values.Count > 1 && positions[^1] < 1)
                {
                    values.Add(values[^1]);
                    positions.Add(1);
                }

                return new MovementRow(book.Key, seen[0], seen[^1], seen.Count - 1, trendsLine, values, positions);
            })
            .OrderBy(r => r.Book, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The finding, in the voice a person would use saying it out loud — "Dallas Cowboys
    /// shortened +136 to -218 at DraftKings" — about the side in focus, the book by its name.
    ///
    /// <para>
    /// The biggest move is the thing worth saying, so it is found and stated, including when
    /// there isn't one, because "nothing moved" is also a finding. A line move outranks any
    /// price move, since it changes what the bet is; between price moves the measure is
    /// implied probability, which is what actually moved.
    /// </para>
    /// </summary>
    public static string Headline(IReadOnlyList<MovementRow> rows, string market, string outcome)
    {
        var biggest = rows
            .Where(r => r.Moved)
            .OrderByDescending(r => Math.Abs(r.LineChange ?? 0))
            .ThenByDescending(r => Math.Abs(ImpliedChange(r)))
            .FirstOrDefault();

        if (biggest is null)
            return "Every book is holding its opening number";

        var (open, now) = (biggest.Open, biggest.Now);
        var book = BookNames.Display(biggest.Book);

        if ((biggest.LineChange ?? 0) != 0)
        {
            // Over and Under share one number, so a total's move belongs to the total.
            return market == Markets.Total
                ? $"The total moved {Bare(open.Line!.Value)} to {Bare(now.Line!.Value)} at {book}"
                : $"{outcome} moved {OddsMath.FormatLine(open.Line!.Value, outcome)} to "
                  + $"{OddsMath.FormatLine(now.Line!.Value, outcome)} at {book}";
        }

        // Shortened or drifted, said the way the desk would say it: a price whose implied
        // probability rose is one the market got more confident about.
        var who = market switch
        {
            Markets.Total when now.Line is { } total => $"{SideLabel(outcome)} {Bare(total)}",
            Markets.Spread when now.Line is { } line => $"{outcome} {OddsMath.FormatLine(line, outcome)}",
            _ => outcome
        };

        return $"{who} {(ImpliedChange(biggest) > 0 ? "shortened" : "drifted")} "
               + $"{Price(open.PriceAmerican)} to {Price(now.PriceAmerican)} at {book}";
    }

    /// <summary>
    /// A game as the picker lists it: "Oct 26, Dallas Cowboys at Philadelphia Eagles". The day
    /// leads because the list is in date order and the day is what tells two games of a series
    /// apart; and a select too narrow for the whole label loses the end of a club's name rather
    /// than leaving a separator with nothing after it. A comma, as a sentence would put it, not
    /// a middle dot, which read as two tags.
    /// </summary>
    public static string GameOption(Game game) => $"{Day(game.StartsAt)}, {Matchup(game)}";

    /// <summary>
    /// The secondary line, in words: how many moves the rows hold and the span they cover —
    /// "14 moves from Aug 24 to Oct 24". A single day gives its hours instead, and a single
    /// instant says it was one reading. It was the span and the count joined by a middle dot,
    /// two facts with nothing to say how they related.
    /// </summary>
    public static string SpanText(IReadOnlyList<MovementRow> rows)
    {
        if (rows.Count == 0)
            return string.Empty;

        var first = rows.Min(r => r.Open.CapturedAt);
        var last = rows.Max(r => r.Now.CapturedAt);

        // One instant is one reading, so there is nothing to have moved.
        if (first == last)
            return $"Seen once, {DisplayTime.Stamp(first)}";

        var span = DisplayTime.Day(first) == DisplayTime.Day(last)
            ? $"{DisplayTime.Format(first, "MMM d HH:mm")} to {DisplayTime.Format(last, "HH:mm")} {DisplayTime.Label}"
            : $"{DisplayTime.Day(first)} to {DisplayTime.Day(last)}";

        var moves = rows.Sum(r => r.Moves);

        return moves == 0
            ? $"No moves yet, {span}"
            : $"{MovesText(moves)} from {span}";
    }

    /// <summary>A count of moves, as the window's status and the span line both say it.</summary>
    public static string MovesText(int moves) => moves switch
    {
        0 => "no moves yet",
        1 => "1 move",
        _ => $"{moves} moves"
    };

    /// <summary>
    /// The Move cell: the line's change in points, then the price's in cents with the
    /// direction the number went — "-1.5 pts · ↓ 6¢". "unmoved" for a book seen once, and
    /// "no net move" for one that moved and came back, which are different facts.
    /// </summary>
    public static string MoveText(MovementRow row)
    {
        if (row.Moves == 0)
            return "unmoved";

        var parts = new List<string>(2);

        if (row.LineChange is { } line && line != 0)
            parts.Add($"{Signed(line)} {(Math.Abs(line) == 1 ? "pt" : "pts")}");

        if (row.PriceCents is { } cents && cents != 0)
            parts.Add($"{(cents > 0 ? "↑" : "↓")} {Math.Abs(cents)}¢");

        return parts.Count == 0 ? "no net move" : string.Join(" · ", parts);
    }

    /// <summary>The Open cell's tooltip: when the opening number was first seen.</summary>
    public static string OpenedTooltip(MovementRow row) => $"Opened {DisplayTime.Stamp(row.Open.CapturedAt)}";

    /// <summary>The Move cell's tooltip: the same change in words, with what it means for the side.</summary>
    public static string MoveTitle(MovementRow row, string market)
    {
        if (row.Moves == 0)
            return $"Seen once, {DisplayTime.Stamp(row.Open.CapturedAt)}: nothing to compare it with yet";

        var parts = new List<string>(2);

        if (row.LineChange is { } line && line != 0)
        {
            parts.Add(market == Markets.Total
                ? $"Total {Bare(row.Open.Line!.Value)} to {Bare(row.Now.Line!.Value)}"
                : $"Line {OddsMath.FormatLine(row.Open.Line!.Value, row.Open.Outcome)} to "
                  + $"{OddsMath.FormatLine(row.Now.Line!.Value, row.Now.Outcome)}");
        }

        if (row.PriceCents is { } cents && cents != 0)
        {
            parts.Add($"price {Price(row.Open.PriceAmerican)} to {Price(row.Now.PriceAmerican)}, "
                      + $"{(ImpliedChange(row) > 0 ? "shortened" : "drifted")} {Math.Abs(cents)}¢");
        }

        if (parts.Count == 0)
            return $"Moved {row.Moves} {(row.Moves == 1 ? "time" : "times")} and is back at its opening number";

        var sentence = string.Join("; ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    /// <summary>
    /// The sparkline's accessible name: what it draws, from the open to now, and how many moves
    /// — the numbers somebody who cannot see the shape needs instead of it.
    /// </summary>
    public static string TrendLabel(MovementRow row)
    {
        var moves = row.Moves == 0 ? "seen once, no moves" : MovesText(row.Moves);

        if (row.TrendsLine)
            return $"Line {OddsMath.FormatLine(row.Open.Line!.Value, row.Open.Outcome)} to "
                   + $"{OddsMath.FormatLine(row.Now.Line!.Value, row.Now.Outcome)}, {moves}";

        return row.Moves == 0
            ? $"Price {Price(row.Now.PriceAmerican)}, {moves}"
            : $"Price {Price(row.Open.PriceAmerican)} to {Price(row.Now.PriceAmerican)}, {moves}";
    }

    /// <summary>
    /// A reading as the market writes it: the price alone on a moneyline, the line and its price
    /// on a spread or total — "+4.5 -104". A stored zero is a broken reading and says so.
    /// </summary>
    public static string Quote(OddsSnapshot s)
    {
        var price = s.PriceAmerican == 0 ? "—" : Price(s.PriceAmerican);

        return s.Line is { } line ? $"{OddsMath.FormatLine(line, s.Outcome)} {price}" : price;
    }

    /// <summary>"2.0d ago", in the desk's own age wording.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var minutes = (now - at).TotalMinutes;

        return minutes < 1 ? "just now" : $"{Age(minutes)} ago";
    }

    /// <summary>A side's name on the switch: the club's last word, or Over and Under capitalised.</summary>
    private static string SideLabel(string outcome)
    {
        var label = SelectionText.Short(outcome);

        return label.Length == 0 ? label : char.ToUpperInvariant(label[0]) + label[1..];
    }

    /// <summary>
    /// What a column sorts on: the line first where there is one, then the price on the cents
    /// scale, so books on the same number sort by what they pay. A broken price sorts last.
    /// </summary>
    private static double Rank(OddsSnapshot s)
        => (double)(s.Line ?? 0m) * 10_000 + (Cents(s.PriceAmerican) ?? -9_999);

    /// <summary>How big a move was, for sorting the Move column: points first, then cents.</summary>
    private static double MoveSize(MovementRow row)
        => (double)Math.Abs(row.LineChange ?? 0m) * 10_000 + Math.Abs(row.PriceCents ?? 0);

    /// <summary>The change in implied probability, open to now. Zero where either price is broken.</summary>
    private static double ImpliedChange(MovementRow row)
        => Implied(row.Now.PriceAmerican) is { } now && Implied(row.Open.PriceAmerican) is { } open
            ? now - open
            : 0;

    /// <summary>
    /// Implied probability, or null where the price cannot carry one.
    ///
    /// <para>
    /// Zero is not a price — there is no American odds value between -100 and +100 — so
    /// <c>OddsMath.ImpliedProbability</c> rejects it rather than inventing an answer. That
    /// contract is right, and it is deliberately not softened there. But it means a single
    /// malformed row from a provider would otherwise take down the whole window: the throw
    /// lands in the load, not in one cell. A stored zero is a broken reading, so it is shown
    /// as one and the rest of the market still renders.
    /// </para>
    /// </summary>
    private static double? Implied(int american)
        => american == 0 ? null : OddsMath.ImpliedProbability(american);

    private static string Signed(decimal value)
        => (value > 0 ? "+" : "") + value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A total's number, unsigned: "47.5", since Over and Under both read it.</summary>
    private static string Bare(decimal value)
        => Math.Abs(value).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
