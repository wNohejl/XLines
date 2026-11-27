using Bunit;
using XLines.Core.Entities;
using XLines.Data.CrossReference;
using XLines.Data.Gamecast;
using XLines.Web.Components.Panels;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The Gamecast's small display fixes (batch 3a): each side's timeouts and the red zone on a live
/// field, a polite announcement of a play that has just landed, the scoring tab headed and
/// pressable onto its drive, a final's empty states said for a final, and the field's words kept
/// legible in a narrow card.
/// </summary>
public partial class GamecastPanelTests
{
    // ---- Timeouts and the red zone (D2) -----------------------------------------------------

    private static AngleSharp.Dom.IElement Timeouts(IRenderedFragment cut, string side)
        => cut.Find($".gc__fieldcard .gc__timeouts[data-side='{side}']");

    [Fact]
    public void A_live_field_card_shows_each_sides_timeouts_as_three_pips_beside_its_abbreviation()
    {
        var cut = Render();

        // PHI (away) has two left, WSH (home) all three: away on the left, as the field has it.
        var away = Timeouts(cut, "away");
        Assert.Equal("img", away.GetAttribute("role"));
        Assert.Equal("PHI, 2 timeouts left", away.GetAttribute("aria-label"));
        Assert.Contains("PHI", away.TextContent);
        Assert.Equal(3, away.QuerySelectorAll(".gc__pip").Length);
        Assert.Equal(2, away.QuerySelectorAll(".gc__pip--left").Length);

        var home = Timeouts(cut, "home");
        Assert.Equal("WSH, 3 timeouts left", home.GetAttribute("aria-label"));
        Assert.Equal(3, home.QuerySelectorAll(".gc__pip--left").Length);

        // The pips are the picture; the name is the words.
        Assert.All(cut.FindAll(".gc__pips"), p => Assert.Equal("true", p.GetAttribute("aria-hidden")));
    }

    [Theory]
    [InlineData(1, "WSH, 1 timeout left")]
    [InlineData(0, "WSH, no timeouts left")]
    public void One_timeout_and_none_are_said_in_words(int left, string said)
    {
        _data.Header = FakeGamecastData.Live() with { HomeTimeouts = left };
        var cut = Render();

        Assert.Equal(said, Timeouts(cut, "home").GetAttribute("aria-label"));
        Assert.Equal(left, Timeouts(cut, "home").QuerySelectorAll(".gc__pip--left").Length);
    }

