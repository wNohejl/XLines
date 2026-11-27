namespace XLines.Web.Tests.Support;

/// <summary>
/// The stylesheets as the page loads them, for tests that read a sheet's rules. desk.css,
/// themes.css and xlines.css are each one sheet cut into files at their banners, so that no file
/// passes 2000 lines; a test about "xlines.css" means the whole sheet, its parts concatenated in
/// the order App.razor and DeskHead link them, and reads it here rather than by file name.
/// </summary>
public static class Sheets
{
    /// <summary>This application's sheet, in link order: xlines.css, then xlines-windows.css.</summary>
    public static IReadOnlyList<string> AppParts { get; } =
    [
        Path.Combine(Repo.Root(), "src", "XLines.Web", "wwwroot", "css", "xlines.css"),
        Path.Combine(Repo.Root(), "src", "XLines.Web", "wwwroot", "css", "xlines-windows.css"),
    ];

    /// <summary>xlines.css whole, comments and all.</summary>
    public static string App => string.Concat(AppParts.Select(File.ReadAllText));

    /// <summary>The files one of the Desk's sheets was cut into, in the order DeskHead links them.</summary>
    public static IReadOnlyList<string> DeskParts(string sheet) => sheet switch
    {
        "desk.css" => ["desk.css", "desk-shell.css", "desk-windows.css", "desk-primitives.css", "desk-feedback.css", "desk-appearance.css"],
        "themes.css" => ["themes.css", "themes-more.css", "themes-accents.css"],
        _ => [sheet],
    };

    /// <summary>One of the Desk's sheets whole, comments and all.</summary>
    public static string Desk(string sheet) => string.Concat(DeskParts(sheet)
        .Select(part => File.ReadAllText(Path.Combine(Repo.Root(), "src", "XLines.Desk", "wwwroot", "css", part))));
}
