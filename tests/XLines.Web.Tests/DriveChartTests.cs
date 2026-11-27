using Bunit;
using XLines.Data.Gamecast;
using XLines.Desk.Testing;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The drive chart: every drive of a game on one field, a row each in game order, each a button
/// that names the drive and hands its sequence back when pressed; the bars on the field frame the
/// field graphic draws, in the side's colour, with the result after them.
/// </summary>
public class DriveChartTests : DeskTestContext
{
    private const int Home = FakeGamecastData.HomeId;
    private const int Away = FakeGamecastData.AwayId;

    private static GamecastDrive Drive(int sequence, int? team, int? start, int? end, string? result,
        int? plays = 3, int? yards = null, bool score = false, int period = 1, bool current = false)
        => FakeGamecastData.Drive(sequence, sequence * 10, sequence * 10 + 2, current, result, plays, yards, score: score) with
        {
            TeamId = team,
            TeamAbbrev = team == Home ? "WSH" : team == Away ? "CIN" : null,
            StartYardLine = start,
            EndYardLine = end,
            StartPeriod = period,
        };

    /// <summary>The first drives of game 2049 (CIN @ WSH), as the store holds them.</summary>
    private static readonly IReadOnlyList<GamecastDrive> Game =
    [
        Drive(1, Away, 76, 76, "Punt", plays: 3, yards: 0),
        Drive(2, Home, 19, 20, "Punt", plays: 3, yards: 1),
        Drive(4, Home, 17, 42, "Punt", plays: 6, yards: 25),
        Drive(3, Away, 69, 75, "Punt", plays: 6, yards: -6),
        Drive(7, Away, 91, 20, "Field Goal", plays: 15, yards: 71, score: true, period: 2),
        Drive(10, Home, 21, 100, "Touchdown", plays: 9, yards: 79, score: true, period: 2),
        Drive(11, Away, 69, 69, "End of Half", plays: 1, yards: -1, period: 2),
    ];

    private IRenderedComponent<DriveChart> Render(IReadOnlyList<GamecastDrive>? drives = null,
        Action<ComponentParameterCollectionBuilder<DriveChart>>? more = null)
        => RenderComponent<DriveChart>(p =>
        {
            p.Add(s => s.Drives, drives ?? Game).Add(s => s.HomeTeamId, Home)
                .Add(s => s.HomeAbbrev, "WSH").Add(s => s.AwayAbbrev, "CIN").Add(s => s.Sport, "nfl");
            more?.Invoke(p);
        });

    [Fact]
    public void Each_drive_is_a_button_in_game_order_named_in_a_sentence()
    {
        var cut = Render();

        var rows = cut.FindAll("button.drivechart__row");
        Assert.Equal(7, rows.Count);
        Assert.Equal(new[] { "1", "2", "3", "4", "7", "10", "11" }, rows.Select(r => r.GetAttribute("data-sequence")));
        Assert.All(rows, r => Assert.Equal("button", r.GetAttribute("type")));

        Assert.Equal("Drive 4, WSH, 6 plays, 25 yards, Punt", rows[3].GetAttribute("aria-label"));
        Assert.Equal("Drive 3, CIN, 6 plays, -6 yards, Punt", rows[2].GetAttribute("aria-label"));
        Assert.Equal("Drive 7, CIN, 1 play, -1 yard, End of Half", rows[6].GetAttribute("aria-label"));
        Assert.Equal("Drive 5, CIN, 15 plays, 71 yards, Field Goal", rows[4].GetAttribute("aria-label"));
    }

    [Fact]
    public void The_sides_are_coloured_by_who_had_the_ball()
    {
        var cut = Render();
        var rows = cut.FindAll("button.drivechart__row");

        Assert.Contains("drivechart__row--away", rows[0].ClassName);
        Assert.Contains("drivechart__row--home", rows[1].ClassName);
        Assert.DoesNotContain("drivechart__row--home", rows[0].ClassName);
        Assert.Equal("WSH", rows[1].QuerySelector(".drivechart__abbrev")!.TextContent);
        Assert.Equal("CIN", rows[0].QuerySelector(".drivechart__abbrev")!.TextContent);
    }

