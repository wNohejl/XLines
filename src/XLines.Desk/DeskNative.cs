namespace XLines.Desk;

/// <summary>
/// Which primitives draw without MudBlazor (ADR 0034). Each is a step of that ADR, and each is off
/// until its host turns it on, so a desk that says nothing is the desk as it was. A step's flag is
/// deleted with its MudBlazor path once the native one is chosen.
///
/// <para>
/// A host passes one to <c>AddDesk</c>. It is read when a component is first drawn: a desk is one
/// or the other for its whole life, not per press.
/// </para>
/// </summary>
public sealed record DeskNative
{
    /// <summary>Step 1: <c>DeskButton</c> is a <c>&lt;button&gt;</c>, not a <c>MudButton</c>.</summary>
    public bool Button { get; init; }

    /// <summary>Step 2: <c>DeskTooltip</c> is a popover element placed by CSS, not a <c>MudTooltip</c>.</summary>
    public bool Tooltip { get; init; }

    /// <summary>Every primitive as it was: on MudBlazor.</summary>
    public static DeskNative Off { get; } = new();

    /// <summary>Every step that has a native path, on.</summary>
    public static DeskNative All { get; } = new() { Button = true, Tooltip = true };
}
