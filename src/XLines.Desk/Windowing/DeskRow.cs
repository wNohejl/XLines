namespace XLines.Desk.Windowing;

/// <summary>
/// The windows on one desk and the columns they make: which windows are open, which column each
/// is in, the order of the tabs inside it, and which one the operator is in. What
/// <see cref="WindowManager"/> changes when it opens, focuses, moves or folds a window, without the
/// events, the history or the notice — those stay with the manager, which says when a change is
/// done. Reads the operator's settings for the column limit and the primary window, and writes none.
/// </summary>
internal sealed class DeskRow
{
    private readonly DeskSettings _settings;
    private long _sequence;
    private long _recency;
    private int _columnSequence;

    public DeskRow(DeskSettings settings) => _settings = settings;

    /// <summary>Every open window, in the order they were opened — showing or waiting as a tab.</summary>
    public List<WindowInstance> Windows { get; } = [];

    /// <summary>The window the operator is in. Always one that is showing, never a tab behind another.</summary>
    public string? FocusedId { get; set; }

    /// <summary>The count the last <see cref="NextId"/> took, which a window opened on its own column starts its place from.</summary>
    public long LastSequence => _sequence;

    /// <summary>A new window's id: its definition's key and the next count.</summary>
    public string NextId(WindowDefinition definition) => $"{definition.Key}-{++_sequence}";

    /// <summary>The next recency: a window given it is the one shown most recently.</summary>
    public long NextRecency() => ++_recency;

    public string NewColumnId() => $"col-{++_columnSequence}";

    public WindowInstance? Find(string id) => Windows.FirstOrDefault(w => w.Id == id);

    /// <summary>Empties the desk: no windows, no focus.</summary>
    public void Clear()
    {
        Windows.Clear();
        FocusedId = null;
    }

    // The last row built, and the state it was built from: each window in list order with its
    // column, recency and sequence — everything the grouping and both sorts read. The manager has
    // no single point every change passes through (a window's fields are written in dozens of
    // places, some between two reads in one operation), so the cache is not invalidated by the
    // writers; it is checked on every read against the state it was built from, and rebuilt the
    // moment any of it differs. Checking is one pass over a handful of windows with no allocation,
    // where building is a grouping, two sorts and a list per column — and a stale row can never
    // be served, because the row is a function of exactly what is compared.
    private List<WindowColumn>? _columns;
    private (WindowInstance Window, string ColumnId, long Recency, long Sequence)[] _columnsBuiltFrom = [];

    /// <summary>The row, left to right, each column with its tabs most recently shown first.</summary>
    public List<WindowColumn> BuildColumns()
    {
        if (_columns is not null && ColumnsStillHold())
            return _columns;

        _columns = RowLayout.Compose(Windows);
        _columnsBuiltFrom = Windows.Select(w => (w, w.ColumnId, w.Recency, w.Sequence)).ToArray();
        return _columns;
    }

    private bool ColumnsStillHold()
    {
        if (_columnsBuiltFrom.Length != Windows.Count)
            return false;

        for (var i = 0; i < Windows.Count; i++)
        {
            var window = Windows[i];
            var (built, columnId, recency, sequence) = _columnsBuiltFrom[i];

            if (!ReferenceEquals(window, built)
                || !string.Equals(window.ColumnId, columnId, StringComparison.Ordinal)
                || window.Recency != recency
                || window.Sequence != sequence)
                return false;
        }

        return true;
    }

    /// <summary>The column a window is in, or null for a window not on the desk.</summary>
    public WindowColumn? ColumnOf(string id)
        => Find(id) is { } window ? BuildColumns().First(c => c.Id == window.ColumnId) : null;

    /// <summary>
    /// Every window, column by column left to right, the showing tab first in each. Includes
    /// collapsed columns and tabs behind — the row is the only list of open windows there is.
    /// </summary>
    public List<WindowInstance> Row() => BuildColumns().SelectMany(c => c.Tabs).ToList();

