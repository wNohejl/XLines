using System.Text.RegularExpressions;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// A table reads at a table's width (design review, 26 November). Maximised, the Journal's ledger
/// put "Chicago Bears −1.5" at one edge and its price, stake and result 700px away, the toolbar's
/// keys 900px from its filters; the Players list, the Runs log, the Player's game log and the
/// Incidents lists did the same. A window whose content is a table stands at the Desk's table
/// measure (.wmeasure), centred; a table among cards takes two tracks (.wsection--pair), so a
/// wider flow gains a card beside it rather than a gutter inside every row. Read from the source:
/// most of these windows render against a database.
/// </summary>
public class TableMeasureTests
{
    private static string Panel(string name) => File.ReadAllText(Path.Combine(
        Repo.Root(), "src", "XLines.Web", "Components", "Panels", name)).ReplaceLineEndings("\n");

    /// <summary>Each window's markup opens its measure before its first control and closes it last.</summary>
    [Theory]
    [InlineData("JournalPanel.razor", "<div class=\"wmeasure journal\">", "<DeskTabs")]
    [InlineData("RunsPanel.razor", "<div class=\"wmeasure\">", "<PanelToolbar")]
    [InlineData("PlayersPanel.razor", "<div class=\"wmeasure wmeasure--narrow\">", "<PanelToolbar")]
    [InlineData("PlayerPanel.razor", "<div class=\"wmeasure\">", "@if (Failed)")]
    [InlineData("IncidentsPanel.razor", "<div class=\"wmeasure incidents\">", "@if (Failed)")]
    public void A_window_of_tables_stands_at_the_table_measure(string panel, string opener, string first)
    {
        var source = Panel(panel);
        var markup = source[..source.IndexOf("\n@code {", StringComparison.Ordinal)];

        var open = markup.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(open >= 0, $"{panel} does not open {opener}");
        Assert.True(markup.IndexOf(first, open, StringComparison.Ordinal) > open, $"{panel}'s {first} is outside its measure");

        // Balanced: as many <div openings as closings in the markup, so the measure holds it all.
        Assert.Equal(Regex.Matches(markup, @"<div[\s>]").Count, Regex.Matches(markup, @"</div>").Count);
    }

    /// <summary>
    /// The tables among cards take two tracks, never the whole of a wide row: Ops' health, feeds and
    /// (when there are any) alerts, and History's seasons with the recent days a track beside them.
    /// </summary>
    [Fact]
    public void A_table_among_cards_takes_two_tracks()
    {
        var ops = Panel("OpsPanel.razor");
        Assert.Contains("<FormGroup Title=\"Source health\" Class=\"wsection--pair\">", ops);
        Assert.Contains("<section class=\"wsection wsection--pair\" aria-labelledby=\"@($\"{_uid}-feeds\")\">", ops);
        Assert.Contains("<FormGroup Title=\"Open alerts\" Class=\"@(_alerts.Count == 0 ? null : \"wsection--pair\")\">", ops);
        Assert.DoesNotContain("DeskCardSpan.Row", ops);

        var history = Panel("HistoryPanel.razor");
        Assert.Contains("<FormGroup Title=\"Seasons\" Class=\"wsection--pair\">", history);
        Assert.Contains("<FormGroup Title=\"Recent days\" Class=\"hist-recent\">", history);
        Assert.DoesNotContain("DeskCardSpan.Row", history);
    }

    /// <summary>
    /// The bankroll curve spans the rows the cards after it fill, so the one-field bankroll card
    /// does not stand over 400px of nothing beside it.
    /// </summary>
    [Fact]
    public void The_bankroll_curve_spans_the_rows_beside_it()
    {
        Assert.Contains("Span=\"DeskCardSpan.Rest\" Class=\"perf-curve\"", Panel("PerformancePanel.razor"));

        var css = Regex.Replace(Panel("PerformancePanel.razor.css"), @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.Matches(@"@container wflow \(min-width: 856px\) \{\s*\.perf-flow ::deep \.perf-curve \{ grid-row: span 2; \}", css);
        Assert.Matches(@"@container wflow \(min-width: 1292px\) \{\s*\.perf-flow ::deep \.perf-curve \{ grid-row: span 3; \}", css);
    }
}
