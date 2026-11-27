namespace XLines.Desk.Windowing;

/// <summary>
/// Owns every open window: the columns of the row, the tabs in each, widths, and focus.
///
/// Scoped per circuit, so each browser session has its own desk. State lives here rather
/// than in the components so that every surface — header, windows, dividers — reads one
/// source of truth.
///
/// The desk is always a horizontal row of full-height columns, up to the operator's column
/// limit. Past it nothing closes: the next window joins a column as a tab, and that column
/// becomes a tab group showing one window at a time (ADR 0027). Reordering the row (dragging
/// a column's bar past a neighbour) and resizing it (dragging a divider) are the only two
/// gestures on the row, and they are deliberately distinct hit targets so neither is ever
/// triggered by accident.
///
/// <para>
/// The manager is the desk's surface: it decides when a change is a step Back can take, what
/// the notice says, and when the desk has changed. The work behind it is in collaborators of its
/// own: <see cref="DeskRow"/> (the windows, their columns and tabs, focus), <see cref="ViewportFit"/>
/// (folding the row for a narrow browser), <see cref="RowLayout"/> (the arithmetic of the row),
/// <see cref="LayoutStore"/> (the desk written down, and workspaces) and <see cref="DeskHistory"/>
/// (Back and Forward, and each window's pages).
/// </para>
/// </summary>
public class WindowManager
{
    private readonly DeskRow _row;
    private readonly ViewportFit _fit;
    private readonly LayoutStore _store;
    private readonly DeskHistory _history = new();

    /// <summary>
    /// True once the operator has set the ceiling themselves, after which the first viewport
    /// measurement stops overriding it.
    /// </summary>
    private bool _ceilingChosen;

    private bool _viewportMeasured;

    /// <summary>
    /// Raised whenever the desk as a whole has changed: which windows are open, their order,
    /// widths and focus, the settings, the notice, the saved workspaces. Not for a change that
    /// stays inside one window — that is <see cref="WindowChanged"/>.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Raised with a window's id when something only that window draws has changed — its pulse,
    /// its status line. Kept apart from <see cref="Changed"/> because panels report often, at
    /// least twice as each one loads, and one event for both re-rendered the desk, the header,
    /// the footer and every window on it to repaint one status line. The window it names, the
    /// window whose tab key and menu carry it, and the bar carrying its dot listen; nothing else needs to.
    /// </summary>
    public event Action<string>? WindowChanged;

    /// <summary>
    /// Raised when a close was asked for and a window holds unsaved input, with that window. The
    /// close did not happen; whoever asked puts the question, and calls <see cref="Close"/> or
    /// <see cref="CloseAll"/> if the answer is Discard.
    /// </summary>
    public event Action<WindowInstance>? CloseNeedsConfirmation;

    /// <summary>Raised when something asks for the command palette — the hotkey or the header key.</summary>
    public event Action? PaletteRequested;

    /// <summary>
    /// Raised when a window is asked to reload what it shows — the Refresh key on its bar, or
    /// Alt+R on the focused one — with its id. The panel inside answers (DeskPanelBase); the
    /// manager changes nothing itself, so a refresh is never a step Back can undo.
    /// </summary>
    public event Action<string>? RefreshRequested;

    /// <summary>
    /// Asks a window to reload what it shows. True when the window exists; whether anything was
    /// reloaded is the panel's to say, on its pulse.
    /// </summary>
    public bool Refresh(string id)
    {
        if (Find(id) is null)
            return false;

        RefreshRequested?.Invoke(id);
        return true;
    }

    /// <summary>
    /// Turns a tab group to another of its windows from the keyboard. Forward
    /// (<paramref name="delta"/> above zero) brings the tab shown longest ago to the front, so
    /// pressing it again and again visits every tab in turn; back brings the one just left, so
    /// two tabs worked between are one key apart. False for a plain window: there is nothing to
    /// turn to.
    /// </summary>
    public bool CycleTab(string id, int delta)
    {
        if (Find(id) is null || ColumnOf(id) is not { IsGroup: true } column)
            return false;

        var next = delta > 0 ? column.Tabs[^1] : column.Tabs[1];

        RequestTabFocus(next.Id);
        Focus(next.Id);
        return true;
    }

    public void RequestPalette() => PaletteRequested?.Invoke();

