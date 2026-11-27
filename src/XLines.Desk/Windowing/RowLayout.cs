namespace XLines.Desk.Windowing;

/// <summary>
/// The row's arithmetic, apart from the manager that owns the row: how many columns fit, which
/// column a tab goes into, how wide each column is and where it stands. Every member takes what it
/// reads as arguments and holds nothing between calls; the ones that write, write only the
/// windows they are handed, and move a window's version only when what it draws moved.
/// </summary>
/// <remarks>
/// Public so it is tested without a manager: the library has no InternalsVisibleTo, and granting
/// one makes the internal <c>Primitives.DeskLayout</c> collide with <see cref="DeskLayout"/> in
/// the tests. An application has no reason to call it; <see cref="WindowManager"/> is the desk.
/// </remarks>
public static class RowLayout
{
    /// <summary>Gap between columns and around the row, in CSS pixels.</summary>
    public const double Gap = 8;

    /// <summary>
    /// The narrowest a column may be before the window in it stops being readable. The widest
    /// <c>MinWidth</c> in the catalogue, so the guidance holds for whatever is opened rather
    /// than for the most forgiving thing that could be.
    /// </summary>
    public const double ReadableWidth = 420;

    /// <summary>
    /// The width under which desk.css stacks the columns into one scrolling list. Below it no
    /// column is laid out side by side, so the row's minimums have nothing to fit.
    /// </summary>
    public const double StackedWidth = 720;

    /// <summary>
    /// How many columns fit at a readable width on a desk <paramref name="deskWidth"/> wide. See
    /// <see cref="WindowManager.RecommendedCapacity"/> for why the primary's share is taken first.
    /// </summary>
    public static int RecommendedCapacity(double deskWidth, bool hasPrimary, double primaryShare)
    {
        var share = Math.Clamp(primaryShare, 0.2, 0.8);

        for (var n = 12; n > 1; n--)
        {
            var usable = deskWidth - Gap * (n + 1);

            if (usable <= 0)
                continue;

            // Without a primary every column is the narrowest; with one, the narrowest is
            // whatever is left after its share is taken.
            var narrowest = hasPrimary
                ? usable * (1 - share) / (n - 1)
                : usable / n;

            if (narrowest >= ReadableWidth)
                return n;
        }

        // One window is always allowed the whole desk, however narrow. The alternative is
        // guidance that recommends showing nothing.
        return 1;
    }

    /// <summary>The width a row of these columns needs with each at its minimum: a collapsed one its stub, the gaps around them all.</summary>
    public static double Required(IEnumerable<IReadOnlyList<WindowInstance>> columns)
    {
        var list = columns.ToList();

        return list.Sum(tabs => tabs[0].Minimised ? DeskSettings.CollapsedWidth : tabs.Max(t => t.Definition.MinWidth))
               + Gap * (list.Count + 1);
    }

    /// <summary>
    /// The row, left to right, built from the windows in the order they were opened: one column
    /// per column id, in sequence order, each with its tabs most recently shown first.
    /// </summary>
    public static List<WindowColumn> Compose(IReadOnlyList<WindowInstance> windows)
    {
        int IndexOf(WindowInstance window)
        {
            for (var i = 0; i < windows.Count; i++)
            {
                if (Equals(windows[i], window))
                    return i;
            }

            return -1;
        }

        var columns = windows
            .GroupBy(w => w.ColumnId)
            .Select(g => (Id: g.Key,
                          Tabs: g.OrderByDescending(w => w.Recency).ThenBy(w => w.Sequence).ToList(),
                          Order: g.Min(w => w.Sequence)))
            .OrderBy(c => c.Order)
            .ThenBy(c => c.Tabs.Min(IndexOf))
            .ToList();

        return columns.Select((c, i) => new WindowColumn(c.Id, c.Tabs, i)).ToList();
    }

