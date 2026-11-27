using XLines.Desk.Windowing;

namespace XLines.Desk.Tests;

/// <summary>
/// The row's arithmetic on its own, with no manager: how many columns fit, how wide a row of
/// minimums is, and that placing and renumbering move a window's version only when something it
/// draws moved.
/// </summary>
public class RowLayoutTests
{
    private static WindowInstance Window(string id, int minWidth = 300, bool minimised = false) => new()
    {
        Id = id,
        Definition = new WindowDefinition
        {
            Key = id,
            Title = id,
            Icon = "",
            ComponentType = typeof(object),
            MinWidth = minWidth
        },
        Minimised = minimised
    };

    [Theory]
    // Without a primary the row divides evenly: 1920 holds four 420px columns, not five.
    [InlineData(1920, false, 0.45, 4)]
    // With a 45% primary the rest share 55%: 1920 leaves room for three beside it, not four.
    [InlineData(1920, true, 0.45, 3)]
    // Too narrow for two readable columns still allows one.
    [InlineData(600, true, 0.45, 1)]
    [InlineData(0, false, 0.45, 1)]
    public void Recommended_capacity_counts_columns_at_a_readable_width(double width, bool primary, double share, int expected)
    {
        Assert.Equal(expected, RowLayout.RecommendedCapacity(width, primary, share));
    }

    [Fact]
    public void Recommended_capacity_clamps_the_primary_share()
    {
        // A share past 80% is read as 80%, so it recommends what 0.8 does.
        Assert.Equal(
            RowLayout.RecommendedCapacity(2560, true, 0.8),
            RowLayout.RecommendedCapacity(2560, true, 0.99));
    }

    [Fact]
    public void Required_sums_each_columns_widest_minimum_a_stub_for_a_collapsed_one_and_the_gaps()
    {
        IReadOnlyList<WindowInstance>[] columns =
        [
            [Window("a", 300), Window("b", 500)],
            [Window("c", 400, minimised: true)],
            [Window("d", 260)]
        ];

        var expected = 500 + DeskSettings.CollapsedWidth + 260 + RowLayout.Gap * 4;

        Assert.Equal(expected, RowLayout.Required(columns));
    }

    [Fact]
    public void Place_moves_the_version_only_when_the_place_moved()
    {
        var window = Window("a");

        RowLayout.Place(window, 8, 400);
        var placed = window.Version;

        RowLayout.Place(window, 8, 400);
        Assert.Equal(placed, window.Version);

        RowLayout.Place(window, 16, 400);
        Assert.Equal(placed + 1, window.Version);
        Assert.Equal(16, window.X);
        Assert.Equal(400, window.Width);
    }

    [Fact]
    public void Renumber_gives_each_column_its_place_and_touches_only_windows_that_moved()
    {
        var a = Window("a");
        var b = Window("b");
        var c = Window("c");
        a.Sequence = 0;
        b.Sequence = 5;
        c.Sequence = 5;

        var before = (a.Version, b.Version, c.Version);

        RowLayout.Renumber([[a], [b, c]]);

        Assert.Equal((0L, 1L, 1L), (a.Sequence, b.Sequence, c.Sequence));
        Assert.Equal(before.Item1, a.Version);
        Assert.Equal(before.Item2 + 1, b.Version);
        Assert.Equal(before.Item3 + 1, c.Version);
    }
}