    /// <summary>
    /// The windows this desk can open. Supplied by the application, read by everything here —
    /// through <see cref="IDeskAccess"/>, so a visitor's catalogue has no operator-only window
    /// in it and nothing that reads it has to remember to ask.
    /// </summary>
    public IWindowCatalog Catalog { get; }

    /// <summary>Who is at this desk. Read by the application where it draws a control that writes.</summary>
    public IDeskAccess Access { get; }

    public WindowManager(IWindowCatalog catalog)
        : this(catalog, OpenDeskAccess.Instance)
    {
    }

    public WindowManager(IWindowCatalog catalog, IDeskAccess access)
    {
        Access = access;
        Catalog = new AccessCatalog(catalog, access);
        Settings.PrimaryWindowKey = Catalog.DefaultPrimary;

        _row = new DeskRow(Settings);
        _fit = new ViewportFit(_row, Settings);
        _store = new LayoutStore(Catalog);
    }

    /// <summary>Every open window, in the order they were opened — showing or waiting as a tab.</summary>
    public IReadOnlyList<WindowInstance> Windows => _row.Windows;

    public DeskSettings Settings { get; } = new();

    /// <summary>The window the operator is in. Always one that is showing, never a tab behind another.</summary>
    public string? FocusedId
    {
        get => _row.FocusedId;
        private set => _row.FocusedId = value;
    }

    /// <summary>Measured browser desk area in CSS pixels. Set by the host on resize.</summary>
    public double ViewportWidth { get; private set; } = 1280;
    public double ViewportHeight { get; private set; } = 800;

    /// <summary>
    /// Whether the browser has reported the desk's size yet. Before it has, the row is laid out
    /// against the default above, and the desk keeps its windows out of sight rather than draw a
    /// layout it is about to replace.
    /// </summary>
    public bool ViewportMeasured => _viewportMeasured;

    /// <summary>
    /// The size layout works against: the browser, or the resolution the operator declared.
    /// Declaring one lets you lay a desk out for the screen you will run it on rather than
    /// the one you are configuring it from.
    /// </summary>
    public double DeskWidth => Settings.ResolutionMode == ResolutionMode.Fixed
        ? Settings.CustomWidth
        : ViewportWidth;

    public double DeskHeight => Settings.ResolutionMode == ResolutionMode.Fixed
        ? Settings.CustomHeight
        : ViewportHeight;

    /// <summary>
    /// How many columns fit at a readable width. Guidance beside the operator's ceiling.
    ///
    /// <para>
    /// This has to survive the layout it is guiding. It used to be <c>DeskWidth / 420</c>, a
    /// uniform division — but the row is not divided uniformly: the primary takes its share
    /// off the top and the rest split what is left. At 1920 with the default 45% primary that
    /// said four columns fit, and the three beside the primary landed at 345px each. Being
    /// under their own minimum they clamped back up, the row summed past the viewport, and the
    /// desk scrolled with the last window off-screen — while the number that produced the
    /// arrangement was also the default for the ceiling, so it recommended itself.
    /// </para>
    /// </summary>
    public int RecommendedCapacity
        => RowLayout.RecommendedCapacity(DeskWidth, Settings.PrimaryWindowKey is not null, Settings.PrimaryShare);

    /// <summary>Every window open, showing or behind a tab.</summary>
    public int OpenCount => _row.Windows.Count;

    /// <summary>Columns in the row — never more than the limit.</summary>
    public int ColumnCount => _row.Windows.Select(w => w.ColumnId).Distinct().Count();

    /// <summary>Windows waiting behind another in a tab group.</summary>
    public int TabbedCount => OpenCount - ColumnCount;

    /// <summary>
    /// Every column is in use: the next window opens as a tab (<see cref="NextTabTarget"/>), and
    /// a follow-up floats rather than covering a column (<see cref="FollowUpLauncher"/>).
    /// </summary>
    public bool AtCapacity => ColumnCount >= Settings.MaxConcurrentWindows;

    /// <summary>The desk holds <see cref="DeskSettings.MaxOpenWindows"/>: the next open is refused, and says so.</summary>
    public bool AtCeiling => OpenCount >= DeskSettings.MaxOpenWindows;

