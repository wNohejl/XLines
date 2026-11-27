using XLines.Core.Entities;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The window's read path (batch 2): it opens on the game's shared snapshot, then on each plays
/// notice reads what was written since the newest stamp it holds, in any drive, and works the
/// drives' play counts out from the plays it holds.
/// </summary>
public partial class GamecastPanelTests
{
    /// <summary>
    /// A play rewritten in a drive long closed — the old read re-read only the drive in progress,
    /// so this correction never reached an open window. Now it arrives with the next notice.
    /// </summary>
    [Fact]
    public void A_correction_to_a_play_in_an_older_drive_reaches_the_open_window()
    {
        TwoDrives();
        var cut = Render();

        _data.Plays[0] = FakeGamecastData.Play(1, 1, "J.Hurts run for 4 yards (spot corrected to 6)");
        Signal("plays", Id);
        cut.WaitForState(() => _data.ChangeReadsSince.Count == 1, TimeSpan.FromSeconds(5));

        cut.Find("[data-drive='1'] button").Click();

        cut.WaitForAssertion(() => Assert.Contains("spot corrected to 6", cut.Find("[data-seq='1']").TextContent), TimeSpan.FromSeconds(5));
        Assert.Equal(1, _data.SnapshotReads);
        Assert.Equal(new string?[] { "2", "1" }, Seqs(cut, ".gc__plays .gc__play"));
    }

    /// <summary>
    /// The watermark is the newest stamp read — the database's, not this host's clock — and it
    /// moves on with every read: the second notice asks after what the first one read.
    /// </summary>
    [Fact]
    public void Each_notice_asks_after_the_newest_stamp_the_window_has_read()
    {
        TwoDrives();
        var cut = Render();

        _data.Plays.Add(FakeGamecastData.Play(5, 2, "S.Barkley run for 11 yards"));
        Signal("plays", Id);
        cut.WaitForState(() => _data.ChangeReadsSince.Count == 1, TimeSpan.FromSeconds(5));

        _data.Plays.Add(FakeGamecastData.Play(6, 2, "J.Hurts pass incomplete"));
        Signal("plays", Id);
        cut.WaitForState(() => _data.ChangeReadsSince.Count == 2, TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.Contains("data-seq=\"6\"", cut.Markup), TimeSpan.FromSeconds(5));

        var (first, second) = (_data.ChangeReadsSince[0]!.Value, _data.ChangeReadsSince[1]!.Value);
        Assert.True(second > first, $"{second:O} should be after {first:O}");

        // Stamps, not the clock this window runs on.
        Assert.True(first < FakeGamecastData.Epoch.AddHours(1));
    }

    /// <summary>A play read twice — the overlap, or a row restamped with nothing changed — is drawn once.</summary>
    [Fact]
    public void A_play_read_again_by_a_later_change_read_is_drawn_once()
    {
        TwoDrives();
        var cut = Render();

        Signal("plays", Id);
        cut.WaitForState(() => _data.ChangeReadsSince.Count == 1, TimeSpan.FromSeconds(5));
        Signal("plays", Id);
        cut.WaitForState(() => _data.ChangeReadsSince.Count == 2, TimeSpan.FromSeconds(5));

        cut.WaitForAssertion(() => Assert.Equal(new string?[] { "4", "3" }, Seqs(cut, ".gc__plays .gc__play")));
        Assert.Equal(Ids(cut).Distinct().Count(), Ids(cut).Length);
    }

    /// <summary>
    /// A drive's figures and range come from the plays held: a play appended under the drive in
    /// progress moves it, though the change read brought the drive back without one.
    /// </summary>
    [Fact]
    public void A_play_appended_under_a_drive_is_counted_under_it_without_a_drive_read()
    {
        TwoDrives();
        var cut = Render();

        // The drive row did not change, so no drive comes back with these: the range grows from the
        // plays alone, and the timeout between two of its snaps, filed under no drive, falls inside it.
        _data.Plays.Add(FakeGamecastData.Play(6, 2, "S.Barkley run for 11 yards"));
        _data.Plays.Add(FakeGamecastData.Play(5, null, "Timeout PHI"));
        Signal("plays", Id);

        cut.WaitForAssertion(() => Assert.Equal(new string?[] { "6", "5", "4", "3" }, Seqs(cut, ".gc__plays .gc__play")), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("filed under no drive", cut.Markup);
    }

    /// <summary>A final asks for a snapshot that may be held for hours; a live game for one held seconds.</summary>
    [Fact]
    public void A_final_asks_for_a_settled_snapshot_and_a_live_game_does_not()
    {
        TwoDrives();
        Render();
        Assert.Equal([false], _data.SnapshotSettled);

        _data.Header = FakeGamecastData.Live(status: GameStatus.Final);
        Render();
        Assert.Equal([false, true], _data.SnapshotSettled);
    }

    /// <summary>
    /// The browser's clock is handed a reading once: a plays notice that leaves the header where it
    /// was redraws the window without starting the clock again.
    /// </summary>
    [Fact]
    public void A_plays_notice_does_not_start_the_clock_again()
    {
        var cut = RenderLiveClock();
        cut.WaitForAssertion(() => Assert.NotEmpty(_clock.Invocations["startClock"]));
        var started = _clock.Invocations["startClock"].Count;

        _data.Plays.Add(FakeGamecastData.Play(5, null, "Play 5"));
        Signal("plays", Id);
        cut.WaitForAssertion(() => Assert.Contains("Play 5", cut.Markup), TimeSpan.FromSeconds(5));

        Assert.Equal(started, _clock.Invocations["startClock"].Count);
    }

    /// <summary>The drive merge replaces a drive by its id, or by its number when it has none, and keeps order.</summary>
    [Fact]
    public void Drives_are_merged_by_their_id_and_kept_in_order()
    {
        var a = FakeGamecastData.Drive(1, 1, 2, current: false) with { Id = 11 };
        var b = FakeGamecastData.Drive(2, 3, 4, current: true) with { Id = 12 };
        var renumbered = b with { Sequence = 3, IsCurrent = false, Result = "Punt" };
        var unnumbered = FakeGamecastData.Drive(4, 5, 5, current: true);

        var merged = Components.Panels.GamecastPanel.MergeDrives([a, b], [renumbered, unnumbered]);

        Assert.Equal([1, 3, 4], merged.Select(d => d.Sequence));
        Assert.Equal("Punt", merged[1].Result);

        var again = Components.Panels.GamecastPanel.MergeDrives(merged, [unnumbered with { Result = "TD" }]);
        Assert.Equal(3, again.Count);
        Assert.Equal("TD", again[2].Result);
    }
}
