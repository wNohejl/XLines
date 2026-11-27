using XLines.Core.Entities;

namespace XLines.Core.Analytics;

/// <summary>One game on the board.</summary>
public record BoardRow(
    Game Game,
    MarketPair Moneyline,
    MarketPair Spread,
    MarketPair Total,
    int BookCount,
    DateTimeOffset? PricedAt,
    bool PricesAreClosing,
    string? Unpriced)
{
    public bool HasPrices => BookCount > 0;

    /// <summary>
    /// When this game's league was last priced — its newest scan or its last lines pull, whichever
    /// is later — for a row with no price to say so. Read only for an unpriced row; null on a priced
    /// one, and null on an unpriced one whose league has never been pulled.
    /// </summary>
    public DateTimeOffset? LastPulled { get; init; }

    /// <summary>
    /// The quarterback each NFL club started its last game with (<see cref="Starters"/>), shown
    /// under the club as baseball's probable pitcher is — or, when the injury report has him out,
    /// the passer who started without him. Null outside the NFL, for a club with no passing on
    /// record, and for a club whose starter is out with nobody to replace him.
    /// </summary>
    public string? AwayQuarterback { get; init; }

    public string? HomeQuarterback { get; init; }

    /// <summary>
    /// Why that is the quarterback, as the tooltip says it (<see cref="StartingQuarterback.Basis"/>):
    /// <see cref="Starters.LastGameBasis"/>, or the injury that moved the job on. Null where there
    /// is no passing on record.
    /// </summary>
    public string? AwayQuarterbackBasis { get; init; }

    public string? HomeQuarterbackBasis { get; init; }

    /// <summary>The card and result when the game is a fight (ADR 0021); null for a team sport.</summary>
    public Bout? Bout { get; init; }

    /// <summary>
    /// How old the newest price on this row is.
    ///
    /// Only meaningful for the live market. A closing line is old by construction — it was
    /// taken before first pitch and will never move again — so reporting its age as staleness
    /// would send an operator chasing a number that is already final.
    /// </summary>
    public TimeSpan? Age
        => PricesAreClosing || PricedAt is not { } at || ReadAt is not { } read ? null : read - at;

    /// <summary>
    /// When the row was read, by the reader's clock: what <see cref="Age"/> is measured to, so the row
    /// reads no clock of its own. Null on a row built by hand, which then states no age.
    /// </summary>
    public DateTimeOffset? ReadAt { get; init; }

    /// <summary>Whether the game has a result to show, however partial.</summary>
    public bool HasScore => Scoreline.Has(Game);

    /// <summary>
    /// The most value any book's live price on this row offers against the fair price, or null
    /// where nothing can be scored. Read across every book rather than each side's best price:
    /// the book with the most value is not always the one with the best number. A close is left
    /// out — there is nothing left to take.
    /// </summary>
    public double? BestEv => BestPick?.Book.Ev;

    /// <summary>The bet behind <see cref="BestEv"/>: which market, which side, at which book's price.</summary>
    public ValuePick? BestPick
        => new[] { Moneyline, Spread, Total }
            .SelectMany(pair => new[] { pair.First, pair.Second }
                .Where(o => o is { IsClosing: false })
                .SelectMany(o => o!.Rungs
                    .Where(r => r.Ev is not null)
                    .Select(r => new ValuePick(pair.Market, o!.Outcome, r))))
            .MaxBy(p => p.Book.Ev);
}

/// <summary>One bet on the board: a market, a side, and the book price that carries its value.</summary>
public record ValuePick(string Market, string Outcome, BookPrice Book)
{
    /// <summary>Whether the side is the one the board lists first — home, or the over.</summary>
    public bool IsFirstSide(Game game)
        => Market == Markets.Total
            ? Outcome.Equals("over", StringComparison.OrdinalIgnoreCase)
            : Outcome == game.HomeTeam?.Name;
}

/// <summary>Both sides of a market. Home/over first, away/under second.</summary>
public record MarketPair(string Market, BestOffer? First, BestOffer? Second)
{
    public bool IsPriced => First is not null || Second is not null;
}

/// <summary>The best offer on one side, plus where every other book stands.</summary>
public record BestOffer(
    string Outcome,
    string Book,
    int PriceAmerican,
    decimal? Line,
    DateTimeOffset CapturedAt,
    double? EdgePoints,
    bool LinesVary,
    bool IsClosing,
    IReadOnlyList<BookPrice> Rungs)
{
    /// <summary>
    /// The side's fair price on the best offer's number, or null where there is none — no
    /// Pinnacle and fewer than two books, or a number nobody else priced (see
    /// <see cref="FairValue"/>).
    /// </summary>
    public FairPrice? Fair { get; init; }

    /// <summary>Expected return per unit at the best offer's price, against <see cref="Fair"/>.</summary>
    public double? Ev { get; init; }

    /// <summary>
    /// The book with the most value on this side, which is not always the best price: a book
    /// hanging an extra half point ranks first on line and has no fair price to be scored by,
    /// while a better price on the reference's number does. Null when no rung can be scored.
    /// </summary>
    public BookPrice? BestValue
        => Rungs.Where(r => r.Ev is not null).OrderByDescending(r => r.Ev).FirstOrDefault();
}

/// <summary>One book's standing on a side.</summary>
public record BookPrice(string Book, int PriceAmerican, decimal? Line, double Implied)
{
    /// <summary>Expected return per unit at this book's price, where its number has a fair price.</summary>
    public double? Ev { get; init; }

    /// <summary>This book's margin on the pair, where it quotes both sides on the same number.</summary>
    public double? Hold { get; init; }

    /// <summary>
    /// When this book's number was observed. The best offer's <see cref="BestOffer.CapturedAt"/>
    /// is the best book's; a wager logged at another book is only as fresh as that book's scan.
    /// </summary>
    public DateTimeOffset? CapturedAt { get; init; }
}
