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
public partial class WindowManager
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

    public WindowInstance? Find(string id) => _row.Find(id);

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
