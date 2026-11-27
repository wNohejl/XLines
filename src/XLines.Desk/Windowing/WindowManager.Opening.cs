namespace XLines.Desk.Windowing;

public partial class WindowManager
{
    // -------------------------------------------------------------------- open ----

    /// <summary>
    /// Opens a window: a column of its own while one is free, and a tab in an existing column
    /// once every column is in use (<see cref="RowLayout.Pick"/>). Nothing closes to make room.
    ///
    /// <para>
    /// The one refusal is the desk's ceiling, <see cref="DeskSettings.MaxOpenWindows"/>: then
    /// nothing opens, <see cref="Notice"/> says why, and what comes back is the window the
    /// operator is in.
    /// </para>
    /// </summary>
    public WindowInstance Open(
        WindowDefinition definition,
        Dictionary<string, object>? parameters = null,
        string? titleOverride = null)
    {
        // Every way a visitor could reach an operator window goes through Catalog, which does
        // not have one; arriving here with one means a control was drawn that should not have been.
        if (definition.OperatorOnly && !Access.CanOperate)
            throw new InvalidOperationException(
                $"'{definition.Key}' is an operator window and this desk is a visitor's.");

        if (definition.Singleton)
        {
            var existing = _row.Windows.FirstOrDefault(w => w.Definition.Key == definition.Key);
            if (existing is not null)
            {
                // Asked for on a subject — runs for one source, one incident — the window already
                // on the desk is re-aimed at it. Focusing it as it stood showed whatever it was
                // last opened on, which read as the desk ignoring the click. Re-aiming replaces
                // what it showed, so it is a step Back can undo.
                if (DeskHistory.Retargets(existing, parameters, titleOverride))
                {
                    Remember();
                    Aim(existing.Parameters, parameters ?? new Dictionary<string, object>());

                    if (titleOverride is not null)
                        existing.TitleOverride = titleOverride;
                }

                // A tab behind another is brought forward: the palette, the tab menu and a follow-up
                // never open a second copy of a window already on the desk.
                Focus(existing.Id);
                return existing;
            }
        }

        else if (_row.Windows.FirstOrDefault(w => w.Definition.Key == definition.Key && DeskHistory.SameSubject(w, parameters)) is { } same)
        {
            // A window that may be open more than once is open once per subject: asking again
            // for a subject already on the desk brings that window forward,
            // wherever it is — a tab behind another included — rather than drawing a second copy.
            Focus(same.Id);
            return same;
        }

        var title = titleOverride ?? definition.Title;

        if (AtCeiling)
        {
            SetNotice($"The desk holds {DeskSettings.MaxOpenWindows} windows, its most. Close one to open {title}.");

            var here = (FocusedId is { } focused ? Find(focused) : null) ?? _row.MostRecent() ?? _row.Windows[0];
            Notify();
            return here;
        }

        Remember();

        var instance = new WindowInstance
        {
            Id = _row.NextId(definition),
            Sequence = _row.LastSequence,
            Definition = definition,
            TitleOverride = titleOverride,
            Weight = definition.DefaultWeight,
            Recency = _row.NextRecency(),
            LastFocusedAt = DateTimeOffset.UtcNow
        };

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
                instance.Parameters[key] = value;
        }

        if (NextTabTarget is { } target)
            _row.Join(instance, target, inFront: true);
        else
            instance.ColumnId = _row.NewColumnId();

        _row.Windows.Add(instance);
        FocusedId = instance.Id;
        _row.GiveRowBackFor(instance);
        SetNotice(null);

        // A new column takes its place at the end of the row and the others give up the space
        // for it; a new tab takes its column's place and width, and the row does not move.
        Relayout();

        return instance;
    }

    /// <summary>
    /// Closes a window unless it holds unsaved input, in which case nothing closes and
    /// <see cref="CloseNeedsConfirmation"/> is raised with it. True when the window is gone.
    /// The close key goes through here; <see cref="Close"/> is what the answer Discard calls.
    /// </summary>
    public bool TryClose(string id)
    {
        if (Find(id) is { HasUnsavedInput: true } window)
        {
            CloseNeedsConfirmation?.Invoke(window);
            return false;
        }

        Close(id);
        return true;
    }

    /// <summary>
    /// Closes every window unless one holds unsaved input. One question for the lot, about the
    /// first such window in the row, rather than one per window. True when the desk is empty.
    /// </summary>
    public bool TryCloseAll()
    {
        if (_row.Row().FirstOrDefault(w => w.HasUnsavedInput) is { } window)
        {
            CloseNeedsConfirmation?.Invoke(window);
            return false;
        }

        CloseAll();
        return true;
    }

    /// <summary>
    /// Closes a window whatever it holds. See <see cref="TryClose"/>. Closing the tab a group is
    /// showing brings the next most recent forward; a group left with one window is a plain
    /// window again; a column left with none gives its width back to the row.
    /// </summary>
    public void Close(string id)
    {
        var window = Find(id);
        if (window is null)
            return;

        Remember();

        var column = ColumnOf(id)!;
        var rest = column.Tabs.Where(t => t != window).ToList();

        _row.Windows.Remove(window);
        RowLayout.TouchAll(rest);

        // Said as a sentence with a key that names the window, so the way back is a key rather
        // than a header arrow whose direction had to be guessed.
        SetNotice($"Closed {window.Title}.", [window.Title]);

        // Focus stays in the column the operator was working in: the tab revealed there is
        // where they are now looking.
        if (FocusedId == id)
            FocusedId = rest.Count > 0 ? rest[0].Id : _row.MostRecent()?.Id;

        Relayout();
    }

    /// <summary>Closes every window whatever they hold. See <see cref="TryCloseAll"/>.</summary>
    public void CloseAll()
    {
        if (_row.Windows.Count > 0)
            Remember();

        _row.Clear();
        SetNotice(null);
        ActiveWorkspace = null;
        Notify();
    }
}
