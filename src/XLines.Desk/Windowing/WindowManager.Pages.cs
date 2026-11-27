namespace XLines.Desk.Windowing;

public partial class WindowManager
{
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
}
