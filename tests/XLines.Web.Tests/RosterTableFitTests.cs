using System.Text.RegularExpressions;
using XLines.Data.CrossReference;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// A roster table fits its card. Line movement's Recent form cards are capped at 720px and the
/// table inside (Player, Pos, GP, five stats, Last) came to about 770px, so Last was cut to "L…"
/// and reachable only by scrolling inside the card. The name is the column that gives way; the
/// figures keep their width, a heading breaking between its words in a card-wide grid; and where
/// a card is too narrow even for that, the name stays pinned while the figures scroll.
/// </summary>
public class RosterTableFitTests : DeskTestContext
{
    private static readonly string Css =
        File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Web", "wwwroot", "css", "xlines.css"));

    private static PlayerForm Player(int id, string name) => new(
        id, name, "WR", true, 3, DateTimeOffset.UtcNow.AddDays(-4),
        new Dictionary<string, double>
        {
            ["receiving.REC"] = 12,
            ["receiving.YDS"] = 140,
            ["receiving.TD"] = 1,
            ["rushing.CAR"] = 4,
            ["rushing.YDS"] = 21,
            ["defensive.PD"] = 0,
        },
        new Dictionary<string, int>(), new Dictionary<string, bool>());

    [Fact]
    public void The_line_movement_table_names_its_fit_and_lets_the_name_give_way()
    {
        var cut = RenderComponent<RosterTable>(p => p
            .Add(r => r.Side, [Player(1, "Christian McCaffrey-Longname"), Player(2, "Bo Nix")])
            .Add(r => r.MaxColumns, 5)
            .Add(r => r.ShowLastPlayed, true)
            .Add(r => r.GrowName, true));

        Assert.Contains("rostertable", cut.Find(".gridwrap").ClassList);

        // The name is the growing column, cut with its full text in its title.
        var heads = cut.FindAll(".mud-table-head .mud-table-cell");
        Assert.Equal("Player", heads[0].TextContent.Trim());
        Assert.Contains("ops-col--grow", heads[0].ClassList);
        var name = cut.FindAll(".mud-table-body .mud-table-row")[0].Children[0];
        Assert.Contains("trunc", name.ClassList);
        Assert.Equal("Christian McCaffrey-Longname", name.QuerySelector("span[title]")!.GetAttribute("title"));

        // Every column after Pos is a figure: on the trailing edge, as the Desk sets tabular figures.
        Assert.All(heads.Skip(2), h => Assert.Contains("deskgrid__end", h.ClassList));
        Assert.Equal("Last", heads[^1].TextContent.Trim());
        Assert.Contains("ops-col--fit", heads[^1].ClassList);
    }

    [Fact]
    public void Every_caller_gets_the_fit_class()
    {
        var cut = RenderComponent<RosterTable>(p => p
            .Add(r => r.Side, [Player(1, "Josh Allen")])
            .Add(r => r.Class, "gameplayers"));

        var wrap = cut.Find(".gridwrap");
        Assert.Contains("rostertable", wrap.ClassList);
        Assert.Contains("gameplayers", wrap.ClassList);
    }

    [Fact]
    public void The_sheet_holds_the_fit_rule()
    {
        // The name keeps a floor that still reads a name, below the operations windows' 16ch.
        Assert.Matches(@"\.rostertable \.deskgrid \.mud-table-cell\.ops-col--grow \{ min-width: 8rem; \}", Css);

        // In a grid of a card's width (Line movement caps its cards at 720px) a figure heading may
        // break between its words, so the longest word, not the label, sets the column.
        var query = Regex.Match(Css, @"@container deskgrid \(max-width: 720px\) \{(?<body>(?:[^{}]*\{[^}]*\})*)\s*\}");
        Assert.True(query.Success, "No 720px fit query for the roster table.");
        var body = query.Groups["body"].Value;
        Assert.Matches(@"\.rostertable \.deskgrid \.mud-table-head \.mud-table-cell\.deskgrid__end \{ white-space: normal; \}", body);
        Assert.Matches(@"\.rostertable [^{]*\.sortable-column-header \{[^}]*white-space: normal;", body);

        // Narrower than its figures, the table scrolls with the name pinned over the card's ground.
        Assert.Matches(@"\.rostertable :where\(\.mud-table-row\) > \.mud-table-cell:first-child \{[^}]*position: sticky;[^}]*left: 0;[^}]*background-color: var\(--surface-2\);", Css);
        // The card it is pinned over is that ground.
        Assert.Matches(@"\.oddsform > \.psection \{[^}]*background: var\(--surface-2\);", Css);

        // No colour that is not a token, and nothing forced.
        var rules = Css[Css.IndexOf("A roster table fits its card", StringComparison.Ordinal)..];
        rules = rules[..rules.IndexOf("/* ---- ", 10, StringComparison.Ordinal)];
        Assert.DoesNotContain("!important", rules);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|rgba?\(", rules);
    }
}
