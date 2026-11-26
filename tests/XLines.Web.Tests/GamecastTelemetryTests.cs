using XLines.Core.Entities;
using XLines.Data.CrossReference;
using XLines.Data.Gamecast;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The numbers behind the Gamecast's field graphic and team comparison
/// (<see cref="GamecastTelemetry"/>), pinned without rendering: where each play of the drive
/// sits on the field's x (110 − yard line, on the home-goal frame), and each comparison row,
/// including the ones left out when the data cannot say them.
/// </summary>
public class GamecastTelemetryTests
{
    private const int Home = FakeGamecastData.HomeId;
    private const int Away = FakeGamecastData.AwayId;

    private static GamecastPlay Play(int? start, int? end, int? team = Away, string type = "Rush",
        bool penalty = false, bool scoring = false, bool turnover = false, long sequence = 1)
        => FakeGamecastData.Play(sequence, 1, "play") with
        {
            Type = type,
            TeamId = team,
            IsPenalty = penalty,
            IsScoring = scoring,
            IsTurnover = turnover,
            Start = start is null ? null : new GamecastSituation(1, 10, start, null, null, null, team),
            End = end is null ? null : new GamecastSituation(2, 6, end, null, null, null, team),
        };

    // ---- Drive arcs -------------------------------------------------------------------------

    /// <summary>
    /// The away side at its own 25 is 75 yards from the home goal (x 35); four yards gained
    /// rightward puts it at 71 (x 39). A rush stays on the ground: its apex no more than
    /// <see cref="GamecastTelemetry.RunRiseMax"/> above the baseline.
    /// </summary>
    [Fact]
    public void An_away_rush_from_its_own_25_for_four_is_a_low_arc_rightward()
    {
        var arc = Assert.Single(GamecastTelemetry.DriveArcs([Play(75, 71)], homeOffence: false));

        Assert.Equal((35d, 39d), (arc.From, arc.To));
        Assert.Equal(DriveArcKind.Run, arc.Kind);
        Assert.True(arc.Apex < GamecastTelemetry.Baseline);
        Assert.True(GamecastTelemetry.Baseline - arc.Apex <= GamecastTelemetry.RunRiseMax);
        Assert.True(arc.Latest);
        Assert.False(arc.Score || arc.Turnover);

        // Rise 0.8 + 0.1 × 4 = 1.2: the control point twice that above the ground at 27.
        Assert.Equal("M 35 27 Q 37 24.6 39 27", arc.Path);
    }

    /// <summary>The home side at its own 30 (x 80) gains twelve to its 42 (x 68): leftward.</summary>
    [Fact]
    public void A_home_gain_runs_leftward()
    {
        var arc = Assert.Single(GamecastTelemetry.DriveArcs([Play(30, 42, Home)], homeOffence: true));

        Assert.Equal((80d, 68d), (arc.From, arc.To));
        Assert.Equal(DriveArcKind.Run, arc.Kind);
    }

    /// <summary>
    /// A pass is thrown: it arcs higher than a run of the same length, and higher the longer it
    /// is, never past the ceiling.
    /// </summary>
    [Fact]
    public void A_pass_arcs_high_by_its_length_and_a_run_stays_low()
    {
        var arcs = GamecastTelemetry.DriveArcs(
        [
            Play(75, 65, type: "Rush", sequence: 1),
            Play(65, 55, type: "Pass Reception", sequence: 2),
            Play(55, 25, type: "Pass Reception", sequence: 3),
            Play(25, 0, type: "Passing Touchdown", scoring: true, sequence: 4),
        ], homeOffence: false);

        Assert.Equal(new[] { DriveArcKind.Run, DriveArcKind.Pass, DriveArcKind.Pass, DriveArcKind.Pass }, arcs.Select(a => a.Kind));
        Assert.True(arcs[1].Apex < arcs[0].Apex, "a ten-yard pass is higher than a ten-yard run");
        Assert.True(arcs[2].Apex < arcs[1].Apex, "a thirty-yard pass is higher than a ten-yard one");
        Assert.All(arcs, a => Assert.True(a.Apex >= GamecastTelemetry.ArcCeiling - 1e-9));

        // Each lands where the next begins.
        for (var i = 1; i < arcs.Count; i++)
            Assert.Equal(arcs[i - 1].To, arcs[i].From);

        Assert.True(arcs[^1].Score);
        Assert.Equal(110, arcs[^1].To);
    }

