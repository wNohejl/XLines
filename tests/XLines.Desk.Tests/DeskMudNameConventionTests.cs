using System.Text.RegularExpressions;
using XLines.Desk.Tests.Support;

namespace XLines.Desk.Tests;

/// <summary>
/// The Desk names its own icons and columns (ADR 0034): a glyph is a <see cref="DeskIcons"/>
/// constant and a grid column a <c>DeskColumn</c> or <c>DeskTemplateColumn</c>, so leaving
/// MudBlazor changes DeskIcons.cs and Primitives/DeskGridColumn.cs, not every window. Read from
/// the source under src/XLines.Desk.
/// </summary>
public class DeskMudNameConventionTests
{
    /// <summary>
    /// The Primitives files that still name a Material glyph directly, with how many times, as of
    /// 2026-11-27. The list can only shrink: a count may fall (lower it here), never rise, and a
    /// file not named here may not start.
    /// </summary>
    private static readonly Dictionary<string, int> StillOnMaterial = new()
    {
        ["Primitives/DeskDialog.razor"] = 2,
        ["Primitives/DeskDisclosure.razor"] = 1,
        ["Primitives/DeskLink.razor"] = 1,
        ["Primitives/DeskMenuItem.razor"] = 2,
        ["Primitives/DeskPopUp.razor"] = 1,
        ["Primitives/DeskPullDown.razor"] = 1,
        ["Primitives/DeskSearch.razor"] = 2,
        ["Primitives/DeskSheet.razor"] = 1,
        ["Primitives/DeskStep.razor"] = 2,
        ["Primitives/DeskStepper.razor"] = 2,
        ["Primitives/DeskToastStack.razor"] = 5,
        ["Primitives/PanelLoadFailure.razor"] = 1,
        ["Primitives/PanelToolbar.razor"] = 2,
        ["Primitives/SnippetFailure.razor"] = 1,
        ["Primitives/SnippetShell.razor"] = 2,
    };

    private static string DeskRoot => Path.Combine(Repo.Root(), "src", "XLines.Desk");

    /// <summary>Every .razor and .cs file under src/XLines.Desk, by its path from there with '/'.</summary>
    private static IEnumerable<(string Path, string Text)> Sources() =>
        Directory.EnumerateFiles(DeskRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => (Path: Path.GetRelativePath(DeskRoot, f).Replace('\\', '/'), File: f))
            .Where(f => !f.Path.StartsWith("bin/", StringComparison.Ordinal) && !f.Path.StartsWith("obj/", StringComparison.Ordinal))
            .Select(f => (f.Path, File.ReadAllText(f.File)));

    [Fact]
    public void Material_glyphs_are_named_only_in_DeskIcons_and_the_shrinking_list()
    {
        var found = Sources()
            .Where(s => s.Path != "DeskIcons.cs")
            .Select(s => (s.Path, Count: Regex.Matches(s.Text, @"\bIcons\.Material\b").Count))
            .Where(s => s.Count > 0)
            .ToList();

        var offences = found
            .Where(s => !StillOnMaterial.TryGetValue(s.Path, out var allowed) || s.Count > allowed)
            .Select(s => $"{s.Path}: {s.Count} (allowed {StillOnMaterial.GetValueOrDefault(s.Path)})")
            .ToList();

        Assert.True(offences.Count == 0,
            "Name the glyph by its DeskIcons constant, not Icons.Material:\n" + string.Join("\n", offences));

        // The list names only files that exist: a primitive renamed or removed leaves no entry behind.
        foreach (var path in StillOnMaterial.Keys)
            Assert.True(File.Exists(Path.Combine(DeskRoot, path)), $"{path} is on the list but no longer exists.");
    }

    [Fact]
    public void Mud_grid_columns_are_declared_only_by_the_Desk_grid()
    {
        var offences = Sources()
            .Where(s => !Regex.IsMatch(s.Path, @"^Primitives/DeskGrid[^/]*$"))
            .Where(s => Regex.IsMatch(s.Text, @"<(PropertyColumn|TemplateColumn)\b"))
            .Select(s => s.Path)
            .ToList();

        Assert.True(offences.Count == 0,
            "Declare a DeskColumn or DeskTemplateColumn, not MudDataGrid's column:\n" + string.Join("\n", offences));
    }
}
