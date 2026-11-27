using Bunit;
using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data.CrossReference;
using XLines.Web.Components.Panels;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Web.Tests;

/// <summary>
/// A market laid out book by book shows the main sportsbooks — DraftKings, FanDuel, BetMGM, Bally
/// Bet, Caesars, in that order — and folds the rest behind a More chip that names how many
/// (BookComparison, MoreBooks, BookRanking, BookFold) — in More bets, the Game window's market in
/// place, and Line movement alike. The rest rank by best price on either side, then the lowest
/// hold; a best price behind the chip is said in words and marked on the chip.
/// </summary>
public class BookComparisonTests : WagerTestContext
{
    public BookComparisonTests()
    {
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<XLines.Data.XLinesDbContext>>(new JournalParlayTests.NoDatabase());
    }

    // Ten books on a moneyline. The best home price is at WynnBET and the best away price at
    // Bovada — both late in the alphabet and both with the widest holds, so neither a name order
    // nor a hold order alone would show them in the first five.
    private static readonly string[] Ten =
        ["betmgm", "betonlineag", "betrivers", "bovada", "caesars", "draftkings", "fanduel", "pointsbetus", "unibet_us", "wynnbet"];

    private static double HoldOf(string book) => book switch
    {
        "wynnbet" => 0.09,
        "bovada" => 0.08,
        _ => 0.02 + Array.IndexOf(Ten, book) * 0.001
    };

    private static BookPrice Rung(string book, int price, double? ev = null)
        => new(book, price, null, OddsMath.ImpliedProbability(price)) { Hold = HoldOf(book), Ev = ev };

    private static BestOffer Side(string outcome, string best, int bestPrice, int others, bool closing = false)
    {
        var rungs = Ten.Select(b => Rung(b, b == best ? bestPrice : others, b == best ? 0.031 : -0.01))
            .OrderBy(r => r.Book == best ? 0 : 1)
            .ToList();

        return new BestOffer(outcome, best, bestPrice, null, DateTimeOffset.UtcNow, null, false, closing, rungs)
        {
            Fair = new FairPrice(0.55, -122, FairValue.SharpBook, 1)
        };
    }

    private static BestOffer Home(bool closing = false) => Side(BoardRows.Home, "wynnbet", -110, -125, closing);

    private static BestOffer Away(bool closing = false) => Side(BoardRows.Away, "bovada", 120, 104, closing);

    private static BoardRow TenBookRow()
    {
        var row = BoardRows.Row();
        return row with { Moneyline = new MarketPair(Markets.Moneyline, Home(), Away()) };
    }

    private IRenderedComponent<BookComparison> Comparison(bool closing = false)
        => RenderComponent<BookComparison>(p => p
            .Add(c => c.Market, Markets.Moneyline)
            .Add(c => c.First, Away(closing))
            .Add(c => c.Second, Home(closing))
            .Add(c => c.FirstSide, "Bills")
            .Add(c => c.SecondSide, "Chiefs"));

    private static IReadOnlyList<string> BookColumn(IRenderedFragment cut)
        => cut.FindAll(".mud-table-body .mud-table-row")
            .Select(r => r.QuerySelector(".mud-table-cell")!.TextContent.Trim())
            .ToList();

    // ---- The order --------------------------------------------------------------------------

    [Fact]
    public void Main_sportsbooks_lead_in_their_fixed_order_then_the_rest_by_best_price_then_hold()
    {
        var ranked = BookRanking.Priced(Away(), Home());

        Assert.Equal(10, ranked.Count);
        // DraftKings, FanDuel, BetMGM and Caesars (filed here as "caesars") in the fixed order, Bally
        // Bet unpriced and so absent; then the two best books, the narrower hold first; then the
        // lowest holds.
        Assert.Equal(
            ["draftkings", "fanduel", "betmgm", "caesars", "bovada", "wynnbet", "betonlineag", "betrivers", "pointsbetus", "unibet_us"],
            ranked);
    }