    /// <summary>The showing window last shown, in a column that is not collapsed: where focus goes when its window goes.</summary>
    public WindowInstance? MostRecent()
        => BuildColumns()
            .Where(c => !c.Showing.Minimised)
            .Select(c => c.Showing)
            .OrderByDescending(w => w.Recency)
            .FirstOrDefault();

    /// <summary>The column holding the row on its own, if one is maximised.</summary>
    public WindowColumn? MaximisedColumn
        => BuildColumns().FirstOrDefault(c => c.Showing.Maximised && !c.Showing.Minimised);

    /// <summary>Whether a column other than <paramref name="window"/>'s holds the row.</summary>
    public bool HeldElsewhere(WindowInstance window)
        => Windows.Any(w => w.Maximised && w.ColumnId != window.ColumnId);

    /// <summary>See <see cref="RowLayout.Pick"/>, with this desk's primary window.</summary>
    public WindowColumn? Pick(IReadOnlyList<WindowColumn> columns, string? avoid)
        => RowLayout.Pick(columns, avoid, _settings.PrimaryWindowKey);

    /// <summary>
    /// Where a window opened at the limit goes: a tab in the window last selected, so what was
    /// just asked for lands where the operator is looking (amended 2026-11-23). It used to avoid
    /// that column and spread tabs across the others, and a window chosen from the menu turned
    /// up somewhere other than the window the operator had been in. With nothing selected the
    /// old rule (<see cref="RowLayout.Pick"/>) still chooses.
    /// </summary>
    public WindowColumn? TabTarget(IReadOnlyList<WindowColumn> columns)
        => (FocusedId is { } focused ? columns.FirstOrDefault(c => c.Tabs.Any(t => t.Id == focused)) : null)
           ?? Pick(columns, avoid: null);

    /// <summary>
    /// Puts <paramref name="window"/> in <paramref name="column"/>, sharing its place and width.
    /// In front of the tabs already there when it is the one being opened; behind them when a
    /// column is folding in, so what the column was showing keeps showing.
    /// </summary>
    public void Join(WindowInstance window, WindowColumn column, bool inFront)
    {
        window.ColumnId = column.Id;
        window.Sequence = column.Tabs.Min(t => t.Sequence);
        window.Weight = column.Showing.Weight;

        // Maximised is the column's: a tab joining one that holds the row holds it with the rest,
        // or the bar of whichever came forward would offer the wrong key.
        window.Maximised = column.Showing.Maximised;

        if (inFront)
        {
            // The window just opened must be seen, so a collapsed column it lands in opens out.
            foreach (var tab in column.Tabs)
                tab.Minimised = false;

            window.Minimised = false;
            window.Recency = ++_recency;
        }
        else
        {
            window.Minimised = column.Showing.Minimised;
            window.Recency = column.Tabs.Min(t => t.Recency) - 1;
        }

        window.Touch();
        RowLayout.TouchAll(column.Tabs);
    }

    /// <summary>
    /// Folds the columns past the limit into tabs, the leftmost extra column first, each into the
    /// column <see cref="Pick"/> chooses among those that remain. The tabs that move go behind the
    /// ones there, in the order they had, so every column the operator can see keeps showing what
    /// it showed. Closes nothing.
    /// </summary>
    public void FoldToLimit()
    {
        var limit = Math.Max(1, _settings.MaxConcurrentWindows);

        while (true)
        {
            var columns = BuildColumns();

            if (columns.Count <= limit)
                return;

            var extra = columns[limit];
            var target = Pick(columns.Take(limit).ToList(), avoid: null)!;

            // Most recent first, each landing behind the one moved before it, so the order holds.
            foreach (var tab in extra.Tabs)
            {
                Join(tab, target, inFront: false);
                target = BuildColumns().First(c => c.Id == target.Id);
            }

            if (FocusedId is { } focused && extra.Tabs.Any(t => t.Id == focused))
                FocusedId = target.Showing.Id;
        }
    }

