using System.Diagnostics;

namespace XLines.Ingestion.Services;

/// <summary>
/// Refuses a personal file a git repository would pick up. The repository is public, and a bet
/// log written where <c>git add</c> sees it is one commit from being published. A path outside
/// any repository, or one the repository ignores (<c>data/personal/</c>), is allowed.
/// </summary>
public static class RepositoryGuard
{
    /// <summary>Why <paramref name="path"/> may not be written, or null when it may.</summary>
    public static string? Refusal(string path)
    {
        var full = Path.GetFullPath(path);
        var root = RepositoryOf(full);

        if (root is null)
            return null;

        try
        {
            // The ignored folder may itself be a repository: the operator keeps the journal in a
            // private one there, where the file is tracked on purpose. A repository the one around
            // it ignores is not where the file would be published from, so the question passes
            // outward to the repository that could publish it.
            // A linked worktree is not such a repository: it commits to the repository it was cut
            // from, wherever its folder sits, so one inside an ignored folder (.claude/worktrees/)
            // publishes exactly as the main checkout does and answers for itself.
            while (!IsLinkedWorktree(root) && RepositoryOf(root) is { } outer && Ignores(outer, root))
                root = outer;

            return Ignores(root, full)
                ? null
                : $"{full} is inside the repository at {root} and git does not ignore it. The repository is public: "
                  + "write the journal under data/personal/ (ignored), or outside the repository.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No git to ask: inside a repository, fail closed.
            return $"{full} is inside the repository at {root}, and git could not be asked whether it is ignored ({ex.Message}).";
        }
    }

    /// <summary>Whether the repository at <paramref name="root"/> ignores the path, as <c>git check-ignore</c> answers.</summary>
    private static bool Ignores(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[] { "check-ignore", "-q", "--", relative })
            start.ArgumentList.Add(arg);

        using var git = Process.Start(start)!;
        git.WaitForExit(15_000);

        return git.HasExited && git.ExitCode == 0;
    }

    /// <summary>
    /// Whether the checkout at <paramref name="root"/> is a linked worktree: its <c>.git</c> is a
    /// file pointing into another repository's <c>worktrees/</c>. (A submodule's points into
    /// <c>modules/</c>, and is a repository of its own.)
    /// </summary>
    internal static bool IsLinkedWorktree(string root)
    {
        var git = Path.Combine(root, ".git");

        if (!File.Exists(git))
            return false;

        try
        {
            var pointer = File.ReadAllText(git).Replace('\\', '/');
            return pointer.StartsWith("gitdir:", StringComparison.Ordinal)
                   && pointer.Contains("/worktrees/", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The nearest directory above the path holding a <c>.git</c> (a directory, or a worktree's file).</summary>
    internal static string? RepositoryOf(string fullPath)
    {
        for (var dir = Path.GetDirectoryName(fullPath); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var git = Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir;
        }

        return null;
    }
}