    /// <summary>
    /// One definition, matched by key through the aliases: Caesars under both of the feeds' keys,
    /// and the cased names ESPN's closing lines carry.
    /// </summary>
    [Theory]
    [InlineData("draftkings", 0)]
    [InlineData("DraftKings", 0)]
    [InlineData("fanduel", 1)]
    [InlineData("FanDuel", 1)]
    [InlineData("betmgm", 2)]
    [InlineData("BetMGM", 2)]
    [InlineData("ballybet", 3)]
    [InlineData("Bally Bet", 3)]
    [InlineData("williamhill_us", 4)]
    [InlineData("caesars", 4)]
    [InlineData("Caesars Sportsbook", 4)]
    [InlineData("William Hill", 4)]
    public void The_main_sportsbooks_are_matched_through_their_aliases(string book, int rank)
    {
        Assert.Equal(rank, BookNames.MainRank(book));
        Assert.True(BookNames.IsMain(book));
    }

    [Theory]
    [InlineData("pinnacle")]
    [InlineData("betonlineag")]
    [InlineData("espnbet")]
    [InlineData("ESPN BET")]
    [InlineData("fanatics")]
    [InlineData("bet365")]
    [InlineData("")]
    public void Every_other_book_is_not_a_main_sportsbook(string book)
    {
        Assert.Null(BookNames.MainRank(book));
        Assert.False(BookNames.IsMain(book));
    }

    [Fact]
    public void The_main_sportsbooks_are_the_owners_five_in_order()
        => Assert.Equal(["DraftKings", "FanDuel", "BetMGM", "Bally Bet", "Caesars"], BookNames.Main.Select(BookNames.Display));

    [Fact]
    public void A_book_with_no_price_is_neither_a_row_nor_counted()
    {
        var away = Away() with { Rungs = Away().Rungs.Where(r => r.Book != "caesars").ToList() };
        var home = Home() with { Rungs = Home().Rungs.Where(r => r.Book != "caesars").ToList() };

        Assert.Equal(9, BookRanking.Priced(away, home).Count);
        Assert.DoesNotContain("caesars", BookRanking.Priced(away, home));
    }

    [Fact]
    public void The_chip_names_how_many_are_folded()
    {
        Assert.Equal("More · 6 books", BookRanking.KeyLabel(6, expanded: false));
        Assert.Equal("More · 1 book", BookRanking.KeyLabel(1, expanded: false));
        Assert.Equal("Fewer", BookRanking.KeyLabel(6, expanded: true));

        var fold = BookFold.Of(BookRanking.Priced(Away(), Home()), Away(), Home());
        Assert.Equal((10, 4, 6), (fold.Total, fold.Main, fold.More));
    }

    [Fact]
    public void A_best_price_behind_the_chip_is_said_by_name()
    {
        var fold = BookFold.Of(BookRanking.Priced(Away(), Home()), Away(), Home());

        Assert.Equal(["Bovada", "WynnBET"], fold.BestUnderMore);
        Assert.Equal("best prices at Bovada and WynnBET, under More", fold.BestText);
        var main = MainCoverage.Of(BookRanking.Priced(Away(), Home()), null);
        Assert.Equal("4 of 5 main sportsbooks priced · 10 books priced · best prices at Bovada and WynnBET, under More.",
            BookRanking.CountLine(fold, null, main, expanded: false));
        Assert.Equal("4 of 5 main sportsbooks priced · 10 books priced: main sportsbooks first.",
            BookRanking.CountLine(fold, null, main, expanded: true));

        // One side's best at a main sportsbook is not behind the chip.
        var atMain = Home() with { Book = "draftkings" };
        Assert.Equal("best price at Bovada, under More", BookFold.Of(BookRanking.Priced(Away(), atMain), Away(), atMain).BestText);
    }