    /// <summary>
    /// CIN's field-goal drive from its 9 (91 out) to the WSH 20 is 15.833% to 75% of the field,
    /// going right; WSH's touchdown from its 21 ends on the away goal line (8.333%), going left.
    /// </summary>
    [Fact]
    public void A_bar_runs_from_the_drive_s_start_to_its_end_on_the_field_frame()
    {
        var cut = Render();
        var rows = cut.FindAll("button.drivechart__row");

        var fieldGoal = rows[4];
        Assert.Equal("--left:15.833; --width:59.167", fieldGoal.QuerySelector(".drivechart__bar")!.GetAttribute("style"));
        var fgEnd = fieldGoal.QuerySelector(".drivechart__end")!;
        Assert.Equal("--end:75", fgEnd.GetAttribute("style"));
        Assert.Contains("drivechart__end--right", fgEnd.ClassName);

        var touchdown = rows[5].QuerySelector(".drivechart__end")!;
        Assert.Equal("--end:8.333", touchdown.GetAttribute("style"));
        Assert.Contains("drivechart__end--left", touchdown.ClassName);
    }

    [Fact]
    public void The_result_is_a_short_mark_and_a_score_or_a_turnover_is_weighted()
    {
        var drives = Game.Append(Drive(12, Home, 30, 55, "Interception", plays: 4, yards: 25, period: 3)).ToList();
        var cut = Render(drives);
        var rows = cut.FindAll("button.drivechart__row");

        Assert.Equal(new[] { "Punt", "Punt", "Punt", "Punt", "FG", "TD", "Half", "INT" },
            rows.Select(r => r.QuerySelector(".drivechart__result")!.TextContent));

        Assert.Contains("drivechart__row--score", rows[4].ClassName);
        Assert.Contains("drivechart__row--score", rows[5].ClassName);
        Assert.Contains("drivechart__row--turnover", rows[7].ClassName);
        Assert.Contains("drivechart__row--other", rows[0].ClassName);
    }

    [Fact]
    public void Pressing_a_drive_hands_back_its_sequence_and_the_selected_one_is_pressed()
    {
        int? chosen = null;
        var cut = Render(more: p => p.Add(s => s.Selected, 7)
            .Add(s => s.OnSelect, (int sequence) => { chosen = sequence; }));

        var rows = cut.FindAll("button.drivechart__row");
        Assert.Equal("true", rows[4].GetAttribute("aria-pressed"));
        Assert.Contains("drivechart__row--selected", rows[4].ClassName);
        Assert.All(rows.Where((_, i) => i != 4), r => Assert.Equal("false", r.GetAttribute("aria-pressed")));
        Assert.Single(cut.FindAll(".drivechart__row--selected"));

        rows[5].Click();
        Assert.Equal(10, chosen);
    }

    [Fact]
    public void A_drive_with_no_start_or_end_keeps_its_row_with_no_bar_and_its_result_in_words()
    {
        var cut = Render(
        [
            Drive(1, Away, null, 40, "Punt"),
            Drive(2, Home, 35, null, null, plays: null, current: true),
            Drive(3, null, 20, 30, "Fumble"),
        ]);

        var rows = cut.FindAll("button.drivechart__row");
        Assert.Equal(3, rows.Count);
        Assert.Empty(cut.FindAll(".drivechart__bar"));
        Assert.Equal("Punt", rows[0].QuerySelector(".drivechart__result")!.TextContent);

        // A drive in progress with no result yet says so; with no offensive-play count, its plays held are counted.
        Assert.Equal("Now", rows[1].QuerySelector(".drivechart__result")!.TextContent);
        Assert.Equal("Drive 2, WSH, 3 plays, in progress", rows[1].GetAttribute("aria-label"));

        // Nobody's drive is named without a side.
        Assert.Equal("Drive 3, 3 plays, Fumble", rows[2].GetAttribute("aria-label"));
        Assert.DoesNotContain("drivechart__row--home", rows[2].ClassName);
        Assert.DoesNotContain("drivechart__row--away", rows[2].ClassName);
    }