    /// <summary>A fumble after a catch is typed as the fumble, but its words say it was thrown.</summary>
    [Fact]
    public void A_play_whose_words_say_pass_is_drawn_as_one()
    {
        var play = Play(68, 48, type: "Fumble Recovery (Opponent)", turnover: true) with
        {
            Text = "(Shotgun) J.Browning pass short right to J.Chase to CIN 47 for 15 yards. FUMBLES",
        };

        var arc = Assert.Single(GamecastTelemetry.DriveArcs([play], homeOffence: false));

        Assert.Equal(DriveArcKind.Pass, arc.Kind);
        Assert.True(arc.Turnover);
        Assert.False(arc.Score);
    }

    /// <summary>
    /// An away sack from the home 40 (60 out, x 50) back to its own 33 (67 out, x 43) is a loss of
    /// seven: it dips under the ground, by its length, never past <see cref="GamecastTelemetry.LossDipMax"/>.
    /// </summary>
    [Fact]
    public void A_sack_dips_under_the_ground()
    {
        var arcs = GamecastTelemetry.DriveArcs([Play(60, 67, type: "Sack"), Play(67, 99, type: "Rush", sequence: 2)], homeOffence: false);

        Assert.Equal(DriveArcKind.Loss, arcs[0].Kind);
        Assert.Equal((50d, 43d), (arcs[0].From, arcs[0].To));
        Assert.True(arcs[0].Apex > GamecastTelemetry.Baseline);
        Assert.True(arcs[0].Apex - GamecastTelemetry.Baseline <= GamecastTelemetry.LossDipMax);

        // A run for a loss is a loss too.
        Assert.Equal(DriveArcKind.Loss, arcs[1].Kind);
        Assert.Equal(GamecastTelemetry.Baseline + GamecastTelemetry.LossDipMax, arcs[1].Apex, 6);
    }

    [Fact]
    public void A_penalty_is_dashed_whichever_way_it_went_and_dips_when_it_went_back()
    {
        var arcs = GamecastTelemetry.DriveArcs(
            [Play(50, 45, penalty: true), Play(45, 50, type: "Penalty", sequence: 2)], homeOffence: false);

        Assert.All(arcs, a => Assert.Equal(DriveArcKind.Penalty, a.Kind));
        Assert.True(arcs[0].Apex < GamecastTelemetry.Baseline, "five yards gained: above the ground");
        Assert.True(arcs[1].Apex > GamecastTelemetry.Baseline, "five yards back: under it");
    }

    [Fact]
    public void A_play_for_no_gain_is_a_dot_with_no_path()
    {
        var arc = Assert.Single(GamecastTelemetry.DriveArcs([Play(50, 50, type: "Pass Incompletion")], homeOffence: false));

        Assert.Equal(DriveArcKind.NoGain, arc.Kind);
        Assert.Null(arc.Path);
        Assert.Equal((60d, 60d), (arc.From, arc.To));
        Assert.Equal(GamecastTelemetry.Baseline, arc.Apex);
    }

    /// <summary>
    /// Kickoffs are left out: ESPN files one under the receiving side's drive, but its ball goes
    /// from the kicker's 35 toward the receiver's own goal and would read as the drive's first play
    /// losing sixty yards. Game 2049's first drive opens with one (35 → 76, under CIN).
    /// </summary>
    [Fact]
    public void A_play_missing_either_end_is_skipped_as_are_timeouts_period_ends_and_kickoffs()
    {
        var arcs = GamecastTelemetry.DriveArcs(
        [
            Play(null, 40),
            Play(40, null),
            Play(35, 76, type: "Kickoff"),
            Play(40, 40, type: "Official Timeout"),
            Play(40, 40, type: "Timeout"),
            Play(75, 75, type: "Two-minute warning"),
            Play(75, 75, type: "End Period"),
            Play(75, 75, type: "End of Half"),
            Play(75, 75, type: "End of Game"),
            Play(30, 30, type: "Kickoff Return Touchdown"),
            Play(75, 70),
        ], homeOffence: false);

        var arc = Assert.Single(arcs);
        Assert.Equal(35, arc.From);
    }