    [Fact]
    public void The_fair_line_is_spelled_one_way()
        => Assert.Equal("Fair Kansas City Chiefs -122 (Pinnacle, no-vig)", BookRanking.FairText(null, Home()));

    // ---- The table -------------------------------------------------------------------------

    private static AngleSharp.Dom.IElement Chip(IRenderedFragment cut) => cut.Find(".bookmore .desk-chip");

    [Fact]
    public void Ten_books_show_the_main_sportsbooks_and_a_more_chip_for_the_rest()
    {
        var cut = Comparison();

        Assert.Equal(["DraftKings", "FanDuel", "BetMGM", "Caesars"], BookColumn(cut));

        var chip = Chip(cut);
        Assert.Equal("BUTTON", chip.TagName);
        Assert.StartsWith("More · 6 books", chip.TextContent.Trim());
        Assert.Equal("false", chip.GetAttribute("aria-expanded"));
        Assert.Equal("4 of 5 main sportsbooks priced · 10 books priced · best prices at Bovada and WynnBET, under More.",
            cut.Find(".bookmore__count").TextContent.Trim());
    }

    /// <summary>
    /// The best prices are at Bovada and WynnBET, behind the chip: the count line names them, and
    /// the chip carries the table's dot with its words. Open, the rows carry the marks as before
    /// and the chip's dot goes.
    /// </summary>
    [Fact]
    public void A_best_price_under_more_is_marked_on_the_chip_and_on_its_row_once_shown()
    {
        var cut = Comparison();

        Assert.Empty(cut.FindAll(".bookcmp__best"));
        Assert.NotNull(Chip(cut).QuerySelector(".bookmore__dot[aria-hidden=true]"));
        Assert.Equal(", best price", Chip(cut).QuerySelector(".sr-only")!.TextContent);

        Chip(cut).Click();

        Assert.Equal(2, cut.FindAll(".bookcmp__best").Count);
        Assert.Equal(2, cut.FindAll(".bookcmp__dot").Count);
        Assert.Null(Chip(cut).QuerySelector(".bookmore__dot"));
        Assert.Equal("4 of 5 main sportsbooks priced · 10 books priced: main sportsbooks first.", cut.Find(".bookmore__count").TextContent.Trim());
    }

    [Fact]
    public void A_best_price_at_a_main_sportsbook_puts_no_dot_on_the_chip()
    {
        var away = Away() with { Book = "draftkings" };
        var home = Home() with { Book = "fanduel" };
        var cut = RenderComponent<BookComparison>(p => p
            .Add(c => c.Market, Markets.Moneyline)
            .Add(c => c.First, away)
            .Add(c => c.Second, home)
            .Add(c => c.FirstSide, "Bills")
            .Add(c => c.SecondSide, "Chiefs"));

        Assert.Equal(2, cut.FindAll(".bookcmp__best").Count);
        Assert.Null(Chip(cut).QuerySelector(".bookmore__dot"));
        Assert.Equal("4 of 5 main sportsbooks priced · 10 books priced.", cut.Find(".bookmore__count").TextContent.Trim());
    }

    [Fact]
    public void The_chip_reveals_every_book_by_name_and_folds_them_back()
    {
        var cut = Comparison();

        Chip(cut).Click();

        var books = BookColumn(cut);
        Assert.Equal(10, books.Count);
        Assert.All(Ten, b => Assert.Contains(BookNames.Display(b), books));
        Assert.Equal(["DraftKings", "FanDuel", "BetMGM", "Caesars", "Bovada", "WynnBET"], books.Take(6));

        var chip = Chip(cut);
        Assert.StartsWith("Fewer", chip.TextContent.Trim());
        Assert.Equal("true", chip.GetAttribute("aria-expanded"));

        chip.Click();
        Assert.Equal(4, BookColumn(cut).Count);
        Assert.Equal("false", Chip(cut).GetAttribute("aria-expanded"));
    }