    /// <summary>
    /// Where a column folds when the limit drops, and where a window opened at the limit goes
    /// with nothing selected. One rule, read left to right:
    ///
    /// <list type="number">
    /// <item>never the column the operator is working in, while there is another — the window
    /// they are reading stays in view beside the new one;</item>
    /// <item>a collapsed column only when every column is — what was set aside stays aside;</item>
    /// <item>the primary window's column last, so the larger share keeps showing the window it
    /// was given to;</item>
    /// <item>then the column with the fewest tabs, the leftmost of those first.</item>
    /// </list>
    ///
    /// The last two make the groups fill left to right and stay balanced: with three columns,
    /// the fourth window tabs into the first, the fifth into the second, the sixth into the third.
    /// </summary>
    public static WindowColumn? Pick(IReadOnlyList<WindowColumn> columns, string? avoid, string? primaryKey)
    {
        if (columns.Count == 0)
            return null;

        var candidates = columns.Where(c => c.Tabs.All(t => t.Id != avoid)).ToList();

        if (candidates.Count == 0)
            candidates = [.. columns];

        return candidates
            .OrderBy(c => c.Showing.Minimised)
            .ThenBy(c => IsPrimaryColumn(c, primaryKey))
            .ThenBy(c => c.Tabs.Count)
            .ThenBy(c => c.Position)
            .First();
    }

    /// <summary>Whether the column holds the primary window, showing or not.</summary>
    public static bool IsPrimaryColumn(WindowColumn column, string? primaryKey)
        => primaryKey is { } key && column.Tabs.Any(t => t.Definition.Key == key);

    /// <summary>Gives every column its place in the row, from 0, and moves the version of each window whose place moved.</summary>
    public static void Renumber(IEnumerable<IEnumerable<WindowInstance>> columns)
    {
        var position = 0;

        foreach (var column in columns)
        {
            foreach (var window in column.Where(w => w.Sequence != position))
            {
                window.Sequence = position;
                window.Touch();
            }

            position++;
        }
    }

    public static void TouchAll(IEnumerable<WindowInstance> windows)
    {
        foreach (var window in windows)
            window.Touch();
    }

    public static void SetWeight(WindowColumn column, double weight)
    {
        foreach (var tab in column.Tabs)
        {
            tab.Weight = weight;
            tab.Touch();
        }
    }

    /// <summary>A column's own width when nothing has been dragged: the widest default among its tabs.</summary>
    public static double DefaultWeightOf(WindowColumn column) => column.Tabs.Max(t => t.Definition.DefaultWeight);

    /// <summary>
    /// Whether a column still sits at a default width: its weight is the default one of its own
    /// tabs gives it — the one it opened at, which every tab that joined it took on.
    /// </summary>
    public static bool AtDefaultWidth(WindowColumn column)
        => column.Tabs.Any(t => Math.Abs(column.Showing.Weight - t.Definition.DefaultWeight) <= 0.001);

    /// <summary>
    /// A divider drag between two columns: their combined weight split by the widths dragged to.
    /// False, and nothing written, when the two are one column or there is nothing to split.
    /// </summary>
    public static bool Split(WindowColumn left, WindowColumn right, double leftWidthPx, double rightWidthPx)
    {
        if (left.Id == right.Id)
            return false;

        var totalWeight = left.Showing.Weight + right.Showing.Weight;
        var totalPx = leftWidthPx + rightWidthPx;

        if (totalPx <= 0 || totalWeight <= 0)
            return false;

        var leftWeight = totalWeight * (leftWidthPx / totalPx);
        SetWeight(left, leftWeight);
        SetWeight(right, totalWeight - leftWeight);
        return true;
    }

    /// <summary>Returns every column to its default width, writing only the ones not already there.</summary>
    public static void ResetWidths(IEnumerable<WindowColumn> columns)
    {
        foreach (var column in columns)
        {
            var weight = DefaultWeightOf(column);

            if (column.Tabs.Any(t => t.Weight != weight))
                SetWeight(column, weight);
        }
    }

    /// <summary>
    /// Lays a row that has columns out: left to right in <see cref="WindowInstance.Sequence"/>
    /// order, collapsed columns at a fixed slim width, the rest splitting what remains by weight,
    /// every tab at its column's place and width. While a column holds the row, every window takes
    /// the whole desk. <paramref name="windows"/> is every window on the desk, in list order.
    /// </summary>
    public static void Lay(
        IReadOnlyList<WindowColumn> columns,
        IEnumerable<WindowInstance> windows,
        double deskWidth,
        string? primaryKey,
        double primaryShare)
    {
        if (columns.Any(c => c.Showing.Maximised && !c.Showing.Minimised))
        {
            // Only the maximised column is in view — the others stay mounted, hidden behind it —
            // so their geometry is irrelevant; keeping it sane avoids a jump when it is restored.
            foreach (var w in windows)
                Place(w, Gap, deskWidth - Gap * 2);

            return;
        }

        var expanded = columns.Where(c => !c.Showing.Minimised).ToList();
        var collapsedCount = columns.Count - expanded.Count;

        ApplyPrimaryShare(expanded, primaryKey, primaryShare);

        var usable = deskWidth - Gap * (columns.Count + 1) - collapsedCount * DeskSettings.CollapsedWidth;
        var totalWeight = expanded.Sum(c => c.Showing.Weight);

        if (totalWeight <= 0)
            totalWeight = Math.Max(expanded.Count, 1);

        var widths = ColumnWidths(expanded, Math.Max(usable, 0), totalWeight);
        var offset = Gap;

        foreach (var column in columns)
        {
            var width = column.Showing.Minimised ? DeskSettings.CollapsedWidth : widths[column.Id];

            foreach (var tab in column.Tabs)
                Place(tab, offset, width);

            offset += width + Gap;
        }
    }

