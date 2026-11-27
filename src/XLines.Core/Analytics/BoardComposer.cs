using XLines.Core.Entities;

namespace XLines.Core.Analytics;

/// <summary>
/// One priced outcome, from whichever tier it came out of.
///
/// A scan and a close are the same observation with different lifetimes, and the board's
/// comparator does not care which table it was read from — so the ranking works on this
/// rather than on two near-identical entity types with a duplicated sort between them.
/// </summary>
public readonly record struct BoardQuote(
    string Book,
    string Market,
    string Outcome,
    decimal? Line,
    int PriceAmerican,
    DateTimeOffset CapturedAt)
{
    public static BoardQuote From(OddsSnapshot s)
        => new(s.Book, s.Market, s.Outcome, s.Line, s.PriceAmerican, s.CapturedAt);

    public static BoardQuote From(ClosingLine c)
        => new(c.Book, c.Market, c.Outcome, c.Line, c.PriceAmerican, c.CapturedAt);
}

/// <summary>
/// The board's ranking: one row per game from the quotes already read, with the best number on
/// each market and the book holding it. Touches no database, so every rule here is tested
/// without one; <c>BoardService</c> reads the quotes and hands them over.
/// </summary>
public static class BoardComposer
{
    /// <summary>
    /// One row from whichever tier has numbers for it, live market preferred.
    ///
    /// A game with scans is still being priced, so the scans are the truth. A game without them
    /// has either started — in which case the close is the truth — or was never covered, in
    /// which case the row says so rather than showing a dash and leaving it to be guessed.
    /// </summary>
    public static BoardRow Compose(
        Game game,
        IReadOnlyList<BoardQuote>? live,
        IReadOnlyList<BoardQuote>? closing,
        DateTimeOffset? lastScanInLeague,
        DateTimeOffset now)
    {
        var isClosing = live is not { Count: > 0 } && closing is { Count: > 0 };
        IReadOnlyList<BoardQuote> prices = live is { Count: > 0 } ? live : closing ?? [];

        return new BoardRow(
            Game: game,
            Moneyline: BestPair(prices, Markets.Moneyline, game, isClosing),
            Spread: BestPair(prices, Markets.Spread, game, isClosing),
            Total: BestPair(prices, Markets.Total, game, isClosing),
            BookCount: prices.Select(p => p.Book).Distinct().Count(),
            PricedAt: prices.Count == 0 ? null : prices.Max(p => p.CapturedAt),
            PricesAreClosing: isClosing,
            Unpriced: prices.Count > 0 ? null : ExplainGap(game, lastScanInLeague, now))
        {
            LastPulled = prices.Count > 0 ? null : lastScanInLeague,
            ReadAt = now
        };
    }

    /// <summary>
    /// Why a game has no price, in the operator's terms.
    ///
    /// A row of dashes is ambiguous between three very different situations — the odds feed is
    /// broken, this fixture was never offered, or the game started before anyone priced it —
    /// and only one of them is worth acting on. Saying which turns a silent gap into a fact.
    ///
    /// <para>
    /// This used to read "pre-match prices are dropped once a game starts", which described the
    /// retention policy rather than the fixture: it was said of every started game, including
    /// the ones whose closing line was sitting in the permanent record unread. A started game
    /// only reaches this now when no close was ever captured for it.
    /// </para>
    /// </summary>
    public static string ExplainGap(Game game, DateTimeOffset? lastScanInLeague, DateTimeOffset now)
    {
        if (game.StartsAt <= now)
            return "Under way — and no closing line was captured before it started.";

        if (lastScanInLeague is not { } last)
            return "No odds scan has run yet. Pull data → Pull lines.";

        // A pull from days ago "not covering" a fixture that did not exist yet is not a
        // coverage gap. Under manual polling it is the common case, and the fact worth acting
        // on is the age of the last pull, not the fixture.
        var age = now - last;
        if (age >= StalePull)
            return $"No lines pulled in {Ago(age)}. Pull data → Pull lines.";

        return "The last scan did not cover this fixture.";
    }

    /// <summary>
    /// How old a league's newest scan can be before an unpriced row blames the pull rather than
    /// the fixture. A day: automatic polling scans far more often than that, and a manual pull
    /// older than a day predates most of the slate it would be asked about.
    /// </summary>
    public static readonly TimeSpan StalePull = TimeSpan.FromDays(1);

    private static string Ago(TimeSpan age)
        => age.TotalDays >= 2 ? $"{(int)age.TotalDays} days" : $"{(int)age.TotalHours} hours";