    [Fact]
    public void Only_main_sportsbooks_priced_draw_no_chip()
    {
        var cut = RenderComponent<BookComparison>(p => p
            .Add(c => c.Market, Markets.Moneyline)
            .Add(c => c.First, BoardRows.Row().Moneyline.Second)
            .Add(c => c.Second, BoardRows.Row().Moneyline.First)
            .Add(c => c.FirstSide, "Bills")
            .Add(c => c.SecondSide, "Chiefs"));

        Assert.Equal(2, BookColumn(cut).Count);
        Assert.Empty(cut.FindAll(".desk-chip"));
        // Nothing to fold, but the count is still on the table, with the main sportsbooks missing.
        Assert.Equal("2 of 5 main sportsbooks priced · 2 books priced.", cut.Find(".bookmore__count").TextContent.Trim());
        Assert.Equal("Main sportsbooks missing: BetMGM, Bally Bet, Caesars.", cut.Find(".bookmore__absent").TextContent.Trim());
    }

    /// <summary>A market no main sportsbook priced has nothing to fold behind: every row shows, and it says so.</summary>
    [Fact]
    public void A_market_no_main_sportsbook_priced_is_drawn_unfolded_and_says_so()
    {
        static BestOffer Only(BestOffer side) => side with { Rungs = side.Rungs.Where(r => !BookNames.IsMain(r.Book)).ToList() };
        var cut = RenderComponent<BookComparison>(p => p
            .Add(c => c.Market, Markets.Moneyline)
            .Add(c => c.First, Only(Away()))
            .Add(c => c.Second, Only(Home()))
            .Add(c => c.FirstSide, "Bills")
            .Add(c => c.SecondSide, "Chiefs"));

        Assert.Equal(6, BookColumn(cut).Count);
        Assert.Empty(cut.FindAll(".desk-chip"));
        Assert.Equal("No main sportsbook priced this market (0 of 5) · 6 books priced.", cut.Find(".bookmore__count").TextContent.Trim());
        Assert.Equal(2, cut.FindAll(".bookcmp__best").Count);
    }

    [Fact]
    public void A_price_reads_as_line_movement_quotes_it_with_its_edge_after()
    {
        var cut = Comparison();
        // The best prices are at books behind the chip: open it to read them.
        Chip(cut).Click();
        var best = cut.FindAll(".bookcmp__best").Select(e => e.TextContent.Trim()).ToList();

        Assert.Contains("+120", best);
        Assert.Contains("-110", best);
        Assert.Contains(cut.FindAll(".bookcmp__ev"), e => e.TextContent.Trim() == "+3.1%");
        Assert.DoesNotContain("% EV", cut.Markup);
    }

    // ---- More bets ---------------------------------------------------------------------------

    [Fact]
    public void More_bets_lays_each_market_out_as_a_card_in_a_flow()
    {
        Data.Row = TenBookRow();
        var cut = RenderComponent<BoardAllBets>(p => p.Add(x => x.GameId, BoardRows.GameId));

        Assert.NotNull(cut.Find(".wflow"));
        var titles = cut.FindAll(".wflow > .wsection .wgroup__bar .wgroup__title").Select(t => t.TextContent.Trim()).ToList();
        Assert.Equal(3, titles.Count);
        // The grid is the card's own child: the card frames it, and nothing frames it twice.
        Assert.NotEmpty(cut.FindAll(".wgroup > .deskgrid"));
        Assert.Contains("(Pinnacle, no-vig)", cut.Find(".wsection .wgroup__foot").TextContent);
        // The two price columns read as the game does: away, then home.
        var heads = cut.Find(".wsection").QuerySelectorAll(".mud-table-head .mud-table-cell").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(4, heads.Count);
        Assert.Equal("Book", heads[0]);
        Assert.EndsWith(BoardRows.Away, heads[1]);
        Assert.EndsWith(BoardRows.Home, heads[2]);
        Assert.Equal("Hold", heads[3]);
    }