    /// <summary>
    /// A punt is the long ball, dotted: from game 2049's first drive, CIN punting from its 24
    /// (76 out, x 34) to where WSH took it at its 19 (x 91).
    /// </summary>
    [Fact]
    public void A_punt_is_a_kick_to_where_it_was_taken()
    {
        var arc = Assert.Single(GamecastTelemetry.DriveArcs([Play(76, 19, type: "Punt")], homeOffence: false));

        Assert.Equal(DriveArcKind.Kick, arc.Kind);
        Assert.Equal((34d, 91d), (arc.From, arc.To));
        Assert.True(arc.Apex < GamecastTelemetry.Baseline - GamecastTelemetry.RunRiseMax);
    }

    /// <summary>
    /// A field goal goes at the posts: ESPN ends a good one on the goal line and a missed one at its
    /// own spot, and both are drawn to the goal line the offence attacks — home's is the away goal
    /// (100 out, x 10), away's the home goal (x 110).
    /// </summary>
    [Fact]
    public void A_field_goal_is_a_kick_to_the_goal_attacked()
    {
        var good = Assert.Single(GamecastTelemetry.DriveArcs([Play(76, 100, Home, type: "Field Goal Good", scoring: true)], homeOffence: true));
        var missed = Assert.Single(GamecastTelemetry.DriveArcs([Play(53, 53, type: "Field Goal Missed")], homeOffence: false));

        Assert.Equal(DriveArcKind.Kick, good.Kind);
        Assert.Equal((34d, 10d), (good.From, good.To));
        Assert.True(good.Score);

        Assert.Equal(DriveArcKind.Kick, missed.Kind);
        Assert.Equal((57d, 110d), (missed.From, missed.To));
        Assert.False(missed.Score);
    }

    /// <summary>From the away 45 (55 out, x 55) to the home 40 (x 70): across the fifty at x 60.</summary>
    [Fact]
    public void A_drive_crosses_midfield_and_only_its_last_arc_is_the_latest()
    {
        var arcs = GamecastTelemetry.DriveArcs(
            [Play(75, 55), Play(55, 40, sequence: 2)], homeOffence: false);

        Assert.Equal(2, arcs.Count);
        Assert.Equal((35d, 55d), (arcs[0].From, arcs[0].To));
        Assert.Equal((55d, 70d), (arcs[1].From, arcs[1].To));
        Assert.True(arcs[1].From < GamecastTelemetry.Midfield && arcs[1].To > GamecastTelemetry.Midfield);
        Assert.False(arcs[0].Latest);
        Assert.True(arcs[1].Latest);
        Assert.Equal(new long[] { 1, 2 }, arcs.Select(a => a.Sequence));
    }

    /// <summary>
    /// An interception is drawn as the pass it was, above the ground whichever way it was returned,
    /// and marked; a pick-six is the turnover, not the offence's score; a safety scores for the
    /// defence and is not the offence's either. Ends are clamped to the field.
    /// </summary>
    [Fact]
    public void A_score_and_a_turnover_are_marked_and_ends_are_clamped_to_the_field()
    {
        var arcs = GamecastTelemetry.DriveArcs(
        [
            Play(8, -3, type: "Rushing Touchdown", scoring: true),
            Play(79, 74, Home, type: "Pass Interception Return", turnover: true, sequence: 2),
            Play(36, 100, Home, type: "Interception Return Touchdown", scoring: true, turnover: true, sequence: 3),
            Play(2, 0, Home, type: "Safety", scoring: true, sequence: 4),
        ], homeOffence: true);

        Assert.True(arcs[0].Score);
        Assert.Equal(110, arcs[0].To);

        Assert.Equal(DriveArcKind.Pass, arcs[1].Kind);
        Assert.True(arcs[1].Turnover);
        Assert.True(arcs[1].Apex < GamecastTelemetry.Baseline, "a returned interception still arcs above the ground");

        Assert.True(arcs[2].Turnover);
        Assert.False(arcs[2].Score);

        Assert.False(arcs[3].Score);
    }

    [Fact]
    public void No_plays_is_no_arcs()
    {
        Assert.Empty(GamecastTelemetry.DriveArcs([], homeOffence: true));
        Assert.Empty(GamecastTelemetry.DriveArcs(null, homeOffence: true));
        Assert.Empty(GamecastTelemetry.DriveArcs([Play(null, null), Play(40, 40, type: "Timeout")], homeOffence: true));
    }

