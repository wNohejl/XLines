using System.Reflection;
using Bunit;
using XLines.Data;
using XLines.Data.CrossReference;
using XLines.Ingestion.Services;
using XLines.Web.Components.Panels;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Web.Tests;

/// <summary>
/// The direct count of main-sportsbook coverage (MainCoverage; ADR 0033, addendum 2026-11-27):
/// how many of the five main sportsbooks priced what is in view, which are missing, and — only
/// where the desk can know it — why: not polled, needs a paid plan, or polled but not priced.
/// </summary>
public class MainCoverageTests : WagerTestContext
{
    public MainCoverageTests()
    {
        Services.AddSingleton<IDbContextFactory<XLinesDbContext>>(new UnreachableFactory());
        Services.AddSingleton<RangePull>();
        Services.AddScoped<XLines.Desk.DeskJobRunner>();
    }

    private static readonly string[] Ten =
        ["pinnacle", "draftkings", "fanduel", "betmgm", "betrivers", "espnbet", "hardrockbet", "bovada", "betonlineag", "ballybet"];

    // ---- The computation ----------------------------------------------------------------------

    [Fact]
    public void The_count_matches_the_main_sportsbooks_through_their_aliases()
    {
        // Caesars under its second key and ESPN's cased spelling of DraftKings both count.
        var main = MainCoverage.Of(["DraftKings", "fanduel", "caesars", "pinnacle"], null);

        Assert.Equal(3, main.Priced);
        Assert.Equal("3 of 5 main sportsbooks priced", main.CountText);
        Assert.Equal("3 / 5", main.Figure);
        Assert.Equal(["BetMGM", "Bally Bet"], main.Missing.Select(m => m.Book));
    }

    /// <summary>With the polled list unknown there is just the count and the names: no reason is guessed.</summary>
    [Fact]
    public void An_unknown_polled_list_gives_the_count_and_names_and_no_reason()
    {
        var main = MainCoverage.Of(["draftkings", "fanduel", "betmgm"], null);

        Assert.All(main.Missing, m => Assert.Equal(MainGap.Unknown, m.Gap));
        Assert.Equal("Missing: Bally Bet, Caesars", main.ReasonsText);
        Assert.Equal("Main sportsbooks missing: Bally Bet, Caesars.", main.MissingText);
        Assert.Equal("3 of 5 main sportsbooks priced (missing Bally Bet, Caesars)", main.Phrase);
        Assert.Equal(MainGap.Unknown, MainCoverage.Of([], []).Missing[0].Gap);
    }

    [Fact]
    public void A_polled_main_book_with_no_price_is_polled_not_priced_and_one_off_the_list_is_not_polled()
    {
        var main = MainCoverage.Of(["draftkings", "fanduel", "betmgm"], Ten);

        Assert.Equal("Missing: Bally Bet (polled, not priced), Caesars (not polled)", main.ReasonsText);
        Assert.Equal([MainGap.PolledNotPriced, MainGap.NotPolled], main.Missing.Select(m => m.Gap));
    }

    [Fact]
    public void A_main_book_only_the_odds_api_lists_on_a_paid_plan_needs_a_paid_plan()
    {
        OddsProviderState[] feeds =
        [
            new("the-odds-api", "The Odds API", IsLive: true, Enabled: true, [.. Ten.Take(9), "williamhill_us"], "Live.")
        ];
        var polled = XLines.Web.Services.PolledBooks.Of(feeds);
        var paid = MainCoverage.PaidOnlyOf(feeds);

        Assert.Equal(["williamhill_us"], paid);
        var main = MainCoverage.Of(["draftkings", "fanduel", "betmgm"], polled, paid);
        Assert.Equal("Missing: Bally Bet (not polled), Caesars (needs a paid plan)", main.ReasonsText);
    }

