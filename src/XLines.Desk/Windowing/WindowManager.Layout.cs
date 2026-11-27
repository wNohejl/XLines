namespace XLines.Desk.Windowing;

public partial class WindowManager
{
    /// <summary>
    /// Makes a window the one the operator is in. A tab behind another comes to the front of its
    /// column — the one that was showing becomes the second tab — and a collapsed column opens out.
    /// </summary>
    public void Focus(string id)
    {
        var window = Find(id);
        if (window is null)
            return;

        // Expanding a collapsed column, or giving back a row another column held, changes how
        // width is shared and is laid out again; a plain focus keeps the row's order and sizes.
        if (_row.BringForward(window))
            Relayout();
        else
            Notify();
    }

    /// <summary>
    /// <see cref="Focus"/>, unless the window is already in and focusing it would change nothing:
    /// what a press inside a window calls, since a window takes focus on every pointerdown and a
    /// full focus redraws the desk's chrome and saves the layout each time.
    /// </summary>
    /// <remarks>
    /// Not folded into <see cref="Focus"/> itself: callers re-aim a window (its parameters, its
    /// title) and then focus it to have it redrawn with what it was aimed at — <see cref="Open"/>
    /// on a re-aimed singleton, the Line watch — and an early return there would leave it showing
    /// what it showed before.
    /// </remarks>
    public void EnsureFocused(string id)
    {
        if (!_row.IsAlreadyIn(id))
            Focus(id);
    }

    /// <summary>Collapses a window's column to a title-only stub, or restores one that already is.</summary>
    public void ToggleMinimise(string id)
    {
        var window = Find(id);
        if (window is null)
            return;

        var column = ColumnOf(id)!;

        if (column.Showing.Minimised)
        {
            Focus(id);
            return;
        }

        _row.Collapse(column);
        Relayout();
    }

    public void ToggleMaximise(string id)
    {
        var window = Find(id);
        if (window is null)
            return;

        // Maximised is the column's, held by every tab in it alike: the tab chosen after
        // maximising another reads the same state on its bar, and one press restores the row.
        var maximise = MaximisedColumn?.Id != window.ColumnId;

        // Only one column can hold the row at a time.
        _row.GiveRowBack();

        // A collapsed window has nothing to maximise into, and a tab behind another is not in
        // view to maximise: both are brought forward first, as pressing them would.
        Focus(id);

        foreach (var tab in ColumnOf(id)!.Tabs)
        {
            tab.Maximised = maximise;
            tab.Touch();
        }

        Relayout();
    }

    /// <summary>The column holding the row on its own, if one is maximised.</summary>
    public WindowColumn? MaximisedColumn => _row.MaximisedColumn;

    /// <summary>
    /// Reassigns the row's order from a dragged column's drop position. Any window of a column
    /// names the column; every column not mentioned keeps its relative order after those that
    /// are. Called with the complete row, so that case does not arise in practice.
    /// </summary>
    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        _row.Reorder(orderedIds);
        Relayout();
    }

    /// <summary>
    /// Applies a divider drag: the two adjacent columns swap width between them and nothing
    /// else in the row moves. Their combined weight is preserved, so the rest of the row is
    /// untouched — which is what makes a divider feel local rather than global. Every tab in a
    /// column shares its width, so a tab switch never resizes the row.
    /// </summary>
    public void AdjustSplit(string leftId, string rightId, double leftWidthPx, double rightWidthPx)
    {
        var left = ColumnOf(leftId);
        var right = ColumnOf(rightId);

        if (left is null || right is null || !RowLayout.Split(left, right, leftWidthPx, rightWidthPx))
            return;

        Relayout();
    }

    /// <summary>Returns every column to the width the primary-window setting implies.</summary>
    public void ResetWidths()
    {
        RowLayout.ResetWidths(_row.BuildColumns());
        Relayout();
    }

    /// <summary>
    /// Lays the columns out in one horizontal row, left to right in <see cref="WindowInstance.Sequence"/>
    /// order. Collapsed columns take a fixed slim width; the rest split what remains by weight.
    /// Every tab in a column takes the column's place and width, so the one that comes forward
    /// is already where it will be seen. The only layout there is.
    /// </summary>
    public void Relayout()
    {
        var columns = _row.BuildColumns();

        if (columns.Count == 0)
        {
            // An empty desk is not any workspace, however it got there.
            ActiveWorkspace = null;
            Notify();
            return;
        }

        RowLayout.Lay(columns, _row.Windows, DeskWidth, Settings.PrimaryWindowKey, Settings.PrimaryShare);
        Notify();
    }
}
