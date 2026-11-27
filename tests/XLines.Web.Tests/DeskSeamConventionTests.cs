using System.Text.RegularExpressions;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The application names the Desk's grid columns and icons, never MudBlazor's (architecture
/// review §6, ADR 0034 steps 5 and 7). <c>DeskColumn</c>, <c>DeskTemplateColumn</c> and
/// <c>DeskIcons</c> forward to MudBlazor today, so replacing the grid or the glyphs later changes
/// the Desk and not every window; a MudBlazor column or Material icon written here again would be
/// one more call site that exit has to rewrite.
/// </summary>
public class DeskSeamConventionTests
{
    private static readonly string Web = Path.Combine(Repo.Root(), "src", "XLines.Web");

    private static IEnumerable<string> Sources()
        => Directory.EnumerateFiles(Web, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"));

    private static List<string> Offenders(string pattern)
    {
        var offenders = new List<string>();

        foreach (var file in Sources())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, pattern))
                offenders.Add($"{Path.GetRelativePath(Web, file)}:{text[..match.Index].Count(c => c == '\n') + 1}  {match.Value}");
        }

        return offenders;
    }

    [Fact]
    public void The_app_declares_desk_columns_not_mudblazor_s()
    {
        var offenders = Offenders(@"<(PropertyColumn|TemplateColumn)\b");

        Assert.True(offenders.Count == 0,
            "MudBlazor columns in the app; use DeskColumn / DeskTemplateColumn:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_app_names_desk_icons_not_material_ones()
    {
        var offenders = Offenders(@"Icons\.Material\b");

        Assert.True(offenders.Count == 0,
            "Material icons named in the app; use DeskIcons:\n" + string.Join("\n", offenders));
    }
}