    [Fact]
    public void A_market_s_view_more_survives_a_redraw_and_is_its_own()
    {
        Data.Row = TenBookRow();
        var cut = RenderComponent<BoardAllBets>(p => p.Add(x => x.GameId, BoardRows.GameId));

        var moneyline = cut.FindAll(".wsection")[0];
        Assert.Equal(4, moneyline.QuerySelectorAll(".mud-table-body .mud-table-row").Length);

        cut.Find(".wsection .bookmore .desk-chip").Click();
        cut.SetParametersAndRender(p => p.Add(x => x.GameId, BoardRows.GameId));

        moneyline = cut.FindAll(".wsection")[0];
        Assert.Equal(10, moneyline.QuerySelectorAll(".mud-table-body .mud-table-row").Length);
        Assert.Equal("true", moneyline.QuerySelector(".bookmore .desk-chip")!.GetAttribute("aria-expanded"));
        Assert.Equal(moneyline.Id, moneyline.QuerySelector(".bookmore .desk-chip")!.GetAttribute("aria-controls"));

        // Another game folds every market back to its main sportsbooks.
        cut.SetParametersAndRender(p => p.Add(x => x.GameId, BoardRows.GameId + 1));
        cut.WaitForAssertion(() =>
            Assert.Equal(4, cut.FindAll(".wsection")[0].QuerySelectorAll(".mud-table-body .mud-table-row").Length));
    }

    // ---- The Game window's market in place --------------------------------------------------

    [Fact]
    public void The_game_window_s_market_folds_its_closes_too()
    {
        var cut = RenderComponent<MarketMovement>(p => p
            .Add(c => c.GameId, BoardRows.GameId)
            .Add(c => c.Market, Markets.Moneyline)
            .Add(c => c.MarketName, "Moneyline")
            .Add(c => c.First, Away(closing: true))
            .Add(c => c.Second, Home(closing: true))
            .Add(c => c.FirstSide, "Bills")
            .Add(c => c.SecondSide, "Chiefs")
            .Add(c => c.Id, "gamemove-test")
            .Add(c => c.Closing, true));

        Assert.Equal(4, BookColumn(cut).Count);
        Assert.Equal("gamemove-test", cut.Find(".bookmore .desk-chip").GetAttribute("aria-controls"));
        Assert.Empty(cut.FindAll("button.take"));

        Chip(cut).Click();
        Assert.Equal(10, BookColumn(cut).Count);

        // A new price for the same market re-renders the row and keeps it open.
        cut.SetParametersAndRender(p => p.Add(c => c.PricedAt, DateTimeOffset.UtcNow));
        Assert.Equal(10, BookColumn(cut).Count);
    }

    // ---- Line movement -----------------------------------------------------------------------

    /// <summary>
    /// A side nobody has moved shows each book's standing in Line movement: the main sportsbooks
    /// lead as in More bets, and the chip shows the rest and says the best prices are behind it.
    /// </summary>
    [Fact]
    public void Line_movement_s_standing_folds_its_books_and_says_where_the_best_is()
    {
        var at = DateTimeOffset.UtcNow;
        var snaps = Ten.Select(b => new OddsSnapshot
        {
            Book = b, Market = Markets.Moneyline, Outcome = BoardRows.Home,
            PriceAmerican = b == "wynnbet" ? -110 : -125, CapturedAt = at.AddHours(-1)
        });
        var rows = OddsPanel.MovementRows(snaps, BoardRows.Home);

        var cut = RenderComponent<PriceStanding>(p => p
            .Add(x => x.Rows, rows)
            .Add(x => x.Side, Home())
            .Add(x => x.Other, Away())
            .Add(x => x.SideName, "Chiefs")
            .Add(x => x.OtherName, "Bills")
            .Add(x => x.ShowBooks, true)
            .Add(x => x.Now, at));

        var books = BookColumn(cut);
        Assert.Equal(["DraftKings", "FanDuel", "BetMGM", "Caesars"], books);
        Assert.Equal("4 of 5 main sportsbooks priced · 10 books priced · best prices at WynnBET and Bovada, under More.",
            cut.Find(".bookmore__count").TextContent.Trim());

        var key = Chip(cut);
        Assert.StartsWith("More · 6 books", key.TextContent.Trim());
        Assert.NotNull(key.QuerySelector(".bookmore__dot"));
        Assert.Equal(cut.Find(".standing__books").Id, key.GetAttribute("aria-controls"));

        key.Click();
        var all = BookColumn(cut);
        Assert.Equal(10, all.Count);
        Assert.StartsWith(BookNames.Display("bovada"), all[4]);
        Assert.StartsWith(BookNames.Display("wynnbet"), all[5]);
    }