    /// <summary>
    /// A sentence for the header: a window closed by its own key, with the way back, or an open
    /// the desk refused, with why. Nothing on the desk vanishes or fails to appear unexplained.
    /// </summary>
    public string? Notice { get; private set; }

    /// <summary>
    /// The titles behind <see cref="Notice"/>, so the key that offers to bring them back can name
    /// them without reading the sentence apart. Empty when the notice is a refusal.
    /// </summary>
    public IReadOnlyList<string> NoticeTitles { get; private set; } = [];

    /// <summary>
    /// The workspace last put on the desk with <see cref="ApplyWorkspace"/>, the application's
    /// or one the operator saved. Cleared when the desk is emptied or another is applied; kept
    /// while windows are opened, closed and moved within it, since the arrangement is still the
    /// one it began as. Written into the layout by name and found again on restore.
    /// </summary>
    public Workspace? ActiveWorkspace { get; private set; }

    /// <summary>
    /// Clears the notice only if it is still <paramref name="expected"/> — the very sentence, not
    /// the same words — so whoever was showing an older one cannot clear a newer one by being late.
    /// True when it cleared.
    /// </summary>
    public bool ClearNotice(string expected)
    {
        if (!ReferenceEquals(Notice, expected))
            return false;

        ClearNotice();
        return true;
    }

    public void ClearNotice()
    {
        if (Notice is null)
            return;

        SetNotice(null);
        Notify();
    }

    /// <summary>Sets the notice and the titles behind it; none when not given.</summary>
    private void SetNotice(string? notice, IReadOnlyList<string>? titles = null)
    {
        Notice = notice;
        NoticeTitles = titles ?? [];
    }

    public void UpdateSettings(Action<DeskSettings> mutate)
    {
        var before = Settings.MaxConcurrentWindows;
        mutate(Settings);

        if (Settings.MaxConcurrentWindows != before)
            _ceilingChosen = true;

        // The limit may have dropped below the columns in the row: the extra ones fold into tabs.
        // Nothing closes, so nothing typed is at risk.
        _row.FoldToLimit();
        Relayout();
    }

    public void SetViewport(double width, double height)
    {
        var changed = Math.Abs(width - ViewportWidth) >= 1 || Math.Abs(height - ViewportHeight) >= 1;

        ViewportWidth = width;
        ViewportHeight = height;

        // First real measurement sets the default ceiling from the screen. Shipping a fixed
        // default means the row overflows out of the box on a laptop and every column sits
        // pinned at its minimum, which makes the dividers inert — the layout would be
        // technically correct and useless. The operator's own choice always wins after this.
        if (!_viewportMeasured && width > 0)
        {
            _viewportMeasured = true;

            if (!_ceilingChosen)
            {
                Settings.MaxConcurrentWindows = Math.Max(2, RecommendedCapacity);
                _row.FoldToLimit();
            }

            _fit.Fit(DeskWidth);
            Relayout();
            return;
        }

        if (changed)
        {
            _fit.Fit(DeskWidth);
            Relayout();
        }
    }

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
    /// Lets a hosted panel say it holds input that has not been saved, and what to call it in
    /// the question — "this log entry". Cleared when the input is saved or emptied.
    /// </summary>
    public void SetUnsaved(string id, bool dirty, string? label = null)
    {
        var window = Find(id);
        if (window is null)
            return;

        label = dirty ? label : null;

        if (window.HasUnsavedInput == dirty && window.UnsavedInputLabel == label)
            return;

        window.HasUnsavedInput = dirty;
        window.UnsavedInputLabel = label;

        // Nothing on the desk's chrome draws the flag — the close key reads it when it acts — so
        // this stays with the one window rather than redrawing the desk.
        NotifyWindow(window);
    }