    [Fact]
    public void The_band_runs_from_the_drive_s_start_to_the_ball()
    {
        Assert.Equal(new DriveBand(35, 20), GamecastTelemetry.Band(75, 55));
        Assert.Equal(new DriveBand(80, GamecastTelemetry.MinSegment), GamecastTelemetry.Band(30, 30));
        Assert.Null(GamecastTelemetry.Band(null, 55));
        Assert.Null(GamecastTelemetry.Band(75, null));
    }

    [Fact]
    public void The_arrow_points_the_way_the_ball_is_going_on_the_ground()
    {
        Assert.Equal("-1.4,25.2 -4,27 -1.4,28.8", GamecastTelemetry.ArrowPoints(leftward: true));
        Assert.Equal("1.4,25.2 4,27 1.4,28.8", GamecastTelemetry.ArrowPoints(leftward: false));
    }

    [Fact]
    public void The_result_is_kept_inside_the_field_s_edges()
    {
        Assert.Equal(GamecastTelemetry.ResultInset, GamecastTelemetry.ResultX(10));
        Assert.Equal(FieldGeometry.Width - GamecastTelemetry.ResultInset, GamecastTelemetry.ResultX(110));
        Assert.Equal(60, GamecastTelemetry.ResultX(60));
    }

    // ---- The drive chart's numbers -----------------------------------------------------------

    /// <summary>
    /// Game 2049's drive 7: CIN (away) from its 9 (91 out, x 19) to the WSH 20 (x 90), a field goal.
    /// In percent of the 120-unit field: 15.833 to 75.
    /// </summary>
    [Fact]
    public void A_drive_s_bar_is_its_start_to_its_end_in_percent_of_the_field()
    {
        var bar = GamecastTelemetry.ChartBar(91, 20, homeSide: false)!;

        Assert.Equal(15.833, bar.Left);
        Assert.Equal(59.167, bar.Width);
        Assert.Equal(75, bar.End);
        Assert.False(bar.Leftward);
    }

    /// <summary>A home touchdown ends at 100 on the home-goal frame: the away goal line, x 10, 8.333%.</summary>
    [Fact]
    public void A_home_touchdown_ends_on_the_away_goal_line_going_left()
    {
        var bar = GamecastTelemetry.ChartBar(21, 100, homeSide: true)!;

        Assert.Equal(8.333, bar.End);
        Assert.True(bar.Leftward);
    }

    [Fact]
    public void A_drive_that_went_nowhere_still_has_a_mark_and_one_lacking_an_end_has_no_bar()
    {
        var still = GamecastTelemetry.ChartBar(55, 55, homeSide: true)!;
        Assert.Equal(Math.Round(100 * GamecastTelemetry.MinChartBar / FieldGeometry.Width, 3), still.Width);
        Assert.True(still.Leftward);

        // A drive that lost ground goes the other way.
        Assert.True(GamecastTelemetry.ChartBar(69, 75, homeSide: false)!.Leftward);

        Assert.Null(GamecastTelemetry.ChartBar(null, 20, homeSide: false));
        Assert.Null(GamecastTelemetry.ChartBar(35, null, homeSide: false));
    }

    [Theory]
    [InlineData("Touchdown", true, DriveOutcome.Score)]
    [InlineData("Field Goal", true, DriveOutcome.Score)]
    [InlineData("Punt", false, DriveOutcome.Other)]
    [InlineData("Downs", false, DriveOutcome.Other)]
    [InlineData("Missed FG", false, DriveOutcome.Other)]
    [InlineData("End of Half", false, DriveOutcome.Other)]
    [InlineData("Interception", false, DriveOutcome.Turnover)]
    [InlineData("Interception Touchdown", true, DriveOutcome.Turnover)]
    [InlineData("Fumble", false, DriveOutcome.Turnover)]
    [InlineData("Safety", false, DriveOutcome.Turnover)]
    [InlineData("Blocked FG", false, DriveOutcome.Turnover)]
    [InlineData("Punt Touchdown", true, DriveOutcome.Turnover)]
    [InlineData(null, false, DriveOutcome.Other)]
    public void A_drive_s_outcome_is_whose_points_they_were(string? result, bool isScore, DriveOutcome expected)
        => Assert.Equal(expected, GamecastTelemetry.Outcome(result, isScore));

