using Bunit;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data.CrossReference;
using XLines.Desk.Primitives;
using XLines.Ingestion.Services;
using XLines.Web.Components.Desk;
using XLines.Web.Components.Panels;
using XLines.Web.Components.Snippets;
using XLines.Web.Services;
using XLines.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Web.Tests;

/// <summary>
/// Every book the desk polls is accounted for wherever a market is read: the book tables count
/// priced of polled and name the polled books with no price in one footnote line; the price
/// tooltip counts them; an unpriced Board row says how many are polled and when its league was
/// last pulled; the Game window's lines say how many of the polled books priced the game. A
/// polled book with no price never gets a row, and with no polled list everything reads as before.
/// </summary>
public class PolledBooksTests : WagerTestContext
{
    public PolledBooksTests()
    {
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<XLines.Data.XLinesDbContext>>(new JournalParlayTests.NoDatabase());
    }

    private static readonly string[] Polled =
        ["draftkings", "fanduel", "betmgm", "williamhill_us", "betrivers", "espnbet", "fanatics", "pinnacle", "bovada", "betonlineag"];

    private static BookPrice Rung(string book, int price)
        => new(book, price, null, OddsMath.ImpliedProbability(price)) { Hold = 0.04 };

    private static BestOffer Side(string outcome, int price, IEnumerable<string> books)
    {
        var rungs = books.Select((b, i) => Rung(b, price - i)).ToList();
        return new BestOffer(outcome, rungs[0].Book, rungs[0].PriceAmerican, null, DateTimeOffset.UtcNow, null, false, false, rungs);
    }

    private IRenderedComponent<BookComparison> Comparison(IEnumerable<string> books, IReadOnlyCollection<string>? polled)
    {
        var list = books.ToList();
        return RenderComponent<BookComparison>(p => p
            .Add(c => c.Market, Markets.Moneyline)
            .Add(c => c.First, Side(BoardRows.Away, 120, list))
            .Add(c => c.Second, Side(BoardRows.Home, -110, list))
            .Add(c => c.FirstSide, "Bills")
            .Add(c => c.SecondSide, "Chiefs")
            .Add(c => c.Polled, polled));
    }

    private static int Rows(IRenderedFragment cut) => cut.FindAll(".mud-table-body .mud-table-row").Count;

    // ---- The book tables ----------------------------------------------------------------------

    [Fact]
    public void Four_of_ten_count_priced_of_polled_and_name_the_six_absent()
    {
        var cut = Comparison(["draftkings", "fanduel", "betmgm", "caesars"], Polled);

        Assert.Equal(4, Rows(cut));
        Assert.Equal("All 4 priced · 10 books polled.", cut.Find(".bookmore__count").TextContent.Trim());
        // caesars is williamhill_us by name, so Caesars is not absent.
        Assert.Equal("Not priced here: BetRivers, theScore Bet, Fanatics, Pinnacle, Bovada, BetOnline.ag.",
            cut.Find(".bookmore__absent").TextContent.Trim());
        // No key: every priced book already shows.
        Assert.Empty(cut.FindAll(".bookmore button"));
    }

    [Fact]
    public void Eight_of_ten_fold_to_five_and_name_the_absent_only_when_expanded()
    {
        var cut = Comparison(Polled.Take(8), Polled);

        Assert.Equal(5, Rows(cut));
        Assert.StartsWith("Top 5 of 8 priced · 10 books polled: ", cut.Find(".bookmore__count").TextContent.Trim());
        Assert.Empty(cut.FindAll(".bookmore__absent"));

        cut.Find(".bookmore button").Click();

        Assert.Equal(8, Rows(cut));
        Assert.StartsWith("All 8 priced · 10 books polled: ", cut.Find(".bookmore__count").TextContent.Trim());
        Assert.Equal("Not priced here: Bovada, BetOnline.ag.", cut.Find(".bookmore__absent").TextContent.Trim());
    }

    [Fact]
    public void Every_polled_book_priced_says_so_and_draws_no_absent_line()
    {
        var cut = Comparison(Polled, Polled);

        cut.Find(".bookmore button").Click();

        Assert.StartsWith("All 10 polled books priced", cut.Find(".bookmore__count").TextContent.Trim());
        Assert.Empty(cut.FindAll(".bookmore__absent"));
    }

    [Fact]
    public void An_unknown_polled_list_reads_exactly_as_before()
    {
        var few = Comparison(["draftkings", "fanduel"], null);
        Assert.Empty(few.FindAll(".bookmore"));

        var empty = Comparison(["draftkings", "fanduel"], []);
        Assert.Empty(empty.FindAll(".bookmore"));

        var many = Comparison(Polled, null);
        Assert.Contains("Top 5 of 10 books priced", many.Find(".bookmore__count").TextContent);
        Assert.Empty(many.FindAll(".bookmore__absent"));
    }

