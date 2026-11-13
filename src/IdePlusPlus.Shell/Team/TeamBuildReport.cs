using System.IO;

namespace IdePlusPlus.Shell.Team;

/// <summary>
/// Reading a failed preflight build for the one cause the team can be blamed for: its copy of the
/// working tree lacks something the user's tree has. "Could not copy the file ...\w1\X\node_modules\...
/// because it was not found" says so in MSBuild's words, but not that the file is there in the user's own
/// tree and missing here only because git ignores it - which is the thing to tell the user.
/// </summary>
internal static class TeamBuildReport
{
    private const int MaxReported = 10;

    /// <summary>
    /// The folders (or files) a build report names inside <paramref name="worktreeRoot"/> that are not in
    /// the worktree but are in <paramref name="repositoryRoot"/>, as repository-relative paths. Each is the
    /// topmost missing piece: "FamWholesale\node_modules", not the thousand files under it. Only what
    /// really is absent from the copy and present in the user's tree is returned, so a path that is
    /// missing from both - a typo in a project file, say - is not blamed on the copy.
    /// </summary>
    public static IReadOnlyList<string> FindMissingFromCopy(string report, string worktreeRoot, string repositoryRoot)
    {
        var prefix = Path.TrimEndingDirectorySeparator(worktreeRoot) + Path.DirectorySeparatorChar;
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var from = 0;
        while (missing.Count < MaxReported && report.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase) is var at and >= 0)
        {
            var begin = at + prefix.Length;
            var end = begin;

            // A path ends at the first character a Windows path cannot hold, which is also where MSBuild
            // puts its own punctuation: a closing quote, "(line,col)", ": error", a project in [brackets].
            while (end < report.Length && report[end] is not ('"' or '\'' or '\r' or '\n' or '(' or ')' or '[' or ']' or '<' or '>' or '|' or '*' or '?' or ':'))
            {
                end++;
            }

            from = Math.Max(end, begin);
            var relative = report[begin..end].Trim().TrimEnd('.', ',', ';');
            if (relative.Length > 0 && TopmostMissing(relative, worktreeRoot, repositoryRoot) is { } found)
            {
                missing.Add(found);
            }
        }

        return [.. missing];
    }

    private static string? TopmostMissing(string relative, string worktreeRoot, string repositoryRoot)
    {
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var soFar = "";
        foreach (var segment in segments)
        {
            soFar = soFar.Length == 0 ? segment : Path.Combine(soFar, segment);
            if (!Path.Exists(Path.Combine(worktreeRoot, soFar)))
            {
                return Path.Exists(Path.Combine(repositoryRoot, soFar)) ? soFar : null;
            }
        }

        return null;
    }
}
