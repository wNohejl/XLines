namespace XLines.Desk.Windowing;

/// <summary>
/// Keeps the row within the browser's width. A window dragged narrower used to keep every
/// column and give each a share under its own minimum — four columns at 900px were 214px
/// apiece, their panels scrolling sideways and their title bars cut off before the close key.
/// Now the rightmost column the operator is not in folds into another as a tab, one at a time,
/// until the minimums fit; nothing closes and the column limit (the operator's setting) is not
/// touched. Widened again, the latest fold comes back out first, to the place it had, while it
/// fits and the limit allows. A declared resolution is not the browser's, and a stacked desk
/// has no row to fit, so neither folds.
/// </summary>
internal sealed class ViewportFit
{
    private readonly DeskRow _row;
    private readonly DeskSettings _settings;

    /// <summary>
    /// Columns folded into tabs because the browser grew too narrow for their minimums: each the
    /// windows one column held and the place it stood in the row, the latest fold last. Only these
    /// come back out when the browser widens again — a tab the operator made stays a tab.
    /// </summary>
    private readonly List<(List<string> Ids, int Position)> _foldedForWidth = [];

    public ViewportFit(DeskRow row, DeskSettings settings)
    {
        _row = row;
        _settings = settings;
    }

    /// <summary>Folds or unfolds the row for a desk <paramref name="deskWidth"/> wide. See the class.</summary>
    public void Fit(double deskWidth)
    {
        if (_settings.ResolutionMode == ResolutionMode.Fixed || deskWidth <= RowLayout.StackedWidth)
            return;

        var folded = false;

        while (_row.BuildColumns() is { Count: > 1 } columns && RowLayout.Required(columns.Select(c => c.Tabs)) > deskWidth)
        {
            // Never the column the operator is in, nor one holding the row: what they are looking
            // at stays where it is.
            var extra = columns.LastOrDefault(c => c.Tabs.All(t => t.Id != _row.FocusedId) && !c.Showing.Maximised);
            if (extra is null)
                break;

            var target = _row.Pick(columns.Where(c => c != extra).ToList(), avoid: null)!;
            _foldedForWidth.Add((extra.Tabs.Select(t => t.Id).ToList(), extra.Position));

            foreach (var tab in extra.Tabs)
            {
                _row.Join(tab, target, inFront: false);
                target = _row.BuildColumns().First(c => c.Id == target.Id);
            }

            RowLayout.Renumber(_row.BuildColumns().Select(c => c.Tabs));
            folded = true;
        }

        if (!folded)
            Unfold(deskWidth);
    }

    private void Unfold(double deskWidth)
    {
        while (_foldedForWidth.Count > 0)
        {
            var (ids, position) = _foldedForWidth[^1];
            var set = ids.Select(_row.Find).OfType<WindowInstance>().ToHashSet();

            // Only what still shares a column with a window it did not bring: one closed since is
            // gone, and a column now holding nothing else is already a column of its own.
            var moving = set.Where(w => _row.Windows.Any(o => o.ColumnId == w.ColumnId && !set.Contains(o))).ToList();

            if (moving.Count == 0)
            {
                _foldedForWidth.RemoveAt(_foldedForWidth.Count - 1);
                continue;
            }

            var columns = _row.BuildColumns();
            if (columns.Count >= _settings.MaxConcurrentWindows)
                return;

            var remaining = columns
                .Select(c => c.Tabs.Where(t => !moving.Contains(t)).ToList())
                .Where(tabs => tabs.Count > 0)
                .ToList();

            if (RowLayout.Required(remaining.Append(moving)) > deskWidth)
                return;

            _foldedForWidth.RemoveAt(_foldedForWidth.Count - 1);

            var column = _row.NewColumnId();
            foreach (var window in moving)
            {
                window.ColumnId = column;
                window.Weight = window.Definition.DefaultWeight;
                window.Minimised = false;
                window.Maximised = false;
                window.Touch();
            }

            foreach (var tabs in remaining)
                RowLayout.TouchAll(tabs);

            remaining.Insert(Math.Clamp(position, 0, remaining.Count), moving.OrderByDescending(w => w.Recency).ToList());
            RowLayout.Renumber(remaining);
        }
    }

    /// <summary>Takes a window out of the folds it is remembered in: the operator has put it somewhere themselves.</summary>
    public void Forget(string id)
    {
        foreach (var (ids, _) in _foldedForWidth)
            ids.Remove(id);
    }
}
