using Bunit;
using XLines.Data.Gamecast;
using XLines.Desk.Testing;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The Gamecast's field graphic: the field always, and once a side has the ball the ball, the
/// line of scrimmage, the line to gain and the drive played out on it — one arc a play, in the
/// side's colour — with the play in words under it, never on the turf. In replay, a finished
/// drive: its arcs, its start and its end with the result named, and no ball or lines.
/// </summary>
public class FieldGraphicTests : DeskTestContext
{
    private static GamecastPlay Play(long sequence, int start, int end, bool penalty = false, string type = "Pass Reception",
        bool scoring = false, bool turnover = false)
        => FakeGamecastData.Play(sequence, 1, "play") with
        {
            Type = type,
            IsPenalty = penalty,
            IsScoring = scoring,
            IsTurnover = turnover,
            Start = new GamecastSituation(1, 10, start, null, null, null, FakeGamecastData.AwayId),
            End = new GamecastSituation(2, 6, end, null, null, null, FakeGamecastData.AwayId),
        };

    private IRenderedComponent<FieldGraphic> Render(Action<ComponentParameterCollectionBuilder<FieldGraphic>>? more = null)
        => RenderComponent<FieldGraphic>(p =>
        {
            p.Add(s => s.HomeAbbrev, "WSH").Add(s => s.AwayAbbrev, "PHI").Add(s => s.Sport, "nfl")
                .Add(s => s.Label, "Field: PHI ball, 2nd and 6 at WSH 34");
            more?.Invoke(p);
        });

    [Fact]
    public void A_spotted_ball_draws_the_ball_the_line_to_gain_and_the_line_of_scrimmage()
    {
        var cut = Render(p => p.Add(s => s.YardLine, 34).Add(s => s.Distance, 6).Add(s => s.HomeBall, false));

        var svg = cut.Find("svg");
        Assert.Equal("img", svg.GetAttribute("role"));
        Assert.Equal("Field: PHI ball, 2nd and 6 at WSH 34", svg.GetAttribute("aria-label"));

        // WSH 34 is x 76; six to go rightward puts the line to gain at x 82.
        var ball = cut.Find(".fieldgraphic__ball");
        Assert.Equal("76", ball.GetAttribute("data-spot"));
        Assert.Contains("translateX(76px)", ball.GetAttribute("style"));
        Assert.Contains("translateX(82px)", cut.Find(".fieldgraphic__gain").GetAttribute("style"));
        Assert.Single(cut.FindAll(".fieldgraphic__scrim"));
        Assert.Empty(cut.FindAll(".fieldgraphic__red"));

        // The ball sits on the ground the arcs land on, its arrow pointing rightward for the away side.
        Assert.Equal("27", cut.Find(".fieldgraphic__spot").GetAttribute("cy"));
        Assert.Equal(GamecastTelemetry.ArrowPoints(leftward: false), cut.Find(".fieldgraphic__arrow").GetAttribute("points"));
    }

    [Fact]
    public void With_nobody_on_the_ball_the_field_draws_alone()
    {
        var cut = Render(p => p.Add(s => s.YardLine, 34).Add(s => s.Distance, 6)
            .Add(s => s.DrivePlays, [Play(1, 75, 70)]).Add(s => s.DriveStartYardLine, 75));

        Assert.Empty(cut.FindAll(".fieldgraphic__ball"));
        Assert.Empty(cut.FindAll(".fieldgraphic__gain"));
        Assert.Empty(cut.FindAll(".fieldgraphic__scrim"));
        Assert.Empty(cut.FindAll(".fieldgraphic__arc"));
        Assert.Empty(cut.FindAll(".fieldgraphic__band"));
        Assert.Empty(cut.FindAll(".fieldgraphic__start"));
        Assert.Equal(2, cut.FindAll(".fieldgraphic__zone").Count);
        Assert.Equal(9, cut.FindAll(".fieldgraphic__yard").Count);
        Assert.Equal("0 0 120 36", cut.Find("svg").GetAttribute("viewBox"));
    }

