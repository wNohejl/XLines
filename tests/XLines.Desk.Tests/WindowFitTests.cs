using XLines.Desk.Windowing;
using XLines.Desk.Tests.Support;

namespace XLines.Desk.Tests;

/// <summary>
/// The row against a browser that changes width. Narrowed past what the columns' minimums need,
/// the row used to keep every column and give each a share under its own minimum — four columns
/// at 900px were 214px apiece, panels scrolling sideways and title bars cut off before Close. It
/// folds a column into a tab instead, and widened again puts it back: nothing closes, and the
/// operator's column limit is never rewritten.
/// </summary>
public class WindowFitTests
{
    // Health 420, Alerts 400, Jobs 440: 1260 of minimums and four 8px gaps need 1292px.
    private static WindowManager Triage(double width = 1920, int limit = 4)
    {
        var manager = new WindowManager(new SampleCatalog());
        manager.SetViewport(width, 900);
        manager.UpdateSettings(s => s.MaxConcurrentWindows = limit);
        manager.Open(SampleCatalog.Find(SampleCatalog.Health));
        manager.Open(SampleCatalog.Find(SampleCatalog.Alerts));
        manager.Open(SampleCatalog.Find(SampleCatalog.Jobs));
        return manager;
    }

    private static WindowInstance Win(WindowManager desk, string key) => desk.Windows.Single(w => w.Definition.Key == key);

    private static string[] Row(WindowManager desk) => desk.Columns.Select(c => c.Showing.Definition.Key).ToArray();

    [Fact]
    public void A_narrowed_browser_folds_a_column_into_a_tab_rather_than_crushing_the_row()
    {
        var desk = Triage();

        desk.SetViewport(1000, 900);

        Assert.Equal(2, desk.ColumnCount);
        Assert.Equal(3, desk.OpenCount);
        Assert.All(desk.Columns, c => Assert.True(c.Showing.Width >= c.MinWidth - 0.5,
            $"{c.Showing.Definition.Key} is {c.Showing.Width}, under its {c.MinWidth} minimum"));
    }

    [Fact]
    public void The_operators_column_limit_is_left_as_they_set_it()
    {
        var desk = Triage();

        desk.SetViewport(1000, 900);

        Assert.Equal(4, desk.Settings.MaxConcurrentWindows);
    }

    [Fact]
    public void The_window_the_operator_is_in_stays_in_view()
    {
        var desk = Triage();
        desk.Focus(Win(desk, SampleCatalog.Alerts).Id);

        desk.SetViewport(1000, 900);

        Assert.Equal(Win(desk, SampleCatalog.Alerts).Id, desk.FocusedId);
        Assert.True(desk.IsShowing(Win(desk, SampleCatalog.Alerts)));
    }

    [Fact]
    public void Widened_again_the_folded_column_comes_back_to_its_place()
    {
        var desk = Triage();
        var before = Row(desk);

        desk.SetViewport(1000, 900);
        desk.SetViewport(1920, 900);

        Assert.Equal(3, desk.ColumnCount);
        Assert.Equal(before, Row(desk));
    }

    [Fact]
    public void It_comes_back_only_once_there_is_room_for_it()
    {
        var desk = Triage();

        desk.SetViewport(1000, 900);
        desk.SetViewport(1200, 900);

        // 1200 is still short of the 1292 three columns need.
        Assert.Equal(2, desk.ColumnCount);
    }

    [Fact]
    public void A_tab_the_operator_made_stays_a_tab()
    {
        var desk = Triage();
        desk.MoveIntoColumn(Win(desk, SampleCatalog.Alerts).Id, Win(desk, SampleCatalog.Health).Id);

        desk.SetViewport(1000, 900);
        desk.SetViewport(1920, 900);

        Assert.Equal(2, desk.ColumnCount);
        Assert.Equal(desk.ColumnOf(Win(desk, SampleCatalog.Health).Id)!.Id, Win(desk, SampleCatalog.Alerts).ColumnId);
    }

    [Fact]
    public void A_window_moved_out_of_its_fold_by_hand_is_not_moved_again()
    {
        var desk = Triage();
        desk.SetViewport(1000, 900);

        var folded = desk.Columns.Single(c => c.IsGroup).Tabs[1];
        desk.MoveIntoColumn(folded.Id, desk.Columns.First(c => c.Tabs.All(t => t.Id != folded.Id)).Showing.Id);
        var shape = Row(desk);

        desk.SetViewport(1920, 900);

        Assert.Equal(shape, Row(desk));
    }

    [Fact]
    public void Coming_back_never_raises_the_row_past_the_limit()
    {
        var desk = Triage();
        desk.SetViewport(1000, 900);

        desk.UpdateSettings(s => s.MaxConcurrentWindows = 2);
        desk.SetViewport(1920, 900);

        Assert.Equal(2, desk.ColumnCount);
    }

    [Fact]
    public void A_stacked_phone_desk_has_no_row_to_fit_and_folds_nothing()
    {
        var desk = Triage();

        desk.SetViewport(390, 800);

        Assert.Equal(3, desk.ColumnCount);
    }

    [Fact]
    public void A_declared_resolution_is_not_the_browsers_and_is_not_folded_for_it()
    {
        var desk = Triage();
        desk.UpdateSettings(s =>
        {
            s.ResolutionMode = ResolutionMode.Fixed;
            s.CustomWidth = 1920;
        });

        desk.SetViewport(1000, 900);

        Assert.Equal(3, desk.ColumnCount);
    }

    [Fact]
    public void A_restored_layout_too_wide_for_the_browser_folds_on_the_first_measurement()
    {
        var wide = Triage();
        var layout = wide.Capture();

        var desk = new WindowManager(new SampleCatalog());
        desk.Restore(layout);
        desk.SetViewport(1000, 900);

        Assert.Equal(3, desk.OpenCount);
        Assert.Equal(2, desk.ColumnCount);
        Assert.True(desk.ViewportMeasured);
    }
}
