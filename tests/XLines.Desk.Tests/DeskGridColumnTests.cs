using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using XLines.Desk.Tests.Support;

namespace XLines.Desk.Tests;

/// <summary>
/// The Desk's columns stand in for MudDataGrid's: the same table written with
/// <c>DeskColumn</c>/<c>DeskTemplateColumn</c> and with <c>PropertyColumn</c>/<c>TemplateColumn</c>
/// draws the same headings and cells, sorts the same way, and hides the same column — so moving
/// an application's tables onto the Desk's names changes nothing on screen.
/// </summary>
public partial class DeskGridColumnTests : DeskTestContext
{
    private static readonly ColumnRow[] Rows =
    [
        new(2, 1.5m, "Bravo", "East", 7.5),
        new(3, 10m, "Alpha", "West", 2),
        new(1, 0.25m, "Charlie", "East", 9.5),
    ];

    private IRenderedComponent<ColumnPairGrid> Render(bool desk, bool hideGroup = false)
        => RenderComponent<ColumnPairGrid>(p => p
            .Add(x => x.Desk, desk)
            .Add(x => x.HideGroup, hideGroup)
            .Add(x => x.Rows, Rows));

    // MudBlazor numbers some elements per instance; everything else must match to the character.
    [GeneratedRegex(@"\s(id|for|aria-controls|aria-labelledby|aria-describedby|blazor:[A-Za-z]+)=""[^""]*""")]
    private static partial Regex InstanceAttributes();

    private static string Normalised(IRenderedFragment cut) => InstanceAttributes().Replace(cut.Markup, "");

    private static void AssertSame(IRenderedFragment desk, IRenderedFragment mud)
        => Assert.Equal(Normalised(mud), Normalised(desk));

    private static IReadOnlyList<string> Column(IRenderedFragment cut, int index)
        => cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[index].TextContent.Trim()).ToList();

    private static void Sort(IRenderedFragment cut, string title)
    {
        var heading = cut.FindAll("thead th").First(th => th.TextContent.Contains(title, StringComparison.Ordinal));
        SortTarget(heading).Click();
    }

    private static IElement SortTarget(IElement heading)
        => heading.QuerySelector(".sortable-column-header")
           ?? heading.QuerySelector(".column-header")
           ?? throw new InvalidOperationException("No sort target in: " + heading.OuterHtml);

    [Fact]
    public void The_desk_columns_draw_the_same_table_as_mudblazor_s()
    {
        var desk = Render(desk: true);
        var mud = Render(desk: false);

        AssertSame(desk, mud);

        // And the table is the one written: sorted by Id descending to begin with, the price
        // formatted, the name drawn by its template, the score heading by its own.
        Assert.Equal(["3", "2", "1"], Column(desk, 0));
        Assert.Equal(["10.00", "1.50", "0.25"], Column(desk, 1));
        Assert.Equal("Alpha", desk.Find("tbody tr td:nth-child(3) b").TextContent);
        Assert.NotNull(desk.Find("thead th i"));
        Assert.Equal(6, desk.FindAll("thead th").Count);
    }

    [Fact]
    public void Align_reaches_the_grid_through_a_desk_column()
    {
        var headings = Render(desk: true).FindAll("thead th");

        Assert.Contains("deskgrid__end", headings[0].ClassName);
        Assert.Contains("deskgrid__end", headings[1].ClassName);
        Assert.Contains("deskgrid__center", headings[4].ClassName);
        Assert.Contains("h-score", headings[4].ClassName);
    }

    [Fact]
    public void The_desk_columns_sort_as_mudblazor_s_do()
    {
        var desk = Render(desk: true);
        var mud = Render(desk: false);

        // Pressed once the template column sorts ascending by its key, which is the order the
        // initial Id sort already left it in; pressed again it reverses.
        Sort(desk, "Score");
        Sort(mud, "Score");
        AssertSame(desk, mud);
        Sort(desk, "Score");
        Sort(mud, "Score");
        AssertSame(desk, mud);
        Assert.Equal(["9.5", "7.5", "2.0"], Column(desk, 4).Select(s => s.Replace(',', '.')).ToList());

        Sort(desk, "Price");
        Sort(mud, "Price");
        AssertSame(desk, mud);
        Assert.Equal(["0.25", "1.50", "10.00"], Column(desk, 1));
    }

    [Fact]
    public void A_column_without_a_sort_says_so_as_mudblazor_s_does()
    {
        var desk = Render(desk: true);

        // Name says Sortable="false"; Plain is a template column with a key but no Sortable, which
        // MudBlazor never sorts. Neither draws an arrow.
        var headings = desk.FindAll("thead th");
        Assert.Null(headings[2].QuerySelector(".sort-direction-icon"));
        Assert.Null(headings[5].QuerySelector(".sort-direction-icon"));
        Assert.NotNull(headings[0].QuerySelector(".sort-direction-icon"));
    }

    [Fact]
    public void A_hidden_desk_column_hides_and_comes_back_as_mudblazor_s_does()
    {
        var desk = Render(desk: true, hideGroup: true);
        var mud = Render(desk: false, hideGroup: true);
        AssertSame(desk, mud);
        Assert.Equal(5, desk.FindAll("thead th").Count);

        desk.SetParametersAndRender(p => p.Add(x => x.HideGroup, false));
        mud.SetParametersAndRender(p => p.Add(x => x.HideGroup, false));
        AssertSame(desk, mud);
        Assert.Equal(6, desk.FindAll("thead th").Count);
    }
}