    /// <summary>
    /// Restores the row when a column other than <paramref name="window"/>'s holds it. Focus is
    /// always on a window that is drawn, and while a column is maximised only that column is in
    /// view: a window reached outside it — from the palette, a follow-up, a tab moved out — would
    /// otherwise be focused behind it, unseen.
    /// </summary>
    public void GiveRowBackFor(WindowInstance window)
    {
        if (!HeldElsewhere(window))
            return;

        foreach (var w in Windows.Where(w => w.Maximised))
        {
            w.Maximised = false;
            w.Touch();
        }
    }

    /// <summary>Lets go of the row, wherever it is held.</summary>
    public void GiveRowBack()
    {
        foreach (var w in Windows.Where(w => w.Maximised))
        {
            w.Maximised = false;
            w.Touch();
        }
    }

    /// <summary>
    /// Takes a tab out of its group into a column of its own, just right of the one it left, at
    /// its own default width, and puts the operator in it.
    /// </summary>
    public void MoveToOwnColumn(WindowInstance window)
    {
        var columns = BuildColumns();
        var from = columns.First(c => c.Id == window.ColumnId);

        window.ColumnId = NewColumnId();
        window.Weight = window.Definition.DefaultWeight;
        window.Minimised = false;
        window.Maximised = false;

        var order = columns.Select(c => c.Tabs.Where(t => t != window).ToList()).ToList();
        order.Insert(from.Position + 1, [window]);
        RowLayout.Renumber(order);

        window.Recency = ++_recency;
        window.LastFocusedAt = DateTimeOffset.UtcNow;
        FocusedId = window.Id;

        // The column it left may hold the row; the window the operator is now in must be drawn.
        GiveRowBackFor(window);

        RowLayout.TouchAll(from.Tabs);
    }

    /// <summary>
    /// Moves <paramref name="window"/> into the column holding <paramref name="target"/>, in front,
    /// closes the row up behind a column the move emptied, and puts the operator in it.
    /// </summary>
    public void MoveIntoColumn(WindowInstance window, WindowInstance target)
    {
        var columns = BuildColumns();
        var from = columns.First(c => c.Id == window.ColumnId);
        var to = columns.First(c => c.Id == target.ColumnId);

        Join(window, to, inFront: true);
        RowLayout.TouchAll(from.Tabs);

        // The row closes up behind a column the move emptied, and keeps every other place.
        RowLayout.Renumber(BuildColumns().Select(c => c.Tabs));

        window.LastFocusedAt = DateTimeOffset.UtcNow;
        FocusedId = window.Id;
        GiveRowBackFor(window);
    }

    /// <summary>
    /// Makes <paramref name="window"/> the one the operator is in: in front of its column, the
    /// column open, the row given back if another column held it. True when how width is shared
    /// may have changed, so the row must be laid out again; false for a plain focus.
    /// </summary>
    public bool BringForward(WindowInstance window)
    {
        var column = ColumnOf(window.Id)!;
        var index = IndexIn(column, window);
        var wasCollapsed = column.Showing.Minimised;
        var heldElsewhere = HeldElsewhere(window);

        foreach (var tab in column.Tabs)
            tab.Minimised = false;

        GiveRowBackFor(window);

        window.Recency = ++_recency;
        window.LastFocusedAt = DateTimeOffset.UtcNow;
        window.Touch();

        // Every tab in the column moves when one comes forward, so every window in the column
        // draws again: the one revealed, the one it covers, and the order between them.
        if (index > 0 || wasCollapsed)
            RowLayout.TouchAll(column.Tabs);

        FocusedId = window.Id;

        // Expanding changes how width is shared, so it must be recomputed, and so does giving
        // the row back. A plain focus does not — the row keeps its order and sizes, which is
        // what makes a tiled desk stable to work in.
        return wasCollapsed || heldElsewhere;
    }

