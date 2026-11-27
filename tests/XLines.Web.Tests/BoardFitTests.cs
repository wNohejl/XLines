using System.Text.RegularExpressions;
using XLines.Data.CrossReference;
using XLines.Desk.Testing;
using XLines.Web.Components.Panels;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The Board fits the window it has. The default Line watch Board (about 940px) used to hide its
/// totals behind a scrollbar and clip the second spread mid-price, because spread and totals only
/// folded away under 820px and the full table needed 1216px. It now gives width back in steps by
/// its own width — the starters, then the one-line start, then the rails — stacks each market's
/// two sides in one column, and scrolls only at the narrowest, the matchup pinned. The layout is
/// CSS, so its rules are pinned here against the app's sheet and the Board's markup.
/// </summary>
public class BoardFitTests
{
    private static readonly string Css =
        DeskConventions.WithoutComments(Support.Sheets.App);

    private static readonly string Razor =
        File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Web", "Components", "Panels", "BoardPanel.razor"));

    private static string Block(string query)
    {
        var start = Css.IndexOf(query, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No {query}");

        // The block runs to the brace that closes the one the query opened.
        var depth = 0;
        for (var i = Css.IndexOf('{', start); i < Css.Length; i++)
        {
            depth += Css[i] == '{' ? 1 : Css[i] == '}' ? -1 : 0;
            if (depth == 0)
                return Css[start..(i + 1)];
        }

        throw new InvalidOperationException($"{query} never closes");
    }

    [Fact]
    public void No_market_folds_away_and_nothing_says_so()
    {
        Assert.DoesNotContain("col--wider", Razor);
        Assert.DoesNotContain("board__folded", Razor);
        Assert.DoesNotContain("board__folded", Css);

        // Each market's two columns are named for the market, so a narrow Board can stack them.
        foreach (var market in new[] { "board__ml", "board__spread", "board__total" })
        {
            Assert.Contains($"HeaderClass=\"{market} board__pair\" CellClass=\"{market} board__pair\"", Razor);
            Assert.Contains($"HeaderClass=\"{market} board__pair-end\" CellClass=\"{market} board__pair-end\"", Razor);
        }
    }

    [Fact]
    public void The_starters_go_first_and_the_start_stacks_its_day_over_its_clock()
    {
        var first = Block("@container board (max-width: 1320px)");

        Assert.Matches(@"\.board__starter\s*\{\s*display:\s*none;", first);
        Assert.Matches(@"\.board__date,\s*\.deskgrid--board \.board__clock\s*\{\s*display:\s*block;", first);
        Assert.Contains("<span class=\"board__date\">", Razor);
        Assert.Contains("<span class=\"board__clock\">", Razor);
    }

    [Fact]
    public void Then_the_rails_and_the_notes_drop_to_the_tooltip_and_the_book_code_stays()
    {
        var second = Block("@container board (max-width: 1180px)");

        Assert.Matches(@"\.deskgrid--board \.rail,\s*\.deskgrid--board \.price__edge:not\(\.price__edge--closing\),\s*\.deskgrid--board \.price__ev:not\(\.price__ev--plus\)\s*\{\s*display:\s*none;", second);
        Assert.DoesNotContain("price__book", second);
        Assert.DoesNotContain("price__odds", second);
    }

    [Fact]
    public void Then_each_market_stacks_its_two_sides_in_one_column()
    {
        var third = Block("@container board (max-width: 1000px)");

        Assert.Matches(@"> tbody > \.mud-table-row\s*\{\s*display:\s*grid;", third);
        Assert.Contains("grid-template-columns:", third);
        Assert.Matches(@"\.board__ml\s*\{\s*grid-column:\s*5;", third);
        Assert.Matches(@"\.board__spread\s*\{\s*grid-column:\s*6;", third);
        Assert.Matches(@"\.board__total\s*\{\s*grid-column:\s*7;", third);

        // Away on the first line beside the away club; a fight's red corner first.
        Assert.Contains("board__row--fight", third);
        Assert.Contains("\"board__row--fight\"", Razor);
    }

    [Fact]
    public void Only_the_narrowest_board_scrolls_and_its_matchup_is_pinned()
    {
        var fourth = Block("@container board (max-width: 680px)");

        Assert.Matches(@"\.mud-table-root\s*\{\s*min-width:\s*680px;", fourth);
        Assert.Matches(@":nth-child\(-n\+2\)\s*\{\s*position:\s*sticky;", fourth);
        Assert.Matches(@":nth-child\(2\)\s*\{\s*left:\s*var\(--board-when\);", fourth);

        // No other step scrolls: nothing wider sets a floor on the table.
        foreach (var query in new[] { "(max-width: 1320px)", "(max-width: 1180px)", "(max-width: 1000px)" })
            Assert.DoesNotContain("min-width: 680px", Block($"@container board {query}"));
    }

    [Fact]
    public void Every_price_stays_on_one_line()
        => Assert.Matches(@"\.price__top\s*\{[^}]*white-space:\s*nowrap;", Css);

    [Theory]
    [InlineData(47.5, null)]
    [InlineData(null, "O")]
    public void A_total_names_its_side_only_when_its_line_does_not(double? line, string? expected)
    {
        var offer = new BestOffer("over", "draftkings", -110, (decimal?)line, DateTimeOffset.UnixEpoch, null, false, false, []);

        Assert.Equal(expected, BoardPanel.TotalSide(offer, "O"));
    }

    [Fact]
    public void The_figures_are_one_compact_row_and_the_notes_share_a_line()
    {
        Assert.Contains("<MetricRow Class=\"board__metrics\">", Razor);
        Assert.Matches(@"\.board__metrics > \.metric\s*\{[^}]*display:\s*grid;", Css);
        Assert.Contains("<div class=\"board__banners\">", Razor);
        Assert.Matches(@"\.board__banners\s*\{[^}]*flex-wrap:\s*wrap;", Css);
    }

    [Fact]
    public void More_bets_cards_take_the_drawer_s_width()
        => Assert.Matches(@"\.deskgrid--board \.rowacts__snippet:has\(> \.snippet--panes\)\s*\{\s*max-width:", Css);
}

/// <summary>
/// Line movement reads at a table's width maximised: the following line, the standing and the
/// books in one 720px column, Recent form's clubs as cards in a second beside it once two fit.
/// </summary>
public class LineMovementLayoutTests
{
    private static readonly string Css =
        DeskConventions.WithoutComments(Support.Sheets.App);

