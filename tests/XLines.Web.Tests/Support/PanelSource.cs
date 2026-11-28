namespace XLines.Web.Tests.Support;

/// <summary>
/// A window's whole source, for the tests that read it rather than render it: its markup, then its
/// code-behind partials (<c>&lt;Panel&gt;*.cs</c>), then the child components split out of it. A
/// rule about a window reads them all, or a member or an element moved out of the .razor would
/// pass by not being looked at (see JournalPanelTests.Source, HoverOnlyTitleTests.Source).
/// </summary>
public static class PanelSource
{
    /// <summary>The child components each split panel draws from beside it.</summary>
    private static readonly Dictionary<string, string[]> Children = new(StringComparer.Ordinal)
    {
        ["OpsPanel"] = ["OpsScanningCost.razor"],
        ["GamePanel"] = ["GameBetRows.razor"],
    };

    public static string Of(string panel)
    {
        var panels = Path.Combine(Repo.Root(), "src", "XLines.Web", "Components", "Panels");

        var files = new List<string> { Path.Combine(panels, panel + ".razor") };
        files.AddRange(Directory.EnumerateFiles(panels, panel + ".*.cs").Order(StringComparer.Ordinal));
        files.AddRange((Children.GetValueOrDefault(panel) ?? []).Select(child => Path.Combine(panels, child)));

        return string.Join("\n", files.Select(File.ReadAllText));
    }
}