    /// <summary>
    /// Focused, the tab its column shows, nothing in its column collapsed, no other column holding
    /// the row, and the last window brought forward by count and by clock — so a focus would move
    /// neither the tab order, nor what a close falls back to, nor anything drawn.
    /// </summary>
    public bool IsAlreadyIn(string id)
    {
        if (FocusedId != id || Find(id) is not { } window || ColumnOf(id) is not { } column)
            return false;

        return column.Showing == window
               && column.Tabs.All(t => !t.Minimised)
               && !HeldElsewhere(window)
               && Windows.All(w => w == window || (w.Recency < window.Recency && w.LastFocusedAt <= window.LastFocusedAt));
    }

    private static int IndexIn(WindowColumn column, WindowInstance window)
    {
        for (var i = 0; i < column.Tabs.Count; i++)
        {
            if (column.Tabs[i] == window)
                return i;
        }

        return 0;
    }

    /// <summary>
    /// Collapses a column whole: its tabs wait behind the stub as they waited behind the window,
    /// panels and input kept (AppWindow hides the body rather than dropping it). A collapsed
    /// column holds no row, so one that held it gives it back rather than taking it again the
    /// moment it opens out. Focus leaves it for the window last shown elsewhere.
    /// </summary>
    public void Collapse(WindowColumn column)
    {
        foreach (var tab in column.Tabs)
        {
            tab.Minimised = true;
            tab.Maximised = false;
            tab.Touch();
        }

        if (column.Tabs.Any(t => t.Id == FocusedId))
            FocusedId = MostRecent()?.Id;
    }

    /// <summary>
    /// Reassigns the row's order: the columns the ids name first, in that order, then every other
    /// column in the order it had.
    /// </summary>
    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        var columns = BuildColumns();
        var order = new List<WindowColumn>();

        foreach (var id in orderedIds)
        {
            if (Find(id) is { } window
                && columns.First(c => c.Id == window.ColumnId) is var column
                && !order.Contains(column))
            {
                order.Add(column);
            }
        }

        order.AddRange(columns.Where(c => !order.Contains(c)));
        RowLayout.Renumber(order.Select(c => c.Tabs));
    }

    /// <summary>
    /// Puts windows built from a saved list on the desk: one column per distinct index, in index
    /// order, and inside each the window ranked 0 showing with the rest behind it in rank order.
    /// The tabs in one column take the weight of the one showing.
    /// </summary>
    public void Arrange(IEnumerable<(WindowInstance Window, int Column, int Rank)> placed)
    {
        foreach (var group in placed.GroupBy(p => p.Column).OrderBy(g => g.Key))
        {
            var id = NewColumnId();
            var tabs = group.OrderBy(p => p.Rank).Select(p => p.Window).ToList();

            // Showing last, so it takes the highest count.
            foreach (var window in Enumerable.Reverse(tabs))
            {
                window.ColumnId = id;
                window.Sequence = group.Key;
                window.Recency = ++_recency;
                Windows.Add(window);
            }

            foreach (var window in tabs.Skip(1))
                window.Weight = tabs[0].Weight;
        }

        RowLayout.Renumber(BuildColumns().Select(c => c.Tabs));
    }

    /// <summary>A column collapses whole, and the one it shows says which way: every tab is made to agree with it.</summary>
    public void CollapseWithShowing()
    {
        foreach (var column in BuildColumns())
        {
            foreach (var tab in column.Tabs)
                tab.Minimised = column.Showing.Minimised;
        }
    }

    /// <summary>
    /// Shows a window that has just turned to another page: in front of its column, the column
    /// open, the operator in it.
    /// </summary>
    public void Reveal(WindowInstance window)
    {
        var column = ColumnOf(window.Id)!;

        foreach (var tab in column.Tabs)
            tab.Minimised = false;

        if (column.Showing != window)
        {
            RowLayout.TouchAll(column.Tabs);
        }

        window.Recency = ++_recency;
        window.LastFocusedAt = DateTimeOffset.UtcNow;
        window.Touch();
        FocusedId = window.Id;
    }
}
