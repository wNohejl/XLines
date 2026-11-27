using System.Text.RegularExpressions;

namespace XLines.Tests.Reliability;

/// <summary>
/// Plays and drives are written only through tracked saves (see <c>GamePlay</c>). An open Gamecast
/// window learns that its game moved from the <c>plays:{id}</c> notice the save raises
/// (<c>ChangeNotifier</c>), and reads what moved by the rows' <c>UpdatedAt</c>, which the writer
/// stamps. A bulk <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>, or raw SQL against the tables, raises no
/// notice and stamps nothing, so every open window — and the shared snapshot behind them — would
/// keep drawing what the database no longer holds. Read from the source, as
/// <see cref="DataTierGuardTests"/> reads it: "writes in bulk" is a fact about the code.
/// </summary>
public partial class GamecastTrackedWritesTests
{
    /// <summary>
    /// A statement that names one of the two sets and then, before its semicolon, a bulk write.
    /// Lazy and bounded by the semicolon, so it spans a fluent chain broken over lines and no more.
    /// </summary>
    [GeneratedRegex(@"(\b(GamePlays|GameDrives)\b|\bSet<Game(Play|Drive)>\(\))[^;]*?\.Execute(Update|Delete)(Async)?\b", RegexOptions.Singleline)]
    private static partial Regex BulkWrite();

    /// <summary>Raw SQL that writes either table, in any quoting.</summary>
    [GeneratedRegex(@"\b(UPDATE|DELETE\s+FROM|INSERT\s+INTO|TRUNCATE(\s+TABLE)?)\s+(ONLY\s+)?(\\?""|`)?(GamePlays|GameDrives|game_plays|game_drives)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RawWrite();

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XLines.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("XLines.slnx not found above the test output.");
    }

    /// <summary>Every C# source under <c>src</c>, migrations and build output aside.</summary>
    private static IEnumerable<string> SourceFiles(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    /// <summary>The code of a file without its comments, so the rule may be stated in prose where it applies.</summary>
    private static string Code(string text)
        => Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    public static IEnumerable<string> Offences(string code)
        => BulkWrite().Matches(code).Concat(RawWrite().Matches(code)).Select(m => m.Value.Trim());

    [Fact]
    public void Plays_and_drives_are_never_written_in_bulk()
    {
        var root = Root();

        var offenders = SourceFiles(root)
            .SelectMany(f => Offences(Code(File.ReadAllText(f))).Select(o => $"{Path.GetRelativePath(root, f)}: {o}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "GamePlays and GameDrives are written only through tracked saves, which stamp UpdatedAt and "
            + "raise plays:{id}; a bulk write does neither and an open Gamecast never sees it: "
            + string.Join("; ", offenders));
    }

    /// <summary>Guards the guard: the patterns catch the shapes they are for, and the scan finds the writer.</summary>
    [Fact]
    public void The_guard_catches_a_bulk_write_and_sees_the_writer()
    {
        Assert.NotEmpty(Offences("await db.GamePlays.Where(p => p.GameId == id).ExecuteDeleteAsync(ct);"));
        Assert.NotEmpty(Offences("await db.GameDrives\n    .Where(d => d.IsCurrent)\n    .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsCurrent, false), ct);"));
        Assert.NotEmpty(Offences("db.Set<GamePlay>().ExecuteDelete();"));
        Assert.NotEmpty(Offences("await db.Database.ExecuteSqlRawAsync(\"DELETE FROM \\\"GamePlays\\\" WHERE 1 = 1\");"));
        Assert.Empty(Offences("var stored = await db.GamePlays.Where(p => p.GameId == gameId).ToListAsync(ct); await db.Alerts.ExecuteDeleteAsync(ct);"));

        var root = Root();
        Assert.Contains(Path.Combine("src", "XLines.Ingestion", "Services", "GamecastWriter.cs"),
            SourceFiles(root).Select(f => Path.GetRelativePath(root, f)));
    }
}