    [Theory]
    [InlineData("Touchdown", false, "TD")]
    [InlineData("Field Goal", false, "FG")]
    [InlineData("Punt", false, "Punt")]
    [InlineData("Interception", false, "INT")]
    [InlineData("Downs", false, "Downs")]
    [InlineData("Missed FG", false, "FG miss")]
    [InlineData("End of Half", false, "Half")]
    [InlineData("Blocked FG, Downs", false, "Blk FG")]
    [InlineData("Something new", false, "Something new")]
    [InlineData(null, true, "Now")]
    [InlineData(" ", false, null)]
    public void A_drive_s_result_has_a_short_mark(string? result, bool current, string? expected)
        => Assert.Equal(expected, GamecastTelemetry.ShortResult(result, current));

    [Fact]
    public void The_yard_numbers_read_ten_to_fifty_and_back()
    {
        Assert.Equal(new[] { 10, 20, 30, 40, 50, 40, 30, 20, 10 }, GamecastTelemetry.YardNumbers.Select(n => n.Number));
        Assert.Equal(60, GamecastTelemetry.YardNumbers[4].X);
        Assert.Equal(10, GamecastTelemetry.FiveYardTicks.Count);
        Assert.Equal(15, GamecastTelemetry.FiveYardTicks[0]);
    }

    // ---- The comparison --------------------------------------------------------------------

    private static GamecastDrive Drive(int? team, int? yards, string? elapsed, bool score = false, int sequence = 1)
        => FakeGamecastData.Drive(sequence, 1, 2, current: false, yards: yards, elapsed: elapsed, score: score) with { TeamId = team };

    private static GamecastHeader Header => FakeGamecastData.Live();

    [Fact]
    public void Time_of_possession_sums_each_side_s_drives_and_skips_a_time_that_does_not_read()
    {
        var rows = GamecastTelemetry.Compare(Header,
        [
            Drive(Away, 75, "3:48"), Drive(Away, 10, "10:45"), Drive(Away, 5, "garbage"), Drive(Away, 5, "1:75"),
            Drive(Home, 20, "0:59"), Drive(Home, 0, null),
        ], [], null);

        var top = rows.Single(r => r.Label == "Time of possession");
        Assert.Equal("14:33", top.AwayText);
        Assert.Equal("0:59", top.HomeText);
        Assert.Equal(100, top.AwayShare + top.HomeShare, 6);
        Assert.True(top.AwayShare > top.HomeShare);
    }

    [Fact]
    public void Seconds_reads_m_ss_and_nothing_else()
    {
        Assert.Equal(228, GamecastTelemetry.Seconds("3:48"));
        Assert.Equal(0, GamecastTelemetry.Seconds("0:00"));
        Assert.Null(GamecastTelemetry.Seconds("3:5"));
        Assert.Null(GamecastTelemetry.Seconds("3:60"));
        Assert.Null(GamecastTelemetry.Seconds("3.48"));
        Assert.Null(GamecastTelemetry.Seconds(""));
        Assert.Null(GamecastTelemetry.Seconds(null));
    }

    [Fact]
    public void The_rows_come_in_order_and_each_side_is_its_own_team()
    {
        var rows = GamecastTelemetry.Compare(Header,
            [Drive(Away, 75, "3:48", score: true), Drive(Home, -4, "1:12"), Drive(Away, 30, "2:00")],
            [Play(50, 45, turnover: true), Play(40, 45, Home, turnover: true), Play(40, 45, Home, turnover: true), Play(30, 35, Home)],
            Box(away: [Line(false, ("passing.YDS", "142"))], home: [Line(true, ("passing.YDS", "208"))]));

        Assert.Equal(
            new[] { "Time of possession", "Total yards", "Passing yards", "Drives", "Scoring drives", "Turnovers" },
            rows.Select(r => r.Label));

        var yards = rows.Single(r => r.Label == "Total yards");
        Assert.Equal(("105", "-4"), (yards.AwayText, yards.HomeText));
        // A side that has lost ground counts as none toward the bar; its figure still says so.
        Assert.Equal((100d, 0d), (yards.AwayShare, yards.HomeShare));

        Assert.Equal(("2", "1"), Texts(rows, "Drives"));
        Assert.Equal(("1", "0"), Texts(rows, "Scoring drives"));

        var turnovers = rows.Single(r => r.Label == "Turnovers");
        Assert.Equal(("1", "2"), (turnovers.AwayText, turnovers.HomeText));
        Assert.False(turnovers.HigherIsBetter);
        Assert.True(yards.HigherIsBetter);
    }