    [Fact]
    public void Each_play_with_both_ends_is_one_arc_in_the_side_s_colour()
    {
        var plays = new[]
        {
            Play(1, 75, 71, type: "Rush"),
            Play(2, 71, 71, type: "Pass Incompletion"),
            FakeGamecastData.Play(3, 1, "no spot"),
            Play(4, 71, 66, penalty: true, type: "Penalty"),
            Play(5, 66, 70, type: "Sack"),
            Play(6, 70, 34),
        };

        var cut = Render(p => p.Add(s => s.YardLine, 34).Add(s => s.Distance, 10).Add(s => s.HomeBall, false)
            .Add(s => s.DrivePlays, plays).Add(s => s.DriveStartYardLine, 75));

        var arcs = cut.FindAll(".fieldgraphic__arc");
        Assert.Equal(5, arcs.Count);
        Assert.Equal(new[] { "run", "nogain", "penalty", "loss", "pass" }, arcs.Select(a => a.GetAttribute("data-kind")));
        Assert.All(arcs, a => Assert.Contains("fieldgraphic__arc--away", a.ClassName));
        Assert.DoesNotContain(arcs, a => a.ClassName!.Contains("fieldgraphic__arc--home"));

        // Paths for the moves, a dot for the incompletion at its spot (71 out, x 39).
        Assert.Equal("path", arcs[0].TagName, ignoreCase: true);
        Assert.Equal("circle", arcs[1].TagName, ignoreCase: true);
        Assert.Equal("39", arcs[1].GetAttribute("cx"));

        // The latest is emphasised and draws itself in; no other arc does.
        Assert.Contains("fieldgraphic__arc--latest", arcs[^1].ClassName);
        Assert.Equal("1", arcs[^1].GetAttribute("pathLength"));
        Assert.Single(cut.FindAll(".fieldgraphic__arc--latest"));
        Assert.Single(cut.FindAll(".fieldgraphic__arc--draw"));
        Assert.Null(arcs[0].GetAttribute("pathLength"));

        // The covered ground runs from the drive's start (x 35) to the ball (x 76), and the start is marked.
        var band = cut.Find(".fieldgraphic__band");
        Assert.Equal("35", band.GetAttribute("x"));
        Assert.Equal("41", band.GetAttribute("width"));
        Assert.Equal("35", cut.Find(".fieldgraphic__start").GetAttribute("data-x"));
    }

    [Fact]
    public void A_home_drive_s_arcs_are_the_home_colour()
    {
        var cut = Render(p => p.Add(s => s.YardLine, 40).Add(s => s.Distance, 10).Add(s => s.HomeBall, true)
            .Add(s => s.DrivePlays, [Play(1, 25, 40, type: "Rush")]).Add(s => s.DriveStartYardLine, 25));

        var arc = cut.Find(".fieldgraphic__arc");
        Assert.Contains("fieldgraphic__arc--home", arc.ClassName);
        Assert.DoesNotContain("fieldgraphic__arc--away", arc.ClassName);
        Assert.Equal(GamecastTelemetry.ArrowPoints(leftward: true), cut.Find(".fieldgraphic__arrow").GetAttribute("points"));
    }

    [Fact]
    public void The_scoring_play_and_the_turnover_are_marked_and_a_dashed_latest_does_not_draw_in()
    {
        var cut = Render(p => p.Add(s => s.YardLine, 0).Add(s => s.HomeBall, false)
            .Add(s => s.DrivePlays,
            [
                Play(1, 40, 20, turnover: true, type: "Pass Interception Return"),
                Play(2, 20, 0, scoring: true, type: "Passing Touchdown"),
                Play(3, 0, 0, penalty: true, type: "Penalty"),
            ]));

        Assert.Contains("fieldgraphic__arc--turnover", cut.Find("[data-kind='pass']").ClassName);
        Assert.Single(cut.FindAll(".fieldgraphic__arc--turnover"));
        Assert.Single(cut.FindAll(".fieldgraphic__arc--score"));
        Assert.Contains("fieldgraphic__arc--latest", cut.Find(".fieldgraphic__arc--penalty").ClassName);
        Assert.Empty(cut.FindAll(".fieldgraphic__arc--draw"));
    }

    [Fact]
    public void The_red_zone_is_washed_once_the_ball_is_inside_it()
    {
        var cut = Render(p => p.Add(s => s.YardLine, 15).Add(s => s.Distance, 5).Add(s => s.HomeBall, false));

        Assert.Equal("90", cut.Find(".fieldgraphic__red").GetAttribute("x"));
    }

