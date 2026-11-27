using System.Text.Json;

namespace XLines.Desk.Windowing;

/// <summary>
/// The desk written down and read back: the workspaces the operator saved, a
/// <see cref="DeskLayout"/> captured from the row, and the settings, windows and workspaces a
/// layout puts back. Builds windows and lists where they go; <see cref="WindowManager"/> puts them
/// on the desk (<see cref="DeskRow.Arrange"/>) and says when it is done.
/// </summary>
internal sealed class LayoutStore
{
    private readonly IWindowCatalog _catalog;
    private readonly List<Workspace> _userWorkspaces = [];

    /// <param name="catalog">The catalogue as the person at this desk may see it.</param>
    public LayoutStore(IWindowCatalog catalog) => _catalog = catalog;

    /// <summary>Workspaces the operator saved on this machine.</summary>
    public IReadOnlyList<Workspace> UserWorkspaces => _userWorkspaces;

    /// <summary>
    /// Saves the row as a named workspace, replacing one of the same name. Null, and nothing
    /// saved, without a name or without a window that opens on nothing.
    /// </summary>
    public Workspace? Save(string name, IReadOnlyList<WindowColumn> row)
    {
        name = name.Trim();

        var columns = row
            .Select(c => c.Tabs.Where(w => w.Parameters.Count == 0).ToList())
            .Where(tabs => tabs.Count > 0)
            .ToList();

        var keys = columns.SelectMany(tabs => tabs.Select(w => w.Definition.Key)).ToList();
        var indices = columns.SelectMany((tabs, i) => tabs.Select(_ => i)).ToList();

        if (name.Length == 0 || keys.Count == 0)
            return null;

        var workspace = new Workspace(name, Describe(keys.Count, columns.Count), keys,
            columns.Count < keys.Count ? indices : null);

        _userWorkspaces.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        _userWorkspaces.Add(workspace);
        return workspace;
    }

    /// <summary>True when a workspace of that name was there to delete.</summary>
    public bool Delete(string name) => _userWorkspaces.RemoveAll(w => w.Name == name) > 0;

    /// <summary>"3 windows, saved here", or "5 windows in 3 columns, saved here" when some are tabs.</summary>
    private static string Describe(int windows, int columns)
    {
        var count = $"{windows} {(windows == 1 ? "window" : "windows")}";

        return columns < windows
            ? $"{count} in {columns} {(columns == 1 ? "column" : "columns")}, saved here"
            : $"{count}, saved here";
    }

