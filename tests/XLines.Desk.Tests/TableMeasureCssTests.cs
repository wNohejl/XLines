using System.Text.RegularExpressions;

namespace XLines.Desk.Tests;

/// <summary>
/// A table reads at a table's width, and a row of figures at its figures' (design review, 26
/// November): more width is more columns, never wider rows. Three rules in desk.css hold it — a
/// card holding a table takes two tracks of a flow (.wsection--pair), a window whose content is a
/// table stands at the table measure (.wmeasure), and a row of one to six tiles stops at that many
/// tiles' width — and the measures are tokens of the plain geometry block.
/// </summary>
public class TableMeasureCssTests
{
    private static string Css => DeskConventions.WithoutComments(
        File.ReadAllText(Path.Combine(Support.Repo.Root(), "src", "XLines.Desk", "wwwroot", "css", "desk.css")));

    private static string Rule(string selector)
    {
        var match = Regex.Match(Css, @"(?m)^" + Regex.Escape(selector) + @"\s*\{[^}]*\}");
        Assert.True(match.Success, $"{selector} is not declared");
        return match.Value;
    }

    [Fact]
    public void The_measures_are_geometry_tokens()
    {
        var geometry = Regex.Match(Css, @"\n:root \{([^{}]*--control-h:[^{}]*)\}");
        Assert.True(geometry.Success);
        Assert.Matches(@"--measure-table:\s*\d+px;", geometry.Value);
        Assert.Matches(@"--measure-table-narrow:\s*\d+px;", geometry.Value);
    }

    [Fact]
    public void A_table_card_takes_two_tracks_once_two_fit_and_the_row_before()
    {
        Assert.Contains("grid-column: 1 / -1", Rule(".wflow > .wsection--pair"));
        Assert.Matches(@"@container wflow \(min-width: 856px\) \{[^}]*\}\s*\.wflow > \.wsection--pair \{ grid-column: span 2; \}", Css);
    }

    [Fact]
    public void A_window_of_tables_is_centred_at_the_measure_and_never_wider_than_itself()
    {
        var measure = Rule(".wmeasure");
        Assert.Contains("width: 100%", measure);
        Assert.Contains("max-width: var(--wmeasure, var(--measure-table))", measure);
        Assert.Contains("margin-inline: auto", measure);
        Assert.Contains("--wmeasure: var(--measure-table-narrow)", Rule(".wmeasure--narrow"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void A_row_of_tiles_stops_at_its_tiles_width(int tiles)
    {
        var rule = Rule($".metrics:has(> :nth-child({tiles}):last-child)");
        Assert.Contains($"max-width: calc({tiles} * var(--metric-max) + {(tiles == 2 ? "" : $"{tiles - 1} * ")}var(--space-2))", rule);
        Assert.Contains("max-width: var(--metric-max)", Rule(".metrics:has(> :only-child)"));
    }
}