    [Fact]
    public void A_side_with_no_timeout_reading_draws_no_pips()
    {
        _data.Header = FakeGamecastData.Live() with { HomeTimeouts = null };
        var cut = Render();

        Assert.Empty(cut.FindAll(".gc__timeouts[data-side='home']"));
        Assert.Single(cut.FindAll(".gc__timeouts[data-side='away']"));

        _data.Header = FakeGamecastData.Live() with { HomeTimeouts = null, AwayTimeouts = null };
        Signal("games", Id);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".gc__timeouts")), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll(".gc__fieldstatus"));
    }

    [Fact]
    public void The_red_zone_is_a_warning_tag_on_the_field_card_while_the_ball_is_in_it()
    {
        _data.Header = FakeGamecastData.Live() with { IsRedZone = true };
        var cut = Render();

        var tag = cut.Find(".gc__fieldcard .gc__fieldstatus .tag");
        Assert.Equal("Red zone", tag.TextContent.Trim());
        Assert.Contains("tag--warn", tag.ClassList);
    }

    [Fact]
    public void Outside_the_red_zone_there_is_no_red_zone_tag()
    {
        var cut = Render();

        Assert.DoesNotContain(cut.FindAll(".gc__fieldcard .tag"), t => t.TextContent.Contains("Red zone"));
    }

    [Fact]
    public void A_final_shows_no_timeouts_and_no_red_zone()
    {
        ThreeFinishedDrives();
        _data.Header = FakeGamecastData.Live(status: GameStatus.Final) with { IsRedZone = true };
        var cut = Render();

        Assert.Single(cut.FindAll(".gc__fieldcard"));
        Assert.Empty(cut.FindAll(".gc__timeouts"));
        Assert.Empty(cut.FindAll(".gc__fieldstatus"));
    }

    [Fact]
    public void An_earlier_drive_on_a_live_field_shows_no_timeouts_or_red_zone_of_the_play_in_progress()
    {
        TwoDrives();
        _data.Header = FakeGamecastData.Live() with { IsRedZone = true };
        var cut = Render();
        Assert.Single(cut.FindAll(".gc__fieldstatus"));

        cut.Find(".drivechart__row[data-sequence='1']").Click();

        Assert.Empty(cut.FindAll(".gc__fieldstatus"));
    }

    // ---- A new play, announced (D3) ---------------------------------------------------------

    private static AngleSharp.Dom.IElement Announcer(IRenderedFragment cut)
        => cut.Find(".gc__announce[role='status'][aria-live='polite']");

    private static GamecastPlay Run(long sequence, int drive, int yards) => FakeGamecastData.Play(sequence, drive, $"S.Barkley run for {yards} yards") with
    {
        Type = "Rush",
        Yardage = yards,
        End = new GamecastSituation(1, 10, 20, 20, "1st & 10 at WSH 20", "WSH 20", FakeGamecastData.AwayId)
    };

    [Fact]
    public void The_window_holds_a_hidden_polite_status_that_says_nothing_on_the_first_load()
    {
        TwoDrives();
        var cut = Render();

        var live = Announcer(cut);
        Assert.Contains("sr-only", live.ClassList);
        Assert.Equal("true", live.GetAttribute("aria-atomic"));
        Assert.Equal(string.Empty, live.TextContent.Trim());
        Assert.Empty(cut.FindAll(".gc__play--new"));
    }

    [Fact]
    public void A_play_appended_is_announced_by_its_headline_and_the_situation_it_left()
    {
        TwoDrives();
        var cut = Render();

        var run = Run(5, 2, 11);
        _data.Plays.Add(run);
        _data.Drives[1] = FakeGamecastData.Drive(2, 3, 5, current: true, plays: 3, yards: 14);
        Signal("plays", Id);

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".gc__plays .gc__play").Count), TimeSpan.FromSeconds(5));
        Assert.Equal($"{PlayHeadline.For(run)}. 1st & 10 at WSH 20", Announcer(cut).TextContent.Trim());

        // The newest row is marked, so a reduced-motion reader's eye is drawn to it.
        var fresh = cut.FindAll(".gc__play--new");
        Assert.Single(fresh);
        Assert.Equal("5", fresh[0].GetAttribute("data-seq"));
    }

    [Fact]
    public void A_notice_that_adds_no_play_says_nothing_new()
    {
        TwoDrives();
        var cut = Render();

        _data.Plays[3] = _data.Plays[3] with { Text = "J.Hurts pass incomplete short left" };
        Signal("plays", Id);

        cut.WaitForAssertion(() => Assert.Contains("short left", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Equal(string.Empty, Announcer(cut).TextContent.Trim());
        Assert.Empty(cut.FindAll(".gc__play--new"));
    }

    [Fact]
    public void A_play_with_no_headline_is_announced_by_its_sentence_and_the_games_situation()
    {
        TwoDrives();
        var cut = Render();

        _data.Plays.Add(FakeGamecastData.Play(5, 2, "Something the feed did not type") with { Type = null });
        Signal("plays", Id);

        cut.WaitForAssertion(() => Assert.NotEqual(string.Empty, Announcer(cut).TextContent.Trim()), TimeSpan.FromSeconds(5));
        Assert.Equal("Something the feed did not type. PHI ball, 2nd & 6 at WSH 34", Announcer(cut).TextContent.Trim());
    }

    [Fact]
    public void Under_reduced_motion_the_newest_row_is_washed_in_a_token_colour()
    {
        var css = File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Web", "Components", "Snippets", "PlayLog.razor.css"));

        var reduced = css[css.IndexOf("prefers-reduced-motion: reduce", StringComparison.Ordinal)..];
        Assert.Contains(".gc__play--new", reduced);
        Assert.Contains("var(--surface-3)", css);
        Assert.DoesNotContain("var(--accent", css);
        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b|rgba?\(", css);
    }

    [Fact]
    public void The_play_log_marks_only_the_row_it_is_told_is_new()
    {
        var plays = new List<GamecastPlay> { FakeGamecastData.Play(1, 1, "a"), FakeGamecastData.Play(2, 1, "b") };
        var cut = RenderComponent<PlayLog>(p => p.Add(l => l.Plays, plays).Add(l => l.Sport, "nfl").Add(l => l.Fresh, plays[1].Id));

        Assert.Equal(new[] { "2" }, cut.FindAll(".gc__play--new").Select(r => r.GetAttribute("data-seq")).ToArray());
    }

    // ---- The scoring tab (D7) ---------------------------------------------------------------

    /// <summary>TwoDrives, with its touchdown typed as one so it has a headline.</summary>
    private void TwoDrivesWithATouchdown()
    {
        TwoDrives();
        _data.Plays[1] = _data.Plays[1] with { Type = "Passing Touchdown", Yardage = 31 };
    }

    [Fact]
    public void A_scoring_row_leads_with_its_headline_then_the_sentence_and_the_drives_line()
    {
        TwoDrivesWithATouchdown();
        var cut = Render();
        Show(cut, "Scoring");

        var row = cut.Find(".gc__score");
        Assert.Equal("BUTTON", row.TagName);
        Assert.Equal("button", row.GetAttribute("type"));

        var body = row.QuerySelector(".gc__scorebody")!;
        Assert.Equal(new[] { "gc__headline", "gc__text", "gc__scoredrive" }, body.Children.Select(c => c.ClassName).ToArray());
        Assert.Equal(PlayHeadline.For(_data.Plays[1]), body.Children[0].TextContent.Trim());
        Assert.Equal("J.Hurts pass to D.Smith for a touchdown", body.Children[1].TextContent.Trim());
        Assert.Equal("7 plays, 62 yds, 3:48", body.Children[2].TextContent.Trim());
    }

    [Fact]
    public void Pressing_a_scoring_row_puts_its_drive_on_the_field_and_marks_it_chosen()
    {
        TwoDrivesWithATouchdown();
        var cut = Render();
        Show(cut, "Scoring");
        Assert.Equal("false", cut.Find(".gc__score").GetAttribute("aria-pressed"));

        cut.Find(".gc__score").Click();

        var graphic = Field(cut);
        Assert.True(graphic.Replay);
        Assert.Equal(new long[] { 1, 2 }, FieldSeqs(graphic));
        Assert.Equal("true", cut.Find(".drivechart__row[data-sequence='1']").GetAttribute("aria-pressed"));
        Assert.Equal("true", cut.Find(".gc__score").GetAttribute("aria-pressed"));
        Assert.Contains("Earlier drive", cut.Find(".gc__fielddrive").TextContent);
    }

    [Fact]
    public void A_scoring_play_no_drive_holds_is_a_row_with_nothing_to_press()
    {
        _data.Plays.Add(FakeGamecastData.Play(1, null, "Safety, ball out of the end zone", scoring: true));
        var cut = Render();
        Show(cut, "Scoring");

        var row = cut.Find(".gc__score");
        Assert.Equal("DIV", row.TagName);
        Assert.Empty(row.QuerySelectorAll(".gc__scoredrive"));
        Assert.Empty(cut.FindAll("button.gc__score"));
    }

    [Fact]
    public void A_drives_figures_are_its_line_without_its_result()
    {
        var drive = FakeGamecastData.Drive(1, 1, 2, current: false, result: "TD", plays: 7, yards: 75, elapsed: "4:02", score: true);

        Assert.Equal("7 plays, 75 yds, 4:02", GamecastPanel.DriveFigures(drive));
        Assert.Equal("7 plays, 75 yds, 4:02 · TD", GamecastPanel.DriveLine(drive));
        Assert.Null(GamecastPanel.DriveFigures(FakeGamecastData.Drive(1, 1, 1, current: false)));
    }

    // ---- A final's empty states (D9) --------------------------------------------------------

    [Fact]
    public void A_final_with_no_box_score_says_none_is_held_not_that_it_is_coming()
    {
        ThreeFinishedDrives();
        var cut = Render();
        Show(cut, "Box score");

        Assert.Contains("No box score is held for this game.", cut.Markup);
        Assert.DoesNotContain("appears once the first stats are in", cut.Markup);
    }

    [Fact]
    public void A_live_game_with_no_box_score_still_says_it_appears_with_the_first_stats()
    {
        var cut = Render();
        Show(cut, "Box score");

        Assert.Contains("The box score appears once the first stats are in.", cut.Markup);
    }

    [Fact]
    public void A_final_with_no_plays_says_how_often_espn_was_asked_and_when_it_is_asked_next()
    {
        Bench.Clock = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
        _data.Header = FakeGamecastData.Live(status: GameStatus.Final);
        _data.Miss = new GamecastMiss(3, Bench.Clock.Value.AddDays(-1), new DateTimeOffset(2026, 12, 2, 6, 0, 0, TimeSpan.Zero), "No plays in the summary.");
        var cut = Render();

        Assert.Contains("No play-by-play is held for this game. Asked ESPN 3 times; next try Dec 2.", cut.Find(".empty").TextContent);
        Assert.Equal(1, _data.MissReads);
    }

    [Fact]
    public void The_miss_is_worded_for_one_ask_a_failure_and_a_retry_that_is_due()
    {
        var now = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);

        Assert.Equal("No play-by-play is held for this game. Asked ESPN once; next try Dec 2.",
            GamecastPanel.NoPlaysHeld(new GamecastMiss(1, now, new DateTimeOffset(2026, 12, 2, 6, 0, 0, TimeSpan.Zero), "No plays in the summary."), now));
        Assert.Equal("No play-by-play is held for this game. Asked ESPN 2 times, the last failed; next try Nov 28.",
            GamecastPanel.NoPlaysHeld(new GamecastMiss(2, now, now.AddDays(1), "HttpRequestException: 503"), now));
        Assert.Equal("No play-by-play is held for this game. Asked ESPN 4 times; it is due another try.",
            GamecastPanel.NoPlaysHeld(new GamecastMiss(4, now.AddDays(-9), now.AddDays(-1), "No plays in the summary."), now));
        Assert.Equal("No play-by-play is held for this game. Asked ESPN 4 times; next try Jan 3, 2027.",
            GamecastPanel.NoPlaysHeld(new GamecastMiss(4, now, new DateTimeOffset(2027, 1, 3, 6, 0, 0, TimeSpan.Zero), "No plays in the summary."), now));
        Assert.Equal("No play-by-play is held for this game.", GamecastPanel.NoPlaysHeld(null, now));
    }

    [Fact]
    public void Only_a_final_with_no_plays_asks_whether_the_backport_missed_it()
    {
        TwoDrives();
        _data.Header = FakeGamecastData.Live(status: GameStatus.Final);
        _data.Miss = new GamecastMiss(3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), "No plays in the summary.");
        Render();
        Assert.Equal(0, _data.MissReads);

        _data.Header = FakeGamecastData.Live();
        _data.Plays.Clear();
        Render();
        Assert.Equal(0, _data.MissReads);
    }

    // ---- A narrow field (D5) ----------------------------------------------------------------

    [Fact]
    public void The_bottom_row_of_yard_numbers_is_marked_so_a_narrow_card_can_drop_it()
    {
        var cut = RenderComponent<FieldGraphic>(p => p.Add(s => s.Label, "Field"));

        var bottoms = cut.FindAll(".fieldgraphic__num--bottom");
        Assert.Equal(GamecastTelemetry.YardNumbers.Count, bottoms.Count);
        Assert.Equal(GamecastTelemetry.YardNumbers.Count * 2, cut.FindAll(".fieldgraphic__num").Count);
    }

    [Fact]
    public void A_narrow_card_drops_the_result_word_and_the_bottom_numbers_and_sets_the_rest_larger()
    {
        var css = File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Web", "Components", "Snippets", "FieldGraphic.razor.css"));

        var at = css.IndexOf("@container (max-width: 480px)", StringComparison.Ordinal);
        Assert.True(at >= 0, "FieldGraphic.razor.css has no narrow-card container query");
        var narrow = css[at..css.IndexOf("@media", at, StringComparison.Ordinal)];

        Assert.Matches(@"\.fieldgraphic__results\s*\{\s*display:\s*none", narrow);
        Assert.Matches(@"\.fieldgraphic__num--bottom\s*\{\s*display:\s*none", narrow);

        // The numbers and the end zones' names grow in the viewBox's units, so they read at a
        // phone-width card's ~2.6px a unit: at least 11px.
        var num = System.Text.RegularExpressions.Regex.Match(narrow, @"\.fieldgraphic__num\s*\{[^}]*font-size:\s*([\d.]+)px");
        Assert.True(num.Success);
        Assert.True(double.Parse(num.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 2.6 >= 11);
        Assert.Matches(@"\.fieldgraphic__mark\s*\{[^}]*font-size:", narrow);
    }

    [Fact]
    public void The_field_card_is_a_container_the_narrow_rules_measure()
    {
        // The card's group (.wgroup) is the Desk's container; the field sits straight inside it.
        var cut = Render();
        var body = cut.Find(".gc__fieldcard .wgroup > .gc__cardbody");
        Assert.NotNull(body.QuerySelector(".fieldgraphic"));
    }
}