    /// <summary>
    /// Lets a hosted panel report its own state up to its window chrome. Said to that window, the
    /// tab key of its column, and the bar alone (<see cref="WindowChanged"/>): a status line moves
    /// nothing else on the desk. A tab behind another still reports, and its tab says so.
    /// </summary>
    public void SetPulse(string id, PulseState pulse, string? statusText = null)
    {
        var window = Find(id);
        if (window is null)
            return;

        if (window.Pulse == pulse && window.StatusText == statusText)
            return;

        window.Pulse = pulse;
        window.StatusText = statusText;
        NotifyWindow(window);
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

    public WindowInstance? Find(string id) => _row.Find(id);

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

    // ------------------------------------------------------------------ history ----

    /// <summary>
    /// How many steps Back can take. The host mirrors this into the browser's own history, so
    /// the browser's Back button and the mouse's back key walk the desk rather than leaving it.
    /// </summary>
    public int HistoryDepth => _history.Depth;

    /// <summary>Counts steps taken, so the host can tell a new step from a walk back and forth.</summary>
    public long HistoryVersion => _history.Version;

    public bool CanGoBack => _history.CanGoBack;

    public bool CanGoForward => _history.CanGoForward;

    /// <summary>
    /// Puts the desk back the way it was before the last open, close or re-aim. A window closed
    /// comes back to its column, a tab to its group; one opened since goes. Before this there was no way
    /// back at all: the desk has one route, so the browser's Back left the application.
    ///
    /// <para>
    /// Not when the step would close or re-aim a window holding unsaved input: then nothing
    /// moves and <see cref="CloseNeedsConfirmation"/> is raised with it, as the close key does.
    /// Back was the one road left by which a half-typed entry went without a question.
    /// </para>
    /// </summary>
    public bool Back()
    {
        if (_history.NextBack is not { } step || Refuses(step))
            return false;

        Become(_history.StepBack(Moment()));
        return true;
    }

    /// <summary>The road Back came down, under the same rule for unsaved input.</summary>
    public bool Forward()
    {
        if (_history.NextForward is not { } step || Refuses(step))
            return false;

        Become(_history.StepForward(Moment()));
        return true;
    }

    /// <summary>
    /// Walks to a depth the browser named — one step for Back, several for a long press on it.
    /// Judged by where the walk ends rather than step by step: a window the walk passes out of
    /// and back into is the same window when it lands, input and all. When the end would close
    /// or re-aim a window holding unsaved input, nothing moves, <see cref="CloseNeedsConfirmation"/>
    /// is raised, and the answer is false.
    /// </summary>
    public bool TraverseTo(int depth)
    {
        if (_history.At(depth) is { } target && Refuses(target))
            return false;

        while (HistoryDepth > depth && _history.CanGoBack)
            Become(_history.StepBack(Moment()));

        while (HistoryDepth < depth && _history.CanGoForward)
            Become(_history.StepForward(Moment()));

        return true;
    }

    /// <summary>
    /// Whether becoming <paramref name="moment"/> would cost a window its unsaved input, and if
    /// so, says which by raising <see cref="CloseNeedsConfirmation"/>.
    /// </summary>
    private bool Refuses(DeskHistory.DeskMoment moment)
    {
        if (DeskHistory.Displaced(_row.Row(), moment) is not { } window)
            return false;

        Refuse(window);
        return true;
    }

    /// <summary>
    /// Says a step was refused for <paramref name="window"/>'s unsaved input: as a question to
    /// whoever listens (<see cref="CloseNeedsConfirmation"/>), and as the notice, naming the
    /// window. A workspace or a Back that silently did nothing read as a key that was broken; the
    /// notice is how the header, the palette and the browser's Back all say why.
    /// </summary>
    private void Refuse(WindowInstance window)
    {
        SetNotice($"Nothing changed: {window.Title} holds input that has not been saved. Save or discard it, then try again.");
        CloseNeedsConfirmation?.Invoke(window);
        Notify();
    }

    /// <summary>Called before anything that changes what the desk shows. A new step forgets the road ahead.</summary>
    private void Remember()
    {
        _history.Remember(Moment());

        // A notice's Reopen steps Back, and Back now means this step, not the close the notice
        // was about: the sentence goes with it. A close sets its own after remembering.
        SetNotice(null);
    }

    private DeskHistory.DeskMoment Moment() => DeskHistory.Moment(_row.Windows, FocusedId, ActiveWorkspace);

    private void Become(DeskHistory.DeskMoment moment)
    {
        DeskHistory.PutBack(moment, _row.Windows);

        // The limit may have been lowered since the moment was taken.
        _row.FoldToLimit();

        // Focus lands on a window that is drawn: where the moment's focus is outside the column
        // holding the row, the row is given back.
        if (moment.FocusedId is { } held && Find(held) is { } heldWindow)
            _row.GiveRowBackFor(heldWindow);

        FocusedId = moment.FocusedId is { } id && ColumnOf(id) is { } column
            ? column.Showing.Id
            : _row.MostRecent()?.Id;
        SetNotice(null);
        ActiveWorkspace = moment.ActiveWorkspace;
        Relayout();
    }

    // ------------------------------------------------------------- window pages ----

    /// <summary>
    /// Follows a link from inside a window: a customer named on an order, a person on a list.
    ///
    /// <para>
    /// A subject window going to another subject turns to it in place, and keeps the page it
    /// left so its own Back key returns there (<see cref="WindowInstance.FollowsInPlace"/>). Every
    /// such link used to open a window of its own — three clicks into a record was three more
    /// columns, each squeezing the rest, and on a full desk each closed something to make room.
    /// The reader's context was the window they were reading; now it stays there.
    /// </para>
    ///
    /// <para>
    /// From anywhere else — a catalogue window, the search, no window at all — this is
    /// <see cref="Open"/>.
    /// </para>
    /// </summary>
    /// <param name="fromId">The window whose link was followed, or null.</param>
    /// <param name="definition">The window to open or turn to.</param>
    /// <param name="parameters">What the window is about, as its component's parameters.</param>
    /// <param name="titleOverride">A title to show instead of the definition's, or null.</param>
    public WindowInstance Follow(
        string? fromId,
        WindowDefinition definition,
        Dictionary<string, object> parameters,
        string? titleOverride = null)
    {
        if (fromId is null
            || Find(fromId) is not { FollowsInPlace: true } from
            || !WindowInstance.Navigable(definition)
            || (definition.OperatorOnly && !Access.CanOperate))
        {
            return Open(definition, parameters, titleOverride);
        }

        if (from.Definition == definition && !DeskHistory.Retargets(from, parameters, titleOverride))
        {
            Focus(from.Id);
            return from;
        }

        // Another window already shows this subject: go to it rather than turning this one into
        // a second copy of it.
        if (_row.Windows.FirstOrDefault(w => w != from && w.Definition.Key == definition.Key
                                             && (definition.Singleton || DeskHistory.SameSubject(w, parameters))) is { } open)
        {
            Focus(open.Id);
            return open;
        }

        Remember();

        DeskHistory.Leave(from);
        Show(from, new WindowPage(definition, new Dictionary<string, object>(parameters), titleOverride));

        return from;
    }

    /// <summary>Turns a window back to the page it followed a link from.</summary>
    public bool PageBack(string id)
    {
        if (Find(id) is not { CanPageBack: true } window)
            return false;

        Remember();
        Show(window, DeskHistory.PageBack(window));
        return true;
    }

    public bool PageForward(string id)
    {
        if (Find(id) is not { CanPageForward: true } window)
            return false;

        Remember();
        Show(window, DeskHistory.PageForward(window));
        return true;
    }

    /// <summary>Puts a page in a window and shows it: in front of its column, the column open, the operator in it.</summary>
    private void Show(WindowInstance window, WindowPage page)
    {
        DeskHistory.Turn(window, page);
        _row.Reveal(window);

        // A customer's view is narrower than an order's: the column's minimum is the new view's.
        Relayout();
    }

    /// <summary>
    /// Points a window's parameters at a new subject. A key the new subject does not name is set
    /// to null rather than removed: a hosted component keeps the last value it was given for a
    /// parameter that stops being passed, so removing it would leave the old subject showing.
    /// </summary>
    public static void Aim(Dictionary<string, object> target, IReadOnlyDictionary<string, object> with)
    {
        foreach (var key in target.Keys.Where(k => !with.ContainsKey(k)).ToList())
            target[key] = null!;

        foreach (var (key, value) in with)
            target[key] = value;
    }

    /// <summary>
    /// Says the desk has changed. Whatever this changed inside a window has already moved that
    /// window's version, so the desk's render redraws those windows and passes the rest by.
    /// </summary>
    private void Notify() => Changed?.Invoke();

    /// <summary>Says one window has changed and nothing else has. See <see cref="WindowChanged"/>.</summary>
    /// <remarks>The column's tab key, drawn by the window its column shows, listens for every tab in it.</remarks>
    private void NotifyWindow(WindowInstance window)
    {
        window.Touch();
        WindowChanged?.Invoke(window.Id);
    }
}
