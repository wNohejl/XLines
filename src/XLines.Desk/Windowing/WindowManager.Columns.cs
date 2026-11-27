namespace XLines.Desk.Windowing;

public partial class WindowManager
{
    // ------------------------------------------------------------------ columns ----

    /// <summary>The row, left to right, each column with its tabs most recently shown first.</summary>
    public IReadOnlyList<WindowColumn> Columns => _row.BuildColumns();

    /// <summary>The column a window is in, or null for a window not on the desk.</summary>
    public WindowColumn? ColumnOf(string id) => _row.ColumnOf(id);

    /// <summary>Whether a window is the one its column shows, rather than a tab behind it.</summary>
    public bool IsShowing(WindowInstance window) => ColumnOf(window.Id)?.Showing == window;

    /// <summary>The narrowest the column holding <paramref name="window"/> may be. See <see cref="WindowColumn.MinWidth"/>.</summary>
    public int MinWidthOf(WindowInstance window) => ColumnOf(window.Id)?.MinWidth ?? window.Definition.MinWidth;

    /// <summary>
    /// The column the next window would join as a tab, or null while a column is free. Said
    /// before the click, as the header's count does, so where a window lands is never a surprise.
    /// </summary>
    public WindowColumn? NextTabTarget => AtCapacity ? _row.TabTarget(_row.BuildColumns()) : null;

    /// <summary>Whether a free column is waiting for a tab to move into it.</summary>
    public bool CanMoveToOwnColumn(string id)
        => ColumnOf(id) is { IsGroup: true } && !AtCapacity;

    /// <summary>
    /// Takes a window out of its tab group into a column of its own, just right of the one it
    /// left, at its own default width. Only while a column is free: the limit is the operator's,
    /// and this does not raise it. True when it moved.
    /// </summary>
    public bool MoveToOwnColumn(string id)
    {
        if (!CanMoveToOwnColumn(id) || Find(id) is not { } window)
            return false;

        // The row changes shape, so it is a step Back takes back: the tab returns to its group,
        // at the group's width.
        Remember();
        _fit.Forget(id);

        _row.MoveToOwnColumn(window);
        Relayout();
        return true;
    }

    /// <summary>
    /// Whether <paramref name="id"/> can be dropped into the column holding <paramref name="targetId"/>
    /// as a tab: both on the desk, in different columns.
    /// </summary>
    public bool CanMoveIntoColumn(string id, string targetId)
        => Find(id) is { } window && Find(targetId) is { } target && window.ColumnId != target.ColumnId;

    /// <summary>
    /// A tab dragged onto another window: <paramref name="id"/> leaves its column and joins the
    /// one holding <paramref name="targetId"/>, in front, at that column's place and width. A
    /// column left empty gives its width back to the row; one left with a single window is a
    /// plain window again. A step Back takes back. True when it moved.
    /// </summary>
    public bool MoveIntoColumn(string id, string targetId)
    {
        if (!CanMoveIntoColumn(id, targetId))
            return false;

        Remember();
        _fit.Forget(id);

        _row.MoveIntoColumn(Find(id)!, Find(targetId)!);
        Relayout();
        return true;
    }

    /// <summary>
    /// Asks the tab key of the column that will show <paramref name="id"/> to take keyboard focus once
    /// drawn. Choosing a tab from the keyboard brings another window forward, and the key that
    /// held focus goes behind it with it; without this the keyboard would be left on the page.
    /// </summary>
    public void RequestTabFocus(string id) => _tabFocus = id;

    /// <summary>True, once, for the tab key drawn for the window focus was asked for.</summary>
    public bool TakeTabFocus(string id)
    {
        if (_tabFocus != id)
            return false;

        _tabFocus = null;
        return true;
    }

    private string? _tabFocus;
}