    private static readonly string Razor =
        File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Web", "Components", "Panels", "OddsPanel.razor"));

    [Fact]
    public void The_following_line_ends_where_the_column_under_it_does()
        => Assert.Matches(@"\.slatewatch__following\s*\{[^}]*max-width:\s*720px;", Css);

    [Fact]
    public void Standing_and_form_are_capped_columns_that_go_side_by_side()
    {
        Assert.Contains("<div class=\"oddswin\">", Razor);
        Assert.Contains("<div class=\"oddswin__cols\">", Razor);
        Assert.Matches(@"\.oddswin__cols\s*\{[^}]*max-width:\s*720px;", Css);
        Assert.Matches(@"@container oddswin \(min-width: 1456px\)\s*\{\s*\.oddswin__cols\s*\{\s*grid-template-columns:\s*repeat\(2,", Css);
    }

    [Fact]
    public void Each_club_s_form_is_a_card_with_its_name_in_the_bar()
    {
        Assert.Contains("<div class=\"oddsform\">", Razor);
        Assert.Matches(@"\.oddsform > \.psection\s*\{[^}]*border:\s*1px solid var\(--separator-strong\);", Css);
        Assert.Matches(@"\.oddsform > \.psection > \.psection__head\s*\{[^}]*border-bottom:\s*1px solid var\(--separator\);", Css);
    }

    [Fact]
    public void The_overview_lists_are_cards_with_their_names_inside()
    {
        Assert.DoesNotContain("panel-h slatewatch__title", Razor);
        Assert.Equal(2, Regex.Matches(Razor, "<div class=\"wgroup slatewatch__list\">").Count);
    }
}
