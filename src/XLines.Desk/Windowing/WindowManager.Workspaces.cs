namespace XLines.Desk.Windowing;

public partial class WindowManager
{
    // ------------------------------------------------------- layout and workspaces ----

    /// <summary>
    /// Opens a named set of windows, replacing whatever is on the desk — unless a window on it
    /// holds unsaved input, in which case nothing changes and <see cref="CloseNeedsConfirmation"/>
    /// is raised with the first such window. A workspace replaces every window, so one typed-in
    /// window stops the lot, as Close all does. True when the workspace is on the desk.
    ///
    /// <para>
    /// The column limit stands. A workspace with more windows than columns opens the rest as
    /// tabs — in the columns it names, when it names them, or folded left to right when it does
    /// not — so the first windows it lists are the ones in view.
    /// </para>
    /// </summary>
    public bool ApplyWorkspace(Workspace workspace)
    {
        if (_row.Row().FirstOrDefault(w => w.HasUnsavedInput) is { } dirty)
        {
            Refuse(dirty);
            return false;
        }

        if (_row.Windows.Count > 0)
            Remember();

        _row.Clear();
        SetNotice(null);

        _row.Arrange(_store.Windows(workspace, _row.NextId));

        FocusedId = _row.BuildColumns().FirstOrDefault()?.Showing.Id;
        ActiveWorkspace = workspace;
        _row.FoldToLimit();
        Relayout();
        return true;
    }

    /// <summary>Workspaces the operator saved on this machine, after the application's own.</summary>
    public IReadOnlyList<Workspace> UserWorkspaces => _store.UserWorkspaces;

    /// <summary>
    /// Saves the row as a named workspace, replacing one of the same name: its columns left to
    /// right and the tabs in each, the showing one first. Only windows that open on nothing go
    /// in: a workspace is a starting arrangement, and "this order's history" is not something to
    /// start a morning on.
    /// </summary>
    public Workspace? SaveWorkspace(string name)
    {
        if (_store.Save(name, _row.BuildColumns()) is not { } workspace)
            return null;

        Notify();
        return workspace;
    }

    public void DeleteWorkspace(string name)
    {
        if (_store.Delete(name))
            Notify();
    }

    /// <summary>The desk as it can be written down. See <see cref="DeskLayout"/>.</summary>
    public DeskLayout Capture()
        => _store.Capture(_row.BuildColumns(), FocusedId, Settings, _ceilingChosen, _row.Windows.Count, ActiveWorkspace);

    /// <summary>
    /// Puts a written-down desk back. Windows the catalogue no longer has are dropped rather than
    /// failing the rest — a layout outlives the version of the application that saved it — and so
    /// are windows the person at this desk may not see. A layout from before tab groups gives every
    /// window a column; columns past the limit fold into tabs. Nothing is closed to fit.
    /// </summary>
    public void Restore(DeskLayout layout)
    {
        _row.Clear();
        SetNotice(null);
        _history.Clear();

        _ceilingChosen = layout.CeilingChosen;
        _store.RestoreSettings(layout, Settings);

        var (placed, focused) = _store.Windows(layout, _row.NextId);
        _row.Arrange(placed);

        // A column collapses whole, and the one it shows says which way.
        _row.CollapseWithShowing();

        _row.FoldToLimit();
        Notice = null;

        _store.RestoreWorkspaces(layout);
        ActiveWorkspace = _store.ActiveFrom(layout);

        FocusedId = focused is not null && IsShowing(focused)
            ? focused.Id
            : _row.BuildColumns().FirstOrDefault()?.Showing.Id;

        Relayout();
    }
}