    /// <summary>Both sides of one market, each with its own best book.</summary>
    public static MarketPair BestPair(IReadOnlyList<BoardQuote> prices, string market, Game game, bool closing)
    {
        var inMarket = prices.Where(p => p.Market == market).ToList();

        if (inMarket.Count == 0)
            return new MarketPair(market, null, null);

        var outcomes = inMarket.Select(p => p.Outcome).Distinct().ToList();

        // Totals name their sides; team markets use the team names, and home leads.
        var (first, second) = market == Markets.Total
            ? ("over", "under")
            : (game.HomeTeam?.Name ?? outcomes.FirstOrDefault() ?? "",
               game.AwayTeam?.Name ?? outcomes.Skip(1).FirstOrDefault() ?? "");

        // Both sides paired once, so every offer on either side reads its fair price and each
        // book's margin from the same pairing.
        var fair = FairMarket.Build(market, first, second,
            inMarket.Select(p => new SidePrice(p.Book, p.Outcome, p.Line, p.PriceAmerican)));

        return new MarketPair(
            market,
            Best(inMarket.Where(p => p.Outcome == first).ToList(), market, closing, fair),
            Best(inMarket.Where(p => p.Outcome == second).ToList(), market, closing, fair));
    }

    /// <summary>
    /// The best offer for one side, and where the rest of the market sits.
    ///
    /// <para>
    /// "Best" is not simply the biggest number. On a moneyline it is the lowest implied
    /// probability — the most you get paid for the same bet. On a handicap the <i>line</i>
    /// outranks the price, because half a point is usually worth more than a few cents:
    /// taking +1.5 at -110 beats +1.0 at -105, and a comparator that only read prices would
    /// recommend the worse bet with total confidence.
    /// </para>
    ///
    /// <para>
    /// Which line is better depends on the side. An over wants the lowest total and an under
    /// the highest; a handicap wants the most points it can get. That asymmetry is why this
    /// is a per-market comparator rather than one <c>OrderByDescending</c>.
    /// </para>
    /// </summary>
    public static BestOffer? Best(IReadOnlyList<BoardQuote> side, string market, bool closing, FairMarket fair)
    {
        if (side.Count == 0)
            return null;

        var ranked = market switch
        {
            Markets.Moneyline => side
                .OrderBy(p => OddsMath.ImpliedProbability(p.PriceAmerican))
                .ToList(),

            Markets.Total when side[0].Outcome.Equals("under", StringComparison.OrdinalIgnoreCase) =>
                side.OrderByDescending(p => p.Line ?? 0)
                    .ThenBy(p => OddsMath.ImpliedProbability(p.PriceAmerican))
                    .ToList(),

            Markets.Total => side
                .OrderBy(p => p.Line ?? 0)
                .ThenBy(p => OddsMath.ImpliedProbability(p.PriceAmerican))
                .ToList(),

            // Spread and anything new: more points is better for the side holding them.
            _ => side
                .OrderByDescending(p => p.Line ?? 0)
                .ThenBy(p => OddsMath.ImpliedProbability(p.PriceAmerican))
                .ToList()
        };

        var best = ranked[0];

        // Every book's standing, best first — the spread rail is drawn from this.
        var rungs = ranked
            .Select(p => new BookPrice(
                p.Book,
                p.PriceAmerican,
                p.Line,
                OddsMath.ImpliedProbability(p.PriceAmerican))
            {
                Ev = fair.Ev(p.Outcome, p.Line, p.PriceAmerican),
                Hold = fair.Hold(p.Book, p.Outcome, p.Line),
                CapturedAt = p.CapturedAt,
            })
            .ToList();

        // What shopping is worth here, in points of implied probability against the worst book.
        double? edge = rungs.Count > 1
            ? (rungs[^1].Implied - rungs[0].Implied) * 100
            : null;

        // Whether the books are even offering the same bet.
        //
        // This matters more than the price gap and is not visible in it: a market where every
        // book quotes -110 but on totals of 7, 8 and 9 has an identical price spread of zero
        // and a large real difference. Reporting that as "books agree" would be confidently
        // wrong about the one case worth shopping hardest.
        var linesVary = rungs.Select(r => r.Line).Distinct().Count() > 1;

        return new BestOffer(
            Outcome: best.Outcome,
            Book: best.Book,
            PriceAmerican: best.PriceAmerican,
            Line: best.Line,
            CapturedAt: best.CapturedAt,
            EdgePoints: edge,
            LinesVary: linesVary,
            IsClosing: closing,
            Rungs: rungs)
        {
            Fair = fair.For(best.Outcome, best.Line),
            Ev = fair.Ev(best.Outcome, best.Line, best.PriceAmerican),
        };
    }

    /// <summary>
    /// The injury report's word on each named player, for <see cref="Starters.Quarterback"/>:
    /// only a status the report gave within <see cref="Player.InjuryStatusLife"/> of
    /// <paramref name="now"/>. An older one is the bottom of a full list, not the club's current
    /// word, and counts as no status — a quarterback ruled out a month ago and playing since is
    /// not still out.
    /// </summary>
    public static Dictionary<string, (string Status, string? Note)> CurrentInjuries(
        IEnumerable<(string FullName, string? Status, string? Note, DateTimeOffset? UpdatedAt)> players,
        DateTimeOffset now)
        => players
            .Where(p => p.Status is not null && Player.InjuryStatusHolds(p.UpdatedAt, now))
            .DistinctBy(p => p.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.FullName, p => (p.Status!, p.Note), StringComparer.OrdinalIgnoreCase);
}