    // ---- The count line with the polled list ------------------------------------------------

    [Fact]
    public void With_the_polled_list_the_count_line_reads_in_the_folds_terms()
    {
        string[] polled = ["draftkings", "fanduel", "betmgm", "ballybet", "pinnacle", "bovada", "betonlineag", "betrivers", "espnbet", "hardrockbet"];
        string[] priced = ["draftkings", "fanduel", "betmgm", "ballybet", "pinnacle", "bovada", "betonlineag", "betrivers", "espnbet"];
        var coverage = BookCoverage.Of(priced, polled);
        var best = new BestOffer("Over", "pinnacle", -102, 47.5m, DateTimeOffset.UtcNow, null, false, false,
            priced.Select(b => new BookPrice(b, -110, 47.5m, 0.52)).ToList());
        var fold = BookFold.Of(priced, best, null);

        var main = MainCoverage.Of(priced, polled);
        Assert.Equal("4 of 5 main sportsbooks priced · 9 books priced · 10 polled · best price at Pinnacle, under More.",
            BookRanking.CountLine(fold, coverage, main, expanded: false));
        Assert.Equal("4 of 5 main sportsbooks priced · 9 books priced · 10 polled: main sportsbooks first.",
            BookRanking.CountLine(fold, coverage, main, expanded: true));

        string[] offMain = ["pinnacle", "bovada", "betonlineag"];
        Assert.Equal("No main sportsbook priced this market (0 of 5) · 3 books priced · 10 polled.",
            BookRanking.CountLine(BookFold.Of(offMain, null, null), BookCoverage.Of(offMain, polled), MainCoverage.Of(offMain, polled), expanded: false));
    }

    // ---- Pickers ------------------------------------------------------------------------------

    /// <summary>A picker lists the main sportsbooks under their heading in the fixed order, then the rest by name.</summary>
    [Fact]
    public void A_book_picker_groups_the_main_sportsbooks_first()
    {
        var cut = RenderComponent<BookOptions>(p => p.Add(x => x.Books,
            new[] { "wynnbet", "williamhill_us", "betonlineag", "betmgm", "draftkings", "bovada" }));

        var groups = cut.FindAll("optgroup");
        Assert.Equal(["Main sportsbooks", "More sportsbooks"], groups.Select(g => g.GetAttribute("label")));
        Assert.Equal(["draftkings", "betmgm", "williamhill_us"], groups[0].QuerySelectorAll("option").Select(o => o.GetAttribute("value")));
        Assert.Equal(["BetOnline.ag", "Bovada", "WynnBET"], groups[1].QuerySelectorAll("option").Select(o => o.TextContent.Trim()));
    }

    [Fact]
    public void A_picker_of_one_kind_needs_no_headings()
    {
        var cut = RenderComponent<BookOptions>(p => p.Add(x => x.Books, new[] { "fanduel", "draftkings" }));

        Assert.Empty(cut.FindAll("optgroup"));
        Assert.Equal(["DraftKings", "FanDuel"], cut.FindAll("option").Select(o => o.TextContent.Trim()));
    }
}