    /// <summary>Caesars through the second source is polled on the free plan: not a paid-plan gap.</summary>
    [Fact]
    public void Caesars_from_the_second_source_is_not_a_paid_plan_gap()
    {
        OddsProviderState[] feeds =
        [
            new("the-odds-api", "The Odds API", IsLive: true, Enabled: true, [.. Ten.Take(9), "williamhill_us"], "Live."),
            new("sportsgameodds", "SportsGameOdds", IsLive: true, Enabled: true, ["williamhill_us"], "Live.")
        ];

        Assert.Empty(MainCoverage.PaidOnlyOf(feeds));
        var main = MainCoverage.Of(["draftkings"], XLines.Web.Services.PolledBooks.Of(feeds), MainCoverage.PaidOnlyOf(feeds));
        Assert.Equal(MainGap.PolledNotPriced, main.Missing.Single(m => m.Book == "Caesars").Gap);
    }

    [Fact]
    public void All_five_priced_misses_nothing()
    {
        var main = MainCoverage.Of(["draftkings", "fanduel", "betmgm", "ballybet", "williamhill_us"], Ten);

        Assert.Equal("5 of 5 main sportsbooks priced", main.CountText);
        Assert.Empty(main.Missing);
        Assert.Equal(string.Empty, main.MissingText);
        Assert.Equal("5 of 5 main sportsbooks priced", main.Phrase);
    }

    // ---- Ops: what the feeds can price now ----------------------------------------------------

    [Fact]
    public void Ops_counts_the_main_sportsbooks_the_feeds_can_price_now()
    {
        OddsProviderState[] feeds = [new("the-odds-api", "The Odds API", IsLive: true, Enabled: true, Ten, "Live.")];
        var sources = MainSource.Of(feeds);

        Assert.Equal("Main sportsbooks: 4 of 5 obtainable now", MainSource.CountText(sources));
        Assert.Equal(
            ["DraftKings (polled)", "FanDuel (polled)", "BetMGM (polled)", "Bally Bet (polled)", "Caesars (needs a key for the second source)"],
            sources.Select(s => s.Text));

        OddsProviderState[] paid = [new("the-odds-api", "The Odds API", IsLive: true, Enabled: true, ["draftkings", "williamhill_us"], "Live.")];
        Assert.Equal("Caesars (paid plan only)", MainSource.Of(paid)[4].Text);
        Assert.Equal("Bally Bet (not polled)", MainSource.Of(paid)[3].Text);

        // A feed that is not live polls nothing.
        OddsProviderState[] off = [new("the-odds-api", "The Odds API", IsLive: false, Enabled: true, Ten, "No key.")];
        Assert.Equal("Main sportsbooks: 0 of 5 obtainable now", MainSource.CountText(MainSource.Of(off)));
    }

    // ---- The Game window's lines ------------------------------------------------------------------

    [Fact]
    public void The_game_window_s_lines_carry_the_same_count()
        => Assert.Equal("2 of 5 main sportsbooks priced (missing BetMGM, Bally Bet, Caesars)", GamePanel.MainText(BoardRows.Row(), null));

    // ---- The Board's tile ---------------------------------------------------------------------

    private IRenderedComponent<BoardPanel> Board(IReadOnlyList<BoardRow> rows, IReadOnlyList<string>? polled, IReadOnlyList<string>? paidOnly)
    {
        var panel = RenderComponent<BoardPanel>();
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll(".empty--error")));

        var handOver = typeof(BoardPanel).GetMethod("HandOver", BindingFlags.Instance | BindingFlags.NonPublic)!;
        panel.InvokeAsync(() => handOver.Invoke(panel.Instance,
            [rows.ToList(), null, 0L, (0, 0), new Dictionary<string, string>(), polled, paidOnly]));
        PanelProbe.MarkLoaded(panel.Instance);
        panel.Render();
        return panel;
    }

    private static AngleSharp.Dom.IElement Tile(IRenderedFragment panel, string label)
        => panel.FindAll(".board__metrics .metric").Single(m => m.TextContent.Contains(label));

    [Fact]
    public void The_board_counts_the_main_sportsbooks_over_the_games_in_view_and_says_what_is_missing()
    {
        var panel = Board([BoardRows.Row()], ["draftkings", "fanduel", "betmgm", "ballybet", "williamhill_us"], ["williamhill_us"]);

        var tile = Tile(panel, "Main sportsbooks");
        var words = string.Join(' ', tile.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("2 / 5", words);
        Assert.Contains("Missing: BetMGM (polled, not priced), Bally Bet (polled, not priced), Caesars (needs a paid plan)", words);
    }
}