    [Fact]
    public void A_priced_book_outside_the_list_is_a_row_but_not_counted_as_polled()
    {
        var cut = Comparison(["draftkings", "fanduel", "ESPN BET"], ["DraftKings", "FANDUEL", "pinnacle"]);

        Assert.Equal(3, Rows(cut));
        Assert.Equal("All 3 priced · 3 books polled.", cut.Find(".bookmore__count").TextContent.Trim());
        Assert.Equal("Not priced here: Pinnacle.", cut.Find(".bookmore__absent").TextContent.Trim());

        var coverage = BookCoverage.Of(["draftkings", "fanduel", "ESPN BET"], ["draftkings", "fanduel", "pinnacle"])!;
        Assert.Equal(3, coverage.Priced);
        Assert.Equal(2, coverage.PolledPriced);
    }

    [Fact]
    public void A_market_no_book_priced_names_how_many_are_polled()
    {
        var cut = RenderComponent<BookComparison>(p => p
            .Add(c => c.Market, Markets.Moneyline)
            .Add(c => c.FirstSide, "Bills")
            .Add(c => c.SecondSide, "Chiefs")
            .Add(c => c.Polled, Polled));

        Assert.Contains("Not priced by any of the 10 polled books yet.", cut.Markup);
        Assert.Equal(0, Rows(cut));
    }

    // ---- The price tooltip --------------------------------------------------------------------

    private IRenderedComponent<PriceCell> Cell(IEnumerable<string> books, IReadOnlyCollection<string>? polled)
        => RenderComponent<PriceCell>(p => p
            .Add(x => x.Offer, Side(BoardRows.Home, -110, books) with { IsClosing = true })
            .Add(x => x.Polled, polled));

    [Fact]
    public void The_tooltip_counts_the_hidden_books_and_the_unpriced_polled_ones()
    {
        var tip = Cell(Polled.Take(8), Polled).FindComponent<DeskTooltip>().Instance;

        Assert.Equal(5, tip.Rows!.Count);
        Assert.Equal("+3 more in More bets · 2 of 10 polled books not priced", tip.Footnote);
        // The spoken text carries it too.
        Assert.Equal("+3 more in More bets · 2 of 10 polled books not priced", tip.Text.Split('\n')[^1]);
    }

    [Fact]
    public void The_tooltip_says_only_the_unpriced_count_when_nothing_is_hidden()
        => Assert.Equal("7 of 10 polled books not priced",
            Cell(Polled.Take(3), Polled).FindComponent<DeskTooltip>().Instance.Footnote);

    [Fact]
    public void The_tooltip_adds_nothing_when_every_polled_book_is_priced_and_shown()
    {
        string[] two = ["draftkings", "fanduel"];
        Assert.Null(Cell(two, two).FindComponent<DeskTooltip>().Instance.Footnote);
        Assert.Null(Cell(two, null).FindComponent<DeskTooltip>().Instance.Footnote);
    }

    // ---- The Board ------------------------------------------------------------------------------

    private static BoardRow Unpriced(DateTimeOffset? lastPulled)
    {
        var none = new MarketPair(Markets.Moneyline, null, null);
        return new BoardRow(BoardRows.Game(), none, none with { Market = Markets.Spread }, none with { Market = Markets.Total },
            BookCount: 0, PricedAt: null, PricesAreClosing: false, Unpriced: "No lines pulled in 12 days. Pull data → Pull lines.")
        {
            LastPulled = lastPulled
        };
    }

    [Fact]
    public void An_unpriced_row_says_how_many_books_are_polled_and_when_its_league_was_pulled()
    {
        var pulled = new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);

        Assert.Equal(" · 10 books polled · NFL lines last pulled Nov 14", BoardPanel.UnpricedWhy(Unpriced(pulled), Polled));
        Assert.Equal(" · 10 books polled · NFL lines never pulled", BoardPanel.UnpricedWhy(Unpriced(null), Polled));
        Assert.Equal("", BoardPanel.UnpricedWhy(Unpriced(pulled), null));
        Assert.Equal("", BoardPanel.UnpricedWhy(Unpriced(pulled), []));
    }

    // ---- The Game window ------------------------------------------------------------------------

    [Fact]
    public void The_game_window_s_lines_count_the_polled_books_that_priced_it()
    {
        var row = BoardRows.Row();

        Assert.Equal("2 of 10 polled books", GamePanel.AcrossText(row, Polled));
        Assert.Equal("2 books", GamePanel.AcrossText(row, null));
        Assert.Equal("1 of 1 polled book and 1 more", GamePanel.AcrossText(row, ["draftkings"]));
    }

    // ---- The one source -------------------------------------------------------------------------

    [Fact]
    public void The_polled_list_is_the_live_providers_books_once_each()
    {
        var feeds = new[]
        {
            new OddsProviderState("the-odds-api", "The Odds API", true, true, ["draftkings", "williamhill_us", "caesars", "FanDuel", "fanduel"], ""),
            new OddsProviderState("odds-api-io", "odds-api.io", false, true, ["bet365"], "")
        };

        Assert.Equal(["draftkings", "williamhill_us", "FanDuel"], PolledBooks.Of(feeds));
        Assert.Empty(PolledBooks.Of([feeds[1]]));
    }

    [Fact]
    public void A_host_without_ingestion_has_no_polled_list()
        => Assert.Empty(PolledBooks.Read(Services.GetRequiredService<IServiceScopeFactory>()));
}
