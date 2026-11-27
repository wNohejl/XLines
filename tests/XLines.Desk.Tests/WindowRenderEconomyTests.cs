using Bunit;
using XLines.Desk.Windowing;
using XLines.Desk.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Desk.Tests;

/// <summary>
/// Three things the desk used to pay for on every press and every change: a focus that changed
/// nothing, a row of columns built again on every read, and a bar that redrew the whole catalogue
/// for a change it does not draw. Each is skipped only when skipping it is invisible.
/// </summary>
public class WindowRenderEconomyTests : SampleDeskTestContext
{
    private static WindowManager Desk(int columns = 3)
    {
        var manager = new WindowManager(new SampleCatalog());
        manager.UpdateSettings(s =>
        {
            s.MaxConcurrentWindows = columns;
            s.PrimaryWindowKey = null;
        });
        return manager;
    }

    private static WindowInstance OpenOne(WindowManager desk, string key) => desk.Open(SampleCatalog.Find(key));

    private static int[] Versions(WindowManager desk) => desk.Windows.Select(w => w.Version).ToArray();

    // ------------------------------------------------------------------ focus ----

    [Fact]
    public void Ensuring_focus_on_the_window_already_in_raises_nothing_and_moves_no_version()
    {
        var desk = Desk();
        OpenOne(desk, SampleCatalog.List);
        var chart = OpenOne(desk, SampleCatalog.Chart);
        desk.Focus(chart.Id);

        var before = Versions(desk);
        var changed = 0;
        desk.Changed += () => changed++;

        desk.EnsureFocused(chart.Id);

        Assert.Equal(0, changed);
        Assert.Equal(before, Versions(desk));
        Assert.Equal(chart.Id, desk.FocusedId);
    }

    [Fact]
    public void Ensuring_focus_on_another_window_focuses_it_as_Focus_does()
    {
        var desk = Desk();
        var list = OpenOne(desk, SampleCatalog.List);
        OpenOne(desk, SampleCatalog.Chart);

        var changed = 0;
        desk.Changed += () => changed++;

        desk.EnsureFocused(list.Id);

        Assert.Equal(list.Id, desk.FocusedId);
        Assert.True(changed > 0);
    }

    [Fact]
    public void Focus_itself_still_notifies_when_the_window_is_already_in()
    {
        // Callers re-aim a window and then focus it to have it redrawn; Focus keeps that promise.
        var desk = Desk();
        var list = OpenOne(desk, SampleCatalog.List);
        desk.Focus(list.Id);

        var version = list.Version;
        var changed = 0;
        desk.Changed += () => changed++;

        desk.Focus(list.Id);

        Assert.True(changed > 0);
        Assert.True(list.Version > version);
    }

    [Fact]
    public void Ensuring_focus_on_a_collapsed_window_opens_it_out()
    {
        var desk = Desk();
        var list = OpenOne(desk, SampleCatalog.List);
        OpenOne(desk, SampleCatalog.Chart);
        desk.Focus(list.Id);
        desk.ToggleMinimise(list.Id);
        Assert.True(list.Minimised);

        desk.EnsureFocused(list.Id);

        Assert.False(list.Minimised);
        Assert.Equal(list.Id, desk.FocusedId);
    }

    [Fact]
    public void Ensuring_focus_on_a_tab_behind_brings_it_forward()
    {
        var desk = Desk(columns: 1);
        var list = OpenOne(desk, SampleCatalog.List);
        var chart = OpenOne(desk, SampleCatalog.Chart);

        Assert.Single(desk.Columns);
        var behind = desk.Columns[0].Showing == list ? chart : list;
        Assert.False(desk.IsShowing(behind));

        desk.EnsureFocused(behind.Id);

        Assert.True(desk.IsShowing(behind));
        Assert.Equal(behind.Id, desk.FocusedId);
    }

    [Fact]
    public void Ensuring_focus_while_another_column_holds_the_row_gives_the_row_back()
    {
        var desk = Desk();
        var list = OpenOne(desk, SampleCatalog.List);
        var chart = OpenOne(desk, SampleCatalog.Chart);
        desk.Focus(list.Id);
        desk.ToggleMaximise(chart.Id);
        Assert.True(chart.Maximised);

        desk.EnsureFocused(list.Id);

        Assert.False(chart.Maximised);
        Assert.Equal(list.Id, desk.FocusedId);
    }

    // ---------------------------------------------------------------- columns ----

    [Fact]
    public void Columns_read_twice_without_a_change_are_the_same_row()
    {
        var desk = Desk();
        var list = OpenOne(desk, SampleCatalog.List);
        OpenOne(desk, SampleCatalog.Chart);

        Assert.Same(desk.Columns, desk.Columns);
        Assert.Same(desk.ColumnOf(list.Id), desk.ColumnOf(list.Id));
    }

