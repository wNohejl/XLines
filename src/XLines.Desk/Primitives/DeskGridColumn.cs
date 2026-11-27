using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;

namespace XLines.Desk.Primitives;

/// <summary>
/// What a column's cell template is handed: the row it draws. The Desk's own type, so a cell
/// template written against a <see cref="DeskGrid{T}"/> names nothing of the grid underneath it
/// (<c>@context.Item</c> reads the same as it did against MudDataGrid's <c>CellContext</c>).
/// </summary>
public readonly record struct DeskCell<T>(T Item);

/// <summary>
/// A column of a <see cref="DeskGrid{T}"/>. The grid underneath is MudDataGrid today (ADR 0034
/// step 7 replaces it), so a column forwards to MudDataGrid's own column, and an application
/// declares Desk columns only: leaving MudBlazor then changes this file, not every table.
///
/// <para>
/// The forwarding is exact. The Mud column receives the parameters the call site wrote and no
/// others, by the same names, so its defaults stay its own (a template column does not sort
/// unless it says so) and a parameter a call site stops supplying behaves as it did. Attributes
/// the Desk does not declare are passed on as written: <c>Align="Align.Right"</c> lands in the
/// Mud column's <c>UserAttributes</c>, where <see cref="DeskGrid{T}"/> reads it. The Mud column
/// is the forwarding component's child, so it sits inside the grid's <c>Columns</c> and finds
/// the grid through its cascade, registering in the order the columns were written.
/// </para>
/// </summary>
public abstract class DeskGridColumn<T> : ComponentBase
{
    /// <summary>The heading's text.</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>Whether the heading sorts its column. Unset, the grid decides (a template column: no).</summary>
    [Parameter] public bool? Sortable { get; set; }

    /// <summary>The order the column is sorted in when the grid first draws.</summary>
    [Parameter] public SortDirection InitialDirection { get; set; }

    /// <summary>A class on the heading cell.</summary>
    [Parameter] public string? HeaderClass { get; set; }

    /// <summary>A class on every body cell.</summary>
    [Parameter] public string? CellClass { get; set; }

    /// <summary>What a body cell draws for its row.</summary>
    [Parameter] public RenderFragment<DeskCell<T>>? CellTemplate { get; set; }

    /// <summary>Everything else the call site wrote, passed on as written — <c>Align</c> above all.</summary>
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    private IReadOnlyDictionary<string, object?> _supplied = new Dictionary<string, object?>();

    /// <summary>The MudDataGrid column this one stands for.</summary>
    private protected abstract Type MudColumn { get; }

    /// <summary>A supplied parameter as the Mud column takes it; most pass through unchanged.</summary>
    private protected virtual object? Forward(string name, object? value) => name switch
    {
        nameof(CellTemplate) when value is RenderFragment<DeskCell<T>> cell =>
            (RenderFragment<CellContext<T>>)(context => cell(new DeskCell<T>(context.Item))),
        _ => value
    };

    public override Task SetParametersAsync(ParameterView parameters)
    {
        // Kept as supplied, so the Mud column receives exactly the call site's set — not every
        // Desk parameter with its default, which would overwrite state the Mud column holds for
        // parameters nobody passed.
        _supplied = parameters.ToDictionary();
        return base.SetParametersAsync(parameters);
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, MudColumn);

        foreach (var (name, value) in _supplied)
            builder.AddComponentParameter(1, name, Forward(name, value));

        builder.CloseComponent();
    }
}

/// <summary>
/// A column that shows one property of the row, and sorts by it: MudDataGrid's
/// <c>PropertyColumn</c>, behind the Desk's name. See <see cref="DeskGridColumn{T}"/>.
/// </summary>
public sealed class DeskColumn<T, TProperty> : DeskGridColumn<T>
{
    /// <summary>The property shown, and sorted by.</summary>
    [Parameter, EditorRequired] public Expression<Func<T, TProperty>> Property { get; set; } = default!;

    /// <summary>A .NET format string for the value, when no <see cref="DeskGridColumn{T}.CellTemplate"/> draws it.</summary>
    [Parameter] public string? Format { get; set; }

    /// <summary>Leaves the column out of the table while keeping its place.</summary>
    [Parameter] public bool Hidden { get; set; }

    private protected override Type MudColumn => typeof(PropertyColumn<T, TProperty>);
}

/// <summary>
/// A column whose cells the caller draws: MudDataGrid's <c>TemplateColumn</c>, behind the Desk's
/// name. It sorts only with <c>Sortable="true"</c>, by <see cref="SortBy"/>. See
/// <see cref="DeskGridColumn{T}"/>.
/// </summary>
public sealed class DeskTemplateColumn<T> : DeskGridColumn<T>
{
    /// <summary>The key the column sorts by.</summary>
    [Parameter] public Func<T, object>? SortBy { get; set; }

    /// <summary>What the heading draws in place of <see cref="DeskGridColumn{T}.Title"/>.</summary>
    [Parameter] public RenderFragment? HeaderTemplate { get; set; }

    private protected override Type MudColumn => typeof(TemplateColumn<T>);

    private protected override object? Forward(string name, object? value) => name switch
    {
        nameof(HeaderTemplate) when value is RenderFragment header =>
            (RenderFragment<HeaderContext<T>>)(_ => header),
        _ => base.Forward(name, value)
    };
}
