using XLines.Core.Analytics;
using XLines.Core.Entities;

namespace XLines.Tests.Analytics;

/// <summary>
/// The board's ranking on quotes held in memory: which book is best on each side, what an
/// unpriced row says at a fixed clock, and a market only one side of which is quoted. No Postgres;
/// <c>BoardServiceTests</c> covers the queries that feed it.
/// </summary>
public class BoardComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);

    private static Game Fixture(TimeSpan startsIn) => new()
    {
        Id = 1,
        HomeTeamId = 10,
        AwayTeamId = 20,
        HomeTeam = new Team { Id = 10, Name = "Home", Abbrev = "HOM" },
        AwayTeam = new Team { Id = 20, Name = "Away", Abbrev = "AWY" },
        StartsAt = Now + startsIn,
        Status = GameStatus.Scheduled
    };

    private static BoardQuote Q(string book, string market, string outcome, int price, decimal? line = null)
        => new(book, market, outcome, line, price, Now.AddMinutes(-5));

    [Fact]
    public void Moneyline_best_is_the_lowest_implied_probability()
    {
        var game = Fixture(TimeSpan.FromHours(2));
        var prices = new[]
        {
            Q("fanduel", Markets.Moneyline, "Home", -120),
            Q("draftkings", Markets.Moneyline, "Home", -105),
            Q("betmgm", Markets.Moneyline, "Home", -130),
            Q("fanduel", Markets.Moneyline, "Away", +100),
            Q("draftkings", Markets.Moneyline, "Away", -115),
        };

        var pair = BoardComposer.BestPair(prices, Markets.Moneyline, game, closing: false);

        Assert.Equal("draftkings", pair.First!.Book);
        Assert.Equal(-105, pair.First.PriceAmerican);
        Assert.Equal(["draftkings", "fanduel", "betmgm"], pair.First.Rungs.Select(r => r.Book));
        Assert.Equal("fanduel", pair.Second!.Book);
        Assert.False(pair.First.IsClosing);
        Assert.NotNull(pair.First.EdgePoints);
    }

    [Fact]
    public void Spread_best_takes_the_extra_point_over_the_better_price()
    {
        var game = Fixture(TimeSpan.FromHours(2));
        var prices = new[]
        {
            Q("fanduel", Markets.Spread, "Home", -105, 1.0m),
            Q("draftkings", Markets.Spread, "Home", -110, 1.5m),
        };

        var best = BoardComposer.BestPair(prices, Markets.Spread, game, closing: false).First!;

        Assert.Equal("draftkings", best.Book);
        Assert.Equal(1.5m, best.Line);
        Assert.True(best.LinesVary);
    }

    [Fact]
    public void Total_best_is_the_lowest_over_and_the_highest_under()
    {
        var game = Fixture(TimeSpan.FromHours(2));
        var prices = new[]
        {
            Q("fanduel", Markets.Total, "over", -110, 8.5m),
            Q("draftkings", Markets.Total, "over", -110, 8.0m),
            Q("fanduel", Markets.Total, "under", -110, 8.5m),
            Q("draftkings", Markets.Total, "under", -110, 8.0m),
        };

        var pair = BoardComposer.BestPair(prices, Markets.Total, game, closing: false);

        Assert.Equal(("draftkings", 8.0m), (pair.First!.Book, pair.First.Line!.Value));
        Assert.Equal(("fanduel", 8.5m), (pair.Second!.Book, pair.Second.Line!.Value));
    }

    [Fact]
    public void A_pair_with_one_side_missing_has_a_best_on_the_other_only()
    {
        var game = Fixture(TimeSpan.FromHours(2));
        var prices = new[] { Q("fanduel", Markets.Moneyline, "Home", -120) };

        var pair = BoardComposer.BestPair(prices, Markets.Moneyline, game, closing: false);

        Assert.True(pair.IsPriced);
        Assert.Equal("fanduel", pair.First!.Book);
        Assert.Null(pair.First.EdgePoints);
        Assert.Null(pair.Second);
    }

    [Fact]
    public void A_market_nobody_quoted_is_an_unpriced_pair()
    {
        var pair = BoardComposer.BestPair([], Markets.Spread, Fixture(TimeSpan.FromHours(2)), closing: false);

        Assert.False(pair.IsPriced);
        Assert.Equal(Markets.Spread, pair.Market);
    }

    [Fact]
    public void The_gap_explanation_reads_the_fixed_clock()
    {
        var started = Fixture(TimeSpan.FromMinutes(-30));
        var upcoming = Fixture(TimeSpan.FromHours(2));

        Assert.StartsWith("Under way", BoardComposer.ExplainGap(started, Now.AddHours(-1), Now));
        Assert.Equal("No odds scan has run yet. Pull data → Pull lines.", BoardComposer.ExplainGap(upcoming, null, Now));
        Assert.Equal("The last scan did not cover this fixture.", BoardComposer.ExplainGap(upcoming, Now.AddHours(-3), Now));
        Assert.Equal("No lines pulled in 30 hours. Pull data → Pull lines.", BoardComposer.ExplainGap(upcoming, Now.AddHours(-30), Now));
        Assert.Equal("No lines pulled in 3 days. Pull data → Pull lines.", BoardComposer.ExplainGap(upcoming, Now.AddDays(-3), Now));
    }

    [Fact]
    public void A_row_with_no_prices_explains_itself_and_states_the_last_pull()
    {
        var lastPull = Now.AddDays(-2);

        var row = BoardComposer.Compose(Fixture(TimeSpan.FromHours(2)), [], [], lastPull, Now);

        Assert.False(row.HasPrices);
        Assert.Equal("No lines pulled in 2 days. Pull data → Pull lines.", row.Unpriced);
        Assert.Equal(lastPull, row.LastPulled);
        Assert.Equal(Now, row.ReadAt);
        Assert.Null(row.Age);
    }

    [Fact]
    public void A_row_read_from_the_close_is_marked_closing_and_states_no_age()
    {
        var closing = new[]
        {
            Q("fanduel", Markets.Moneyline, "Home", -120),
            Q("fanduel", Markets.Moneyline, "Away", +100),
        };

        var row = BoardComposer.Compose(Fixture(TimeSpan.FromMinutes(-30)), live: null, closing, null, Now);

        Assert.True(row.PricesAreClosing);
        Assert.True(row.Moneyline.First!.IsClosing);
        Assert.Null(row.Unpriced);
        Assert.Null(row.LastPulled);
        Assert.Null(row.Age);
        Assert.Equal(1, row.BookCount);
    }

    [Fact]
    public void Live_prices_win_over_the_close_and_carry_their_age()
    {
        var live = new[] { Q("draftkings", Markets.Moneyline, "Home", -110) };
        var closing = new[] { Q("fanduel", Markets.Moneyline, "Home", -120) };

        var row = BoardComposer.Compose(Fixture(TimeSpan.FromHours(2)), live, closing, null, Now);

        Assert.False(row.PricesAreClosing);
        Assert.Equal("draftkings", row.Moneyline.First!.Book);
        Assert.Equal(TimeSpan.FromMinutes(5), row.Age);
    }
}