    [Fact]
    public void Columns_follow_every_change_to_the_row()
    {
        var desk = Desk(columns: 2);
        var list = OpenOne(desk, SampleCatalog.List);
        var chart = OpenOne(desk, SampleCatalog.Chart);
        Assert.Equal(2, desk.Columns.Count);

        // Opened at the limit: a tab in a column.
        var notes = OpenOne(desk, SampleCatalog.Notes);
        Assert.Equal(2, desk.Columns.Count);
        Assert.Equal(3, desk.Columns.Sum(c => c.Tabs.Count));

        // Moved out to a column of its own once there is room.
        desk.UpdateSettings(s => s.MaxConcurrentWindows = 3);
        Assert.True(desk.MoveToOwnColumn(notes.Id));
        Assert.Equal(3, desk.Columns.Count);
        Assert.Single(desk.ColumnOf(notes.Id)!.Tabs);

        // Reordered: the row reads in the new order.
        desk.Reorder([notes.Id, chart.Id, list.Id]);
        Assert.Equal([notes.Id, chart.Id, list.Id], desk.Columns.Select(c => c.Showing.Id));

        // Resized: no column moves, and the row still names every window.
        desk.AdjustSplit(notes.Id, chart.Id, 300, 500);
        Assert.Equal([notes.Id, chart.Id, list.Id], desk.Columns.Select(c => c.Showing.Id));

        // Moved into another column, then a tab brought forward.
        Assert.True(desk.MoveIntoColumn(list.Id, chart.Id));
        Assert.Equal(2, desk.Columns.Count);
        var group = desk.ColumnOf(chart.Id)!;
        Assert.Equal(2, group.Tabs.Count);
        var behind = group.Tabs[1];
        desk.Focus(behind.Id);
        Assert.Same(behind, desk.ColumnOf(chart.Id)!.Showing);

        // Closed: gone from the row.
        desk.Close(notes.Id);
        Assert.Null(desk.ColumnOf(notes.Id));
        Assert.DoesNotContain(desk.Columns.SelectMany(c => c.Tabs), w => w.Id == notes.Id);

        desk.CloseAll();
        Assert.Empty(desk.Columns);
    }

    [Fact]
    public void A_sequence_written_from_outside_reorders_the_row_it_reads()
    {
        var desk = Desk();
        var list = OpenOne(desk, SampleCatalog.List);
        var chart = OpenOne(desk, SampleCatalog.Chart);
        Assert.Equal(list.Id, desk.Columns[0].Showing.Id);

        // Sequence is public: the cache must see a write it was not told about.
        list.Sequence = chart.Sequence + 10;

        Assert.Equal(chart.Id, desk.Columns[0].Showing.Id);
    }

    // -------------------------------------------------------------------- bar ----

    private WindowManager BarDesk()
    {
        var manager = Desk();
        Services.AddSingleton(manager);
        Services.AddSingleton<XLines.Desk.Primitives.IDeskAlerts>(new FakeAlerts());
        return manager;
    }

    [Fact]
    public void The_bar_does_not_redraw_for_a_divider_dragged()
    {
        var desk = BarDesk();
        var list = OpenOne(desk, SampleCatalog.List);
        var chart = OpenOne(desk, SampleCatalog.Chart);
        var bar = RenderComponent<WindowBar>();
        var before = bar.RenderCount;

        bar.InvokeAsync(() => desk.AdjustSplit(list.Id, chart.Id, 300, 500));

        Assert.Equal(before, bar.RenderCount);
    }

    [Fact]
    public void The_bar_redraws_for_what_it_draws()
    {
        var desk = BarDesk();
        var list = OpenOne(desk, SampleCatalog.List);
        var chart = OpenOne(desk, SampleCatalog.Chart);
        var bar = RenderComponent<WindowBar>();

        var before = bar.RenderCount;
        bar.InvokeAsync(() => desk.SetPulse(list.Id, PulseState.Warn, "stale"));
        Assert.True(bar.RenderCount > before);
        Assert.Contains("stale", bar.FindAll(".bar__key").Single(k => k.GetAttribute("title")!.StartsWith(list.Title)).GetAttribute("title"));

        before = bar.RenderCount;
        bar.InvokeAsync(() => desk.Focus(list.Id));
        Assert.True(bar.RenderCount > before);

        before = bar.RenderCount;
        bar.InvokeAsync(() => desk.ToggleMinimise(chart.Id));
        Assert.True(bar.RenderCount > before);
        Assert.Contains(bar.FindAll(".bar__key"), k => k.ClassList.Contains("bar__key--collapsed"));

        before = bar.RenderCount;
        bar.InvokeAsync(() => desk.Close(chart.Id));
        Assert.True(bar.RenderCount > before);

        before = bar.RenderCount;
        bar.InvokeAsync(() => desk.Open(SampleCatalog.Find(SampleCatalog.Notes)));
        Assert.True(bar.RenderCount > before);
    }

    [Fact]
    public void The_bar_redraws_when_its_own_drawer_is_pinned()
    {
        BarDesk();
        var bar = RenderComponent<WindowBar>();
        var before = bar.RenderCount;

        bar.Find(".bar__group").Click();

        Assert.True(bar.RenderCount > before);
        Assert.Single(bar.FindAll(".bar__menu--pinned"));
    }
}
