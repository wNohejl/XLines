namespace XLines.Desk.Windowing;

/// <summary>
/// The desk's road back and forward, and each window's own pages. Holds the steps; takes the
/// moment to remember from <see cref="WindowManager"/>, and hands back the one to become, which
/// <see cref="PutBack"/> writes into the windows. Raises nothing: whether a step is refused, and
/// what is said when it is, stays with the manager.
/// </summary>
internal sealed class DeskHistory
{
    /// <summary>
    /// How far back the desk remembers. Each step holds references to windows that already
    /// exist plus a copy of their parameters, so this is a bound on surprise, not on memory.
    /// </summary>
    private const int HistoryLimit = 50;

    /// <summary>How many pages one window remembers behind it.</summary>
    private const int PageLimit = 30;

    private readonly List<DeskMoment> _back = [];
    private readonly List<DeskMoment> _forward = [];

    /// <summary>
    /// Steps dropped off the far end. Counted so <see cref="Depth"/> keeps rising past the
    /// limit — the browser's entries are numbered by it and would stop matching otherwise.
    /// </summary>
    private int _forgotten;

    /// <summary>
    /// The desk as it stood before one navigation: which windows were in the row, what each
    /// was showing and the pages it came through, and which had focus. The windows themselves
    /// are kept, not described, so going back puts the same window — same id, same slot, same
    /// width — where it was.
    /// </summary>
    internal sealed record DeskMoment(IReadOnlyList<WindowMoment> Row, string? FocusedId, Workspace? ActiveWorkspace = null);

    internal sealed record WindowMoment(
        WindowInstance Window,
        WindowDefinition Definition,
        Dictionary<string, object> Parameters,
        string? Title,
        List<WindowPage> BackPages,
        List<WindowPage> ForwardPages,
        string ColumnId,
        long Recency,
        long Sequence,
        bool Minimised,
        bool Maximised,
        double Weight);

    /// <summary>How many steps Back can take, counting those dropped off the far end.</summary>
    public int Depth => _forgotten + _back.Count;

    /// <summary>Counts steps taken, so the host can tell a new step from a walk back and forth.</summary>
    public long Version { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>The step Back would take, or null.</summary>
    public DeskMoment? NextBack => _back.Count > 0 ? _back[^1] : null;

    /// <summary>The step Forward would take, or null.</summary>
    public DeskMoment? NextForward => _forward.Count > 0 ? _forward[^1] : null;

    /// <summary>Forgets every step, both ways.</summary>
    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
        _forgotten = 0;
    }

    /// <summary>Remembers the desk as it stands before a change. A new step forgets the road ahead.</summary>
    public void Remember(DeskMoment now)
    {
        _back.Add(now);

        if (_back.Count > HistoryLimit)
        {
            _back.RemoveAt(0);
            _forgotten++;
        }

        _forward.Clear();
        Version++;
    }

    /// <summary>Puts <paramref name="now"/> on the road ahead and hands back the step behind.</summary>
    public DeskMoment StepBack(DeskMoment now)
    {
        _forward.Add(now);
        return Pop(_back);
    }

    /// <summary>Puts <paramref name="now"/> on the road behind and hands back the step ahead.</summary>
    public DeskMoment StepForward(DeskMoment now)
    {
        _back.Add(now);
        return Pop(_forward);
    }

    /// <summary>The moment a walk to <paramref name="depth"/> would end on, or null where it would not move.</summary>
    public DeskMoment? At(int depth)
        => depth < Depth && _back.Count > 0
            ? _back[Math.Max(0, _back.Count - (Depth - depth))]
            : depth > Depth && _forward.Count > 0
                ? _forward[Math.Max(0, _forward.Count - (depth - Depth))]
                : null;

    /// <summary>The desk as it stands, as a step it can come back to.</summary>
    public static DeskMoment Moment(IEnumerable<WindowInstance> windows, string? focusedId, Workspace? activeWorkspace) => new(
        windows.Select(w => new WindowMoment(
            w,
            w.Definition,
            new Dictionary<string, object>(w.Parameters),
            w.TitleOverride,
            [.. w.BackPages],
            [.. w.ForwardPages],
            w.ColumnId,
            w.Recency,
            w.Sequence,
            w.Minimised,
            w.Maximised,
            w.Weight)).ToList(),
        focusedId,
        activeWorkspace);

    /// <summary>
    /// The first window of <paramref name="row"/> holding unsaved input that <paramref name="moment"/>
    /// would close — it is not in that moment — or replace: it is there showing another view, or the
    /// same view on another subject. A window kept as it is passes, whatever else the step does.
    /// </summary>
    public static WindowInstance? Displaced(IEnumerable<WindowInstance> row, DeskMoment moment)
    {
        foreach (var window in row.Where(w => w.HasUnsavedInput))
        {
            var saved = moment.Row.FirstOrDefault(m => ReferenceEquals(m.Window, window));

            if (saved is null
                || saved.Definition != window.Definition
                || !SameSubject(window.Parameters, saved.Parameters))
                return window;
        }

        return null;
    }