    [Fact]
    public void A_quiet_break_marks_each_new_quarter_and_the_field_is_drawn_once_behind_the_rows()
    {
        var drives = Game.Append(Drive(21, Home, 25, 40, "Punt", period: 5)).ToList();
        var cut = Render(drives);

        var breaks = cut.FindAll(".drivechart__period");
        Assert.Equal(new[] { "Q2", "OT" }, breaks.Select(b => b.TextContent.Trim()));
        Assert.All(breaks, b => Assert.Equal("true", b.GetAttribute("aria-hidden")));

        var field = cut.Find(".drivechart__field");
        Assert.Equal("true", field.GetAttribute("aria-hidden"));
        Assert.Equal(2, field.QuerySelectorAll(".drivechart__zone").Length);
        Assert.Single(field.QuerySelectorAll(".drivechart__mid"));
        Assert.Single(cut.FindAll(".drivechart__field"));
    }

    [Fact]
    public void No_drives_draws_nothing()
    {
        var cut = Render([]);

        Assert.Empty(cut.FindAll(".drivechart"));
    }

    [Fact]
    public void The_sheet_spends_tokens_draws_no_outline_on_a_row_and_rings_the_focus()
    {
        var css = Sheet();

        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b|rgba?\(", css);
        // The sides are the desk's chart-side pair (the ink and the neutral, re-pointed on Graphite),
        // never the raw tokens, so a neutral accent's two sides still part.
        Assert.Contains("var(--chart-side-home)", css);
        Assert.Contains("var(--chart-side-away)", css);
        Assert.DoesNotContain("var(--accent-ink)", css);
        Assert.DoesNotContain("var(--chart-neutral", css);
        Assert.Contains("var(--focus-ring-inset)", css);
        Assert.Contains("var(--accent-wash)", css);
        Assert.Matches(@"\.drivechart__row\s*\{[^}]*border:\s*0", css);
    }

    /// <summary>
    /// Not by colour alone: the home side's bars are solid, the away side's hollow — an inset line in
    /// the side's tone round a clear bar — and a scoring away drive stays hollow in the positive.
    /// </summary>
    [Fact]
    public void An_away_bar_is_hollow_and_a_home_bar_solid()
    {
        var css = Sheet();

        Assert.Matches(@"\.drivechart__row--home \.drivechart__bar[^{]*\{[^}]*background:\s*var\(--chart-side-home\)", css);
        Assert.Matches(@"\.drivechart__row--away \.drivechart__bar\s*\{[^}]*background:\s*transparent;[^}]*box-shadow:\s*inset 0 0 0 [\d.]+px var\(--chart-side-away\)", css);
        Assert.Matches(@"\.drivechart__row--away\.drivechart__row--score \.drivechart__bar\s*\{[^}]*background:\s*transparent;[^}]*box-shadow:\s*inset 0 0 0 [\d.]+px var\(--state-positive\)", css);
    }

    /// <summary>The key above the field says which is which: the away side's hollow mark on the left, as its end zone is, the home side's solid on the right.</summary>
    [Fact]
    public void A_key_names_the_hollow_side_and_the_solid_side()
    {
        var cut = Render();

        var key = cut.Find(".drivechart__legend");
        Assert.Equal("true", key.GetAttribute("aria-hidden"));
        var items = key.QuerySelectorAll(".drivechart__keyitem");
        Assert.Equal(new[] { "CIN", "WSH" }, items.Select(i => i.TextContent.Trim()));
        Assert.NotNull(items[0].QuerySelector(".drivechart__swatch--away"));
        Assert.NotNull(items[1].QuerySelector(".drivechart__swatch--home"));
    }

    /// <summary>A row is at least 28px tall, the whole row the button, so the target is the row and not its bar.</summary>
    [Fact]
    public void A_row_is_at_least_twenty_eight_pixels_and_the_whole_row_is_the_target()
    {
        var css = Sheet();

        var row = System.Text.RegularExpressions.Regex.Match(css, @"--dc-row:\s*(\d+)px");
        Assert.True(row.Success);
        Assert.True(int.Parse(row.Groups[1].Value) >= 28, $"--dc-row is {row.Groups[1].Value}px");
        Assert.Matches(@"\.drivechart__row\s*\{[^}]*width:\s*100%[^}]*min-height:\s*var\(--dc-row\)", css);
    }

    /// <summary>
    /// One Tab stop for the whole list — a roving tabindex, on the chosen drive or the first when
    /// none is chosen — rather than twenty-odd stops a keyboard reader has to walk past.
    /// </summary>
    [Fact]
    public void The_list_is_one_tab_stop_on_the_chosen_drive_or_the_first()
    {
        var none = Render();
        var rows = none.FindAll("button.drivechart__row");
        Assert.Equal("0", rows[0].GetAttribute("tabindex"));
        Assert.All(rows.Skip(1), r => Assert.Equal("-1", r.GetAttribute("tabindex")));

        var chosen = Render(more: p => p.Add(s => s.Selected, 7));
        rows = chosen.FindAll("button.drivechart__row");
        Assert.Single(rows, r => r.GetAttribute("tabindex") == "0");
        Assert.Equal("0", rows[4].GetAttribute("tabindex"));
    }

    /// <summary>
    /// Down and Up move the focus a row, Home and End to the ends, and the tab stop goes with it;
    /// nothing is chosen until Enter or Space presses the row (the button's own keys).
    /// </summary>
    [Fact]
    public void Arrows_Home_and_End_move_the_focus_and_the_tab_stop_without_choosing()
    {
        int? chosen = null;
        var cut = Render(more: p => p.Add(s => s.OnSelect, (int sequence) => { chosen = sequence; }));

        int Stop() => cut.FindAll("button.drivechart__row").ToList().FindIndex(r => r.GetAttribute("tabindex") == "0");
        void Press(string key) => cut.Find("ol.drivechart").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = key });

