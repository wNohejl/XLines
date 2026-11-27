using System.Xml.Linq;

namespace XLines.Tests;

/// <summary>
/// The application's projects stand in layers: Core at the bottom, the hosts at the top, and
/// each one reaching only the layers beneath it. The references are read off the csproj files,
/// because a project that compiled against one more layer would build just the same.
/// </summary>
public class ProjectShapeTests
{
    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XLines.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("XLines.slnx not found above the test output.");
    }

    private static XDocument Project(string name)
        => XDocument.Load(Path.Combine(Root(), "src", name, name + ".csproj"));

    private static List<string> References(string name) => Project(name)
        .Descendants("ProjectReference")
        .Select(r => Path.GetFileNameWithoutExtension(((string?)r.Attribute("Include") ?? string.Empty).Replace('\\', '/')))
        .Order(StringComparer.Ordinal)
        .ToList();

    [Theory]
    [InlineData("XLines.Core")]
    [InlineData("XLines.Data", "XLines.Core")]
    [InlineData("XLines.Reliability", "XLines.Core", "XLines.Data")]
    [InlineData("XLines.Ingestion", "XLines.Core", "XLines.Data", "XLines.Reliability")]
    [InlineData("XLines.Observability", "XLines.Core", "XLines.Data", "XLines.Reliability")]
    [InlineData("XLines.Worker", "XLines.Core", "XLines.Data", "XLines.Ingestion", "XLines.Observability", "XLines.Reliability")]
    [InlineData("XLines.Web", "XLines.Core", "XLines.Data", "XLines.Desk", "XLines.Desk.Hosting", "XLines.Ingestion", "XLines.Observability", "XLines.Reliability")]
    public void A_project_references_only_the_layers_beneath_it(string project, params string[] expected)
        => Assert.Equal(expected.Order(StringComparer.Ordinal), References(project));

    /// <summary>Core is the contracts and the maths: it takes no package, so every layer can stand on it.</summary>
    [Fact]
    public void Core_takes_no_package()
        => Assert.Empty(Project("XLines.Core").Descendants("PackageReference"));
}