    /// <summary>
    /// Sets a column's place in the row, and moves its version only when the place moved: a
    /// relayout that leaves a column where it was leaves its chrome undrawn, and one that moves
    /// it is drawn where it now is.
    /// </summary>
    public static void Place(WindowInstance window, double x, double width)
    {
        if (window.X == x && window.Width == width)
            return;

        window.X = x;
        window.Width = width;
        window.Touch();
    }

    /// <summary>
    /// Splits the row's usable width between the expanded columns by weight, and the row
    /// always sums to that width — it never runs past the desk.
    ///
    /// <para>
    /// A column whose share falls under its minimum is lifted to the minimum and the rest
    /// divide what is left, so a minimum costs its neighbours rather than the desk's edge.
    /// When the minimums together do not fit — two columns at 800px, one of them the list's
    /// 680 — they cannot all be honoured, and every column takes its plain share instead and
    /// scrolls inside itself. Overflowing the row instead put the last window's right half
    /// off-screen behind a sideways scroll, which is the arrangement capacity exists to avoid.
    /// </para>
    /// </summary>
    public static Dictionary<string, double> ColumnWidths(
        List<WindowColumn> expanded, double usable, double totalWeight)
    {
        var widths = new Dictionary<string, double>();

        if (expanded.Count == 0)
            return widths;

        double Share(WindowColumn c, double room, double weight)
            => weight > 0 ? room * (c.Showing.Weight / weight) : room / expanded.Count;

        if (expanded.Sum(c => c.MinWidth) > usable)
        {
            foreach (var c in expanded)
                widths[c.Id] = Share(c, usable, totalWeight);

            return widths;
        }

        var free = new List<WindowColumn>(expanded);
        var room = usable;

        // Lift every column under its minimum, then re-divide among the rest; lifting one can
        // push another under, so repeat until nothing more is lifted. Terminates: each pass
        // removes at least one column or stops.
        while (true)
        {
            var weight = free.Sum(c => c.Showing.Weight);
            var under = free.Where(c => Share(c, room, weight) < c.MinWidth).ToList();

            if (under.Count == 0)
            {
                foreach (var c in free)
                    widths[c.Id] = Share(c, room, weight);

                return widths;
            }

            foreach (var c in under)
            {
                widths[c.Id] = c.MinWidth;
                room -= c.MinWidth;
                free.Remove(c);
            }
        }
    }

    /// <summary>
    /// Nudges the primary window's column so it lands on its configured share, unless the
    /// operator has since dragged a divider — a manual adjustment outranks a default. Only
    /// affects width: the column's position in the row is whatever the operator put it at, same
    /// as any other. The primary's column is the one holding the primary window, showing or not.
    /// </summary>
    public static void ApplyPrimaryShare(List<WindowColumn> expanded, string? primaryKey, double primaryShare)
    {
        if (primaryKey is null || expanded.Count < 2)
            return;

        var primary = expanded.FirstOrDefault(c => IsPrimaryColumn(c, primaryKey));
        if (primary is null)
            return;

        // Untouched columns all sit at a default weight; if any differ, widths are the
        // operator's and this leaves them alone.
        var others = expanded.Where(c => c != primary).ToList();
        if (others.Any(c => !AtDefaultWidth(c)))
            return;

        var share = Math.Clamp(primaryShare, 0.2, 0.8);
        var othersWeight = others.Sum(c => c.Showing.Weight);

        var weight = othersWeight * share / (1 - share);

        if (primary.Tabs.All(t => t.Weight == weight))
            return;

        SetWeight(primary, weight);
    }
}