        Press("ArrowDown");
        Assert.Equal(1, Stop());
        Press("ArrowDown");
        Press("ArrowUp");
        Assert.Equal(1, Stop());
        Press("End");
        Assert.Equal(6, Stop());
        Press("ArrowDown");
        Assert.Equal(6, Stop());
        Press("Home");
        Assert.Equal(0, Stop());

        Assert.Null(chosen);
        // Five moves (Down at the end goes nowhere): each one focuses the row it lands on.
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 5);

        // A shortcut is the browser's, and Left and Right are not this list's.
        cut.Find("ol.drivechart").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End", CtrlKey = true });
        Press("ArrowRight");
        Assert.Equal(0, Stop());
    }

    /// <summary>
    /// Choosing a drive brings its plays into view: the chart hands the browser the drive's
    /// sequence, and reveal.js scrolls the nearest group marked data-drive with it into view —
    /// at once under reduced motion. Off when the caller names no attribute.
    /// </summary>
    [Fact]
    public void Choosing_a_drive_reveals_its_plays()
    {
        var module = JSInterop.SetupModule("./js/reveal.js");
        module.SetupVoid("revealNear", _ => true);

        var cut = Render();
        cut.FindAll("button.drivechart__row")[5].Click();

        var call = Assert.Single(module.Invocations, i => i.Identifier == "revealNear");
        Assert.Equal("[data-drive=\"10\"]", call.Arguments[1]);

        var off = Render(more: p => p.Add(s => s.RevealAttribute, null));
        off.FindAll("button.drivechart__row")[0].Click();
        Assert.Single(module.Invocations, i => i.Identifier == "revealNear");
    }

    private static string Sheet()
        => File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Web", "Components", "Snippets", "DriveChart.razor.css"));
}