    /// <summary>The desk as it can be written down. See <see cref="DeskLayout"/>.</summary>
    public DeskLayout Capture(
        IReadOnlyList<WindowColumn> columns,
        string? focusedId,
        DeskSettings settings,
        bool ceilingChosen,
        int windowCount,
        Workspace? activeWorkspace)
    {
        var row = columns
            .SelectMany(c => c.Tabs.Select((w, rank) => (Window: w, Column: c.Position, Rank: rank)))
            .ToList();
        var focused = row.FindIndex(p => p.Window.Id == focusedId);

        return new DeskLayout
        {
            MaxConcurrentWindows = settings.MaxConcurrentWindows,
            CeilingChosen = ceilingChosen,
            PrimaryWindowKey = settings.PrimaryWindowKey,
            PrimaryShare = settings.PrimaryShare,
            ResolutionMode = settings.ResolutionMode,
            CustomWidth = settings.CustomWidth,
            CustomHeight = settings.CustomHeight,
            UiScale = settings.UiScale,
            TextScale = settings.TextScale,
            Windows = row.Select(p => new DeskLayoutWindow(
                p.Window.Definition.Key,
                p.Window.TitleOverride,
                p.Window.Weight,
                p.Window.Minimised,
                p.Window.Parameters.Count == 0
                    ? null
                    : p.Window.Parameters.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value)),
                p.Column,
                p.Rank))
                .ToList(),
            Focused = focused >= 0 ? focused : null,
            Workspaces = _userWorkspaces.Select(w => new DeskLayoutWorkspace(w.Name, w.WindowKeys, w.Columns)).ToList(),
            ActiveWorkspace = windowCount > 0 ? activeWorkspace?.Name : null,
            ActiveWorkspaceSaved = windowCount > 0 && activeWorkspace is { } active && _userWorkspaces.Any(w => ReferenceEquals(w, active))
        };
    }

    /// <summary>
    /// The layout's settings into <paramref name="settings"/>, each clamped to what the desk allows;
    /// a primary window this catalogue no longer has becomes the catalogue's default.
    /// </summary>
    public void RestoreSettings(DeskLayout layout, DeskSettings settings)
    {
        settings.MaxConcurrentWindows = Math.Clamp(layout.MaxConcurrentWindows, 1, 12);
        settings.PrimaryWindowKey = layout.PrimaryWindowKey is { } key && _catalog.Find(key) is not null
            ? key
            : layout.PrimaryWindowKey is null ? null : _catalog.DefaultPrimary;
        settings.PrimaryShare = Math.Clamp(layout.PrimaryShare, 0.2, 0.8);
        settings.ResolutionMode = layout.ResolutionMode;
        settings.CustomWidth = layout.CustomWidth > 0 ? layout.CustomWidth : settings.CustomWidth;
        settings.CustomHeight = layout.CustomHeight > 0 ? layout.CustomHeight : settings.CustomHeight;
        settings.UiScale = DeskSettings.ClampScale(layout.UiScale, DeskSettings.MinUiScale, DeskSettings.MaxUiScale);
        settings.TextScale = DeskSettings.ClampScale(layout.TextScale, DeskSettings.MinTextScale, DeskSettings.MaxTextScale);
    }

    /// <summary>
    /// The layout's windows, built and placed — up to the desk's ceiling, without the ones this
    /// catalogue cannot open — and the one that had focus, if it is among them. A layout from
    /// before tab groups gives every window a column of its own, in the order written.
    /// </summary>
    public (List<(WindowInstance Window, int Column, int Rank)> Placed, WindowInstance? Focused) Windows(
        DeskLayout layout, Func<WindowDefinition, string> nextId)
    {
        WindowInstance? focused = null;
        var placed = new List<(WindowInstance Window, int Column, int Rank)>();

        foreach (var (saved, index) in layout.Windows.Select((w, i) => (w, i)))
        {
            if (placed.Count >= DeskSettings.MaxOpenWindows)
                break;

            if (_catalog.Find(saved.Key) is not { } definition)
                continue;

            var instance = new WindowInstance
            {
                Id = nextId(definition),
                Definition = definition,
                TitleOverride = saved.TitleOverride,
                Weight = saved.Weight > 0 ? saved.Weight : definition.DefaultWeight,
                Minimised = saved.Minimised,
                LastFocusedAt = DateTimeOffset.UtcNow
            };

            foreach (var (name, value) in saved.Parameters ?? new Dictionary<string, JsonElement>())
            {
                if (ToParameter(value) is { } parameter)
                    instance.Parameters[name] = parameter;
            }

            // Before tabs, each window was its own column, in the order written.
            var column = saved.Column ?? index;
            placed.Add((instance, column, saved.Rank ?? placed.Count(p => p.Column == column)));

            if (index == layout.Focused)
                focused = instance;
        }

        return (placed, focused);
    }

    /// <summary>
    /// A workspace's windows, built and placed: in the columns it names when it names one for
    /// every key, a column each otherwise; up to the desk's ceiling, without keys this catalogue
    /// cannot open.
    /// </summary>
    public List<(WindowInstance Window, int Column, int Rank)> Windows(
        Workspace workspace, Func<WindowDefinition, string> nextId)
    {
        var columns = workspace.Columns is { } named && named.Count == workspace.WindowKeys.Count ? named : null;
        var placed = new List<(WindowInstance Window, int Column, int Rank)>();

        for (var i = 0; i < workspace.WindowKeys.Count && placed.Count < DeskSettings.MaxOpenWindows; i++)
        {
            var definition = _catalog.Find(workspace.WindowKeys[i]);
            if (definition is null)
                continue;

            var column = columns?[i] ?? i;
            var instance = new WindowInstance
            {
                Id = nextId(definition),
                Sequence = column,
                Definition = definition,
                Weight = definition.DefaultWeight,
                LastFocusedAt = DateTimeOffset.UtcNow
            };

            placed.Add((instance, column, placed.Count(p => p.Column == column)));
        }

        return placed;
    }

    /// <summary>
    /// Replaces the saved workspaces with the layout's, each without the windows this desk cannot
    /// open, and none that is left with nothing.
    /// </summary>
    public void RestoreWorkspaces(DeskLayout layout)
    {
        _userWorkspaces.Clear();
        _userWorkspaces.AddRange(layout.Workspaces
            .Where(w => w.WindowKeys.Any(k => _catalog.Find(k) is not null))
            .Select(Readable));
    }

    /// <summary>
    /// The workspace the layout was arranged from, found again by name, in the kind it was written
    /// as first (a saved one, or the application's): a layout from before the field reads null and
    /// restores no workspace, and one naming a workspace this desk no longer offers — or one a
    /// visitor may not open — restores none rather than a stale label.
    /// </summary>
    public Workspace? ActiveFrom(DeskLayout layout)
    {
        IEnumerable<Workspace> lookIn = layout.ActiveWorkspaceSaved
            ? _userWorkspaces.Concat(_catalog.Workspaces)
            : _catalog.Workspaces.Concat(_userWorkspaces);

        return layout.ActiveWorkspace is { } activeName
            ? lookIn.FirstOrDefault(w => w.Name == activeName)
            : null;
    }

    /// <summary>A saved workspace with the windows this desk cannot open taken out, and its columns with them.</summary>
    private Workspace Readable(DeskLayoutWorkspace saved)
    {
        var keep = saved.WindowKeys.Select((k, i) => (Key: k, Index: i)).Where(k => _catalog.Find(k.Key) is not null).ToList();
        var columns = saved.Columns is { } named && named.Count == saved.WindowKeys.Count
            ? keep.Select(k => named[k.Index]).ToList()
            : null;

        var count = columns?.Distinct().Count() ?? keep.Count;

        return new Workspace(saved.Name, Describe(keep.Count, count), keep.Select(k => k.Key).ToList(), columns);
    }

    /// <summary>A stored parameter back to the type a component expects: ids are ints.</summary>
    private static object? ToParameter(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var i) => i,
        JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