    [Fact]
    public void The_caption_says_the_situation_and_the_last_play_under_the_field()
    {
        var cut = Render(p => p.Add(s => s.YardLine, 34).Add(s => s.HomeBall, false)
            .Add(s => s.Situation, "2nd & 6 at WSH 34")
            .Add(s => s.LastPlay, "J.Hurts pass short right to A.Brown for 9 yards"));

        var caption = cut.Find("p.fieldgraphic__caption");
        Assert.Contains("2nd &amp; 6 at WSH 34", caption.InnerHtml);
        Assert.Contains("PHI", cut.Find(".fieldgraphic__side").TextContent);
        Assert.Equal("J.Hurts pass short right to A.Brown for 9 yards", cut.Find(".fieldgraphic__lastplay").TextContent);

        // The play in words is under the field, not on it: the SVG's text is numbers and abbreviations.
        var onField = cut.FindAll("svg text").Select(t => t.TextContent.Trim()).ToList();
        Assert.All(onField, t => Assert.True(int.TryParse(t, out _) || t is "PHI" or "WSH", t));
    }

    [Fact]
    public void With_neither_the_situation_nor_a_play_there_is_no_caption()
    {
        var cut = Render(p => p.Add(s => s.YardLine, 34).Add(s => s.HomeBall, true).Add(s => s.Situation, " "));

        Assert.Empty(cut.FindAll(".fieldgraphic__caption"));
    }

    // ---- Replay: a drive that is over ---------------------------------------------------------

    /// <summary>
    /// Game 2049's drive 9, abridged: CIN (away) from its 23 (77 out, x 33) to a touchdown, which
    /// ESPN ends at 0 on the home-goal frame — the home goal line, x 110.
    /// </summary>
    private static readonly GamecastPlay[] Drive9 =
    [
        Play(1, 77, 72),
        Play(2, 72, 78, type: "Sack"),
        Play(3, 78, 78, type: "Timeout"),
        Play(4, 78, 31),
        Play(5, 31, 0, scoring: true, type: "Passing Touchdown"),
    ];

    // Each parameter once: bUnit refuses one added twice, so a test's variation is an argument.
    private IRenderedComponent<FieldGraphic> Replay(string? result = "Touchdown", int? end = 0, bool? homeBall = false,
        string label = "Field: drive 9, PHI", IReadOnlyList<GamecastPlay>? plays = null)
        => RenderComponent<FieldGraphic>(p => p
            .Add(s => s.HomeAbbrev, "WSH").Add(s => s.AwayAbbrev, "PHI").Add(s => s.Sport, "nfl")
            .Add(s => s.Replay, true).Add(s => s.HomeBall, homeBall)
            .Add(s => s.Label, label)
            .Add(s => s.YardLine, 50).Add(s => s.Distance, 10)
            .Add(s => s.DriveStartYardLine, 77).Add(s => s.DriveEndYardLine, end)
            .Add(s => s.DriveResult, result)
            .Add(s => s.DrivePlays, plays ?? Drive9));

    [Fact]
    public void A_replay_draws_the_drive_s_arcs_with_no_ball_and_no_lines()
    {
        var cut = Replay();

        Assert.Empty(cut.FindAll(".fieldgraphic__ball"));
        Assert.Empty(cut.FindAll(".fieldgraphic__gain"));
        Assert.Empty(cut.FindAll(".fieldgraphic__scrim"));
        Assert.Empty(cut.FindAll(".fieldgraphic__red"));

        var arcs = cut.FindAll(".fieldgraphic__arc");
        Assert.Equal(new[] { "pass", "loss", "pass", "pass" }, arcs.Select(a => a.GetAttribute("data-kind")));
        Assert.Contains("fieldgraphic__arc--score", arcs[^1].ClassName);
        Assert.Contains("fieldgraphic--replay", cut.Find(".fieldgraphic").ClassName);
    }