    [Fact]
    public void Passing_and_rushing_sum_the_box_score_s_qualified_keys_per_side()
    {
        var box = Box(
            away: [Line(false, ("passing.YDS", "289"), ("rushing.YDS", "17")), Line(false, ("rushing.YDS", "94")), Line(false, ("receiving.YDS", "120"))],
            home: [Line(true, ("passing.YDS", "150")), Line(true, ("rushing.YDS", "-2")), Line(true, ("rushing.YDS", "60"))]);

        var rows = GamecastTelemetry.Compare(Header, [], [], box);

        Assert.Equal(("289", "150"), Texts(rows, "Passing yards"));
        Assert.Equal(("111", "58"), Texts(rows, "Rushing yards"));
        Assert.Equal(new[] { "Passing yards", "Rushing yards" }, rows.Select(r => r.Label));
    }

    [Fact]
    public void A_box_score_row_one_side_lacks_or_cannot_read_is_left_out()
    {
        var oneSided = Box(away: [Line(false, ("passing.YDS", "142"))], home: [Line(true, ("CAR", "4"))]);
        Assert.Empty(GamecastTelemetry.Compare(Header, [], [], oneSided));

        var unreadable = Box(away: [Line(false, ("rushing.YDS", "12"))], home: [Line(true, ("rushing.YDS", "--"))]);
        Assert.Empty(GamecastTelemetry.Compare(Header, [], [], unreadable));

        // A bare YDS is not a passing or a rushing figure: the box-score read never hands one over for the NFL.
        var bare = Box(away: [Line(false, ("YDS", "142"))], home: [Line(true, ("YDS", "99"))]);
        Assert.Empty(GamecastTelemetry.Compare(Header, [], [], bare));
    }

    [Fact]
    public void Nothing_the_data_cannot_say_is_returned()
    {
        Assert.Empty(GamecastTelemetry.Compare(Header, [], [], null));
        Assert.Empty(GamecastTelemetry.Compare(Header, null, null, null));

        // Drives nobody can be charged with, and plays with no side, say nothing.
        Assert.Empty(GamecastTelemetry.Compare(Header, [Drive(null, 40, "3:00"), Drive(99, 40, "3:00")], [Play(40, 45, team: null)], null));

        // Drives with no yards and no readable time still count as drives.
        var rows = GamecastTelemetry.Compare(Header, [Drive(Away, null, "x"), Drive(Home, null, null)], [], null);
        Assert.Equal(new[] { "Drives", "Scoring drives" }, rows.Select(r => r.Label));
    }

    [Fact]
    public void Penalties_are_never_compared()
    {
        var rows = GamecastTelemetry.Compare(Header, [], [Play(40, 45, penalty: true), Play(40, 35, Home, penalty: true)], null);

        Assert.DoesNotContain(rows, r => r.Label.Contains("Penalt", StringComparison.Ordinal));
    }

    [Fact]
    public void Shares_never_split_nothing_down_the_middle()
    {
        Assert.Equal((0d, 0d), GamecastTelemetry.Shares(0, 0));
        Assert.Equal((25d, 75d), GamecastTelemetry.Shares(1, 3));
        Assert.Equal((100d, 0d), GamecastTelemetry.Shares(5, 0));

        var rows = GamecastTelemetry.Compare(Header, [Drive(Away, 10, "1:00"), Drive(Home, 10, "1:00")], [Play(40, 45)], null);
        var turnovers = rows.Single(r => r.Label == "Turnovers");
        Assert.Equal(("0", "0"), (turnovers.AwayText, turnovers.HomeText));
        Assert.Equal((0d, 0d), (turnovers.AwayShare, turnovers.HomeShare));
    }

    private static (string, string) Texts(IReadOnlyList<ComparisonRow> rows, string label)
    {
        var row = rows.Single(r => r.Label == label);
        return (row.AwayText, row.HomeText);
    }

    private static BoxScoreLine Line(bool home, params (string Key, string Value)[] stats)
        => new(1, "Player", null, home, stats.ToDictionary(s => s.Key, s => s.Value));

    private static GameBoxScore Box(IReadOnlyList<BoxScoreLine> away, IReadOnlyList<BoxScoreLine> home)
        => new(new Game { Id = BoardRows.GameId }, home, away);
}