    /// <summary>
    /// Writes <paramref name="moment"/> back into its windows and makes them the desk's, in the
    /// order it holds them. Focus, the notice and the layout are the caller's.
    /// </summary>
    public static void PutBack(DeskMoment moment, List<WindowInstance> windows)
    {
        windows.Clear();

        foreach (var saved in moment.Row)
        {
            var window = saved.Window;

            // A window that followed a link since is showing another view, and the parameters of
            // one view are not the parameters of another — a Customer handed an OrderId refuses it.
            // So a different view is put back whole; the same view is re-aimed, as a singleton's
            // subject always was.
            if (window.Definition != saved.Definition)
            {
                window.Definition = saved.Definition;
                window.Parameters.Clear();

                foreach (var (key, value) in saved.Parameters)
                    window.Parameters[key] = value;

                window.PageVersion++;
            }
            else
            {
                WindowManager.Aim(window.Parameters, saved.Parameters);
            }

            window.TitleOverride = saved.Title;
            window.BackPages = [.. saved.BackPages];
            window.ForwardPages = [.. saved.ForwardPages];

            // Its column and its place in it come back too: a tab closed from a group returns to
            // that group, behind the tab it was behind, at the group's width, collapsed or holding
            // the row as the group was. Every tab in a column was saved agreeing, so they agree
            // again. A collapsed window keeps its panel (AppWindow hides the body), so coming
            // back collapsed costs it nothing it holds.
            window.ColumnId = saved.ColumnId;
            window.Recency = saved.Recency;
            window.Sequence = saved.Sequence;
            window.Minimised = saved.Minimised;
            window.Maximised = saved.Maximised;
            window.Weight = saved.Weight;
            window.Touch();
            windows.Add(window);
        }
    }

    // ------------------------------------------------------------- window pages ----

    /// <summary>Keeps the page <paramref name="window"/> is leaving behind it, and forgets the pages ahead.</summary>
    public static void Leave(WindowInstance window)
    {
        window.BackPages.Add(PageOf(window));

        if (window.BackPages.Count > PageLimit)
            window.BackPages.RemoveAt(0);

        window.ForwardPages.Clear();
    }

    /// <summary>Keeps the page a window is on ahead of it and hands back the one behind.</summary>
    public static WindowPage PageBack(WindowInstance window)
    {
        window.ForwardPages.Add(PageOf(window));
        return Pop(window.BackPages);
    }

    /// <summary>Keeps the page a window is on behind it and hands back the one ahead.</summary>
    public static WindowPage PageForward(WindowInstance window)
    {
        window.BackPages.Add(PageOf(window));
        return Pop(window.ForwardPages);
    }

    /// <summary>What a window is showing, as a page it can come back to. Cleared keys are left out.</summary>
    private static WindowPage PageOf(WindowInstance window) => new(
        window.Definition,
        window.Parameters.Where(p => p.Value is not null).ToDictionary(),
        window.TitleOverride);

    /// <summary>
    /// Puts a page in a window. The parameters are replaced rather than re-aimed, because the
    /// view may have changed, and the page version moves so the host builds the view afresh.
    /// </summary>
    public static void Turn(WindowInstance window, WindowPage page)
    {
        window.Definition = page.Definition;
        window.Parameters.Clear();

        foreach (var (key, value) in page.Parameters)
            window.Parameters[key] = value;

        window.TitleOverride = page.TitleOverride;
        window.PageVersion++;

        // The next page is a new component; whatever the last one held went with it, and a flag
        // left behind would guard nothing while making every close of it ask a question.
        window.HasUnsavedInput = false;
        window.UnsavedInputLabel = null;
    }

    // ----------------------------------------------------------------- subjects ----

    /// <summary>Whether two parameter sets name the same subject. A key set to null is a key not named.</summary>
    public static bool SameSubject(Dictionary<string, object> current, Dictionary<string, object> saved)
    {
        var now = current.Where(p => p.Value is not null).ToDictionary();
        var then = saved.Where(p => p.Value is not null).ToDictionary();

        return now.Count == then.Count
            && then.All(p => now.TryGetValue(p.Key, out var value) && Equals(value, p.Value));
    }

    /// <summary>Whether a window is on exactly the subject <paramref name="parameters"/> name: the same keys, the same values.</summary>
    public static bool SameSubject(WindowInstance window, Dictionary<string, object>? parameters)
    {
        var current = window.Parameters.Where(p => p.Value is not null).ToDictionary();
        var asked = (parameters ?? []).Where(p => p.Value is not null).ToDictionary();

        return current.Count == asked.Count
            && asked.All(p => current.TryGetValue(p.Key, out var value) && Equals(value, p.Value));
    }

    /// <summary>Whether opening <paramref name="window"/> with these arguments would change what it shows.</summary>
    public static bool Retargets(WindowInstance window, Dictionary<string, object>? parameters, string? title)
    {
        if (title is not null && title != window.TitleOverride)
            return true;

        if (parameters is null)
            return false;

        var current = window.Parameters.Where(p => p.Value is not null).ToDictionary();
        return current.Count != parameters.Count
            || parameters.Any(p => !current.TryGetValue(p.Key, out var value) || !Equals(value, p.Value));
    }

    private static T Pop<T>(List<T> stack)
    {
        var last = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return last;
    }
}