    [Fact]
    public void A_replay_marks_where_the_drive_started_and_where_it_ended_with_its_result()
    {
        var cut = Replay();

        Assert.Equal("33", cut.Find(".fieldgraphic__start").GetAttribute("data-x"));

        var end = cut.Find(".fieldgraphic__end");
        Assert.Equal("110", end.GetAttribute("data-x"));
        Assert.Contains("fieldgraphic__end--score", end.ClassName);

        // The result under the end, drawn in from the edge so a long one fits.
        var result = cut.Find(".fieldgraphic__result");
        Assert.Equal("Touchdown", result.TextContent);
        Assert.Equal("107", result.GetAttribute("x"));

        // The covered ground runs from the start (x 33) to the end (x 110).
        var band = cut.Find(".fieldgraphic__band");
        Assert.Equal(("33", "77"), (band.GetAttribute("x"), band.GetAttribute("width")));

        // And the result is in the image's name.
        Assert.Equal("Field: drive 9, PHI. Result: Touchdown", cut.Find("svg").GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData("Punt", "fieldgraphic__end--away")]
    [InlineData("Interception", "fieldgraphic__end--turnover")]
    [InlineData("Field Goal", "fieldgraphic__end--score")]
    public void A_replay_s_end_is_weighted_by_what_the_drive_came_to(string result, string expected)
    {
        var cut = Replay(result: result, end: 40);

        Assert.Contains(expected, cut.Find(".fieldgraphic__end").ClassName);
        Assert.Equal(result, cut.Find(".fieldgraphic__result").TextContent);
    }

    [Fact]
    public void A_replay_whose_label_already_says_the_result_does_not_say_it_twice()
    {
        var cut = Replay(label: "Field: PHI drive, 5 plays, 77 yards, Touchdown");

        Assert.Equal("Field: PHI drive, 5 plays, 77 yards, Touchdown", cut.Find("svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void A_replay_with_no_end_or_result_draws_its_arcs_and_where_the_last_landed()
    {
        var cut = Replay(result: null, end: null);

        Assert.Empty(cut.FindAll(".fieldgraphic__end"));
        Assert.Empty(cut.FindAll(".fieldgraphic__result"));
        Assert.Equal("110", cut.Find(".fieldgraphic__landing").GetAttribute("cx"));
        Assert.Equal(4, cut.FindAll(".fieldgraphic__arc").Count);
        Assert.Equal("Field: drive 9, PHI", cut.Find("svg").GetAttribute("aria-label"));
    }

    /// <summary>A drive whose plays carry no situations still has its start, end and result.</summary>
    [Fact]
    public void A_replay_whose_plays_have_no_spots_still_marks_its_ends()
    {
        var cut = Replay(plays: [FakeGamecastData.Play(1, 1, "no spot"), FakeGamecastData.Play(2, 1, "no spot")]);

        Assert.Empty(cut.FindAll(".fieldgraphic__arc"));
        Assert.Single(cut.FindAll(".fieldgraphic__start"));
        Assert.Single(cut.FindAll(".fieldgraphic__end"));
        Assert.Equal("Touchdown", cut.Find(".fieldgraphic__result").TextContent);
    }

    [Fact]
    public void A_replay_with_no_side_draws_the_field_alone()
    {
        var cut = Replay(homeBall: null);

        Assert.Empty(cut.FindAll(".fieldgraphic__arc"));
        Assert.Empty(cut.FindAll(".fieldgraphic__end"));
        Assert.Empty(cut.FindAll(".fieldgraphic__start"));
        Assert.Equal("Field: drive 9, PHI", cut.Find("svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void The_sheet_draws_no_outline_spends_the_accent_only_as_a_sides_ink_and_stops_under_reduced_motion()
    {
        var css = File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Web", "Components", "Snippets", "FieldGraphic.razor.css"));

        Assert.DoesNotContain("border:", css);
        // The accent reaches the field only as the home side's ink, a series' colour; nothing else wears it.
        Assert.DoesNotMatch(@"var\(--accent(?!-ink\))", css);
        Assert.Contains("max-width: 760px", css);
        Assert.Contains("prefers-reduced-motion: reduce", css);

        // One animation, the latest arc drawing in, and it stops under reduced motion.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(css, @"animation:\s*fieldgraphic-draw"));
        var reduced = css[css.IndexOf("prefers-reduced-motion: reduce", StringComparison.Ordinal)..];
        Assert.Contains(".fieldgraphic__arc--draw", reduced);
        Assert.Contains("animation: none", reduced);

        // The sides spend DeskChart's pair for two series with no state: away the chart neutral,
        // home the accent's ink (the dim neutral all but vanished on a dark field); no literal colour.
        Assert.Contains("var(--chart-neutral)", css);
        Assert.Contains("var(--accent-ink)", css);
        Assert.DoesNotContain("var(--chart-neutral-dim)", css);
        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b|rgba?\(", css);
    }
}
