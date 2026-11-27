namespace XLines.Desk.Windowing;

public partial class WindowManager
{
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
}
