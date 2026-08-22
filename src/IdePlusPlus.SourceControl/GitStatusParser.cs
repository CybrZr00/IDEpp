using System.Text.RegularExpressions;

namespace IdePlusPlus.SourceControl;

/// <summary>
/// Parses `git status --porcelain=v1 --branch` output. Porcelain v1 (rather than v2) because its
/// per-file line format is simpler to parse correctly for the common case; the tradeoff is that
/// paths containing characters git considers "unusual" (non-ASCII, control chars, backslash, a
/// literal double-quote) come back quoted/escaped and are not unquoted here - a known limitation
/// acceptable for a first version, since the vast majority of real project paths never hit it.
/// </summary>
public static class GitStatusParser
{
    private static readonly HashSet<string> ConflictCodes = ["DD", "AU", "UD", "UA", "DU", "AA", "UU"];

    public static GitStatus Parse(string porcelainOutput)
    {
        string? branch = null;
        var ahead = 0;
        var behind = 0;
        var staged = new List<GitFileChange>();
        var unstaged = new List<GitFileChange>();

        using var reader = new StringReader(porcelainOutput);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                (branch, ahead, behind) = ParseBranchLine(line[3..]);
                continue;
            }

            ParseFileLine(line, staged, unstaged);
        }

        return new GitStatus(branch, ahead, behind, staged, unstaged);
    }

    private static (string? Branch, int Ahead, int Behind) ParseBranchLine(string branchLine)
    {
        if (branchLine.StartsWith("No commits yet on ", StringComparison.Ordinal))
        {
            return (branchLine["No commits yet on ".Length..], 0, 0);
        }

        if (branchLine is "HEAD (no branch)" or "HEAD")
        {
            return (null, 0, 0);
        }

        var branchPart = branchLine;
        var bracketIndex = branchLine.IndexOf(" [", StringComparison.Ordinal);
        if (bracketIndex >= 0)
        {
            branchPart = branchLine[..bracketIndex];
        }

        var branch = branchPart.Split("...", 2)[0];

        var ahead = 0;
        var behind = 0;

        var aheadMatch = Regex.Match(branchLine, @"ahead (\d+)");
        if (aheadMatch.Success)
        {
            ahead = int.Parse(aheadMatch.Groups[1].Value);
        }

        var behindMatch = Regex.Match(branchLine, @"behind (\d+)");
        if (behindMatch.Success)
        {
            behind = int.Parse(behindMatch.Groups[1].Value);
        }

        return (branch, ahead, behind);
    }

    private static void ParseFileLine(string line, List<GitFileChange> staged, List<GitFileChange> unstaged)
    {
        if (line.Length < 4)
        {
            return;
        }

        var code = line[..2];
        var indexStatus = line[0];
        var worktreeStatus = line[1];
        var rest = line[3..];

        // Renames report as "old -> new"; the new path is what matters for staging/diffing.
        var arrowIndex = rest.IndexOf(" -> ", StringComparison.Ordinal);
        var path = arrowIndex >= 0 ? rest[(arrowIndex + 4)..] : rest;

        if (code == "??")
        {
            unstaged.Add(new GitFileChange(path, GitFileStatus.Untracked, IsStaged: false));
            return;
        }

        if (code == "!!")
        {
            return; // ignored - only appears with --ignored, which callers here don't pass
        }

        if (ConflictCodes.Contains(code))
        {
            // Report as a single unmerged entry rather than splitting index/worktree - a conflict
            // isn't meaningfully "staged" or "unstaged" the way an ordinary change is.
            unstaged.Add(new GitFileChange(path, GitFileStatus.Conflicted, IsStaged: false));
            return;
        }

        if (indexStatus != ' ')
        {
            staged.Add(new GitFileChange(path, MapStatus(indexStatus), IsStaged: true));
        }

        if (worktreeStatus != ' ')
        {
            unstaged.Add(new GitFileChange(path, MapStatus(worktreeStatus), IsStaged: false));
        }
    }

    private static GitFileStatus MapStatus(char code) => code switch
    {
        'A' => GitFileStatus.Added,
        'D' => GitFileStatus.Deleted,
        'R' => GitFileStatus.Renamed,
        'C' => GitFileStatus.Added, // copied - treated as a new file for this first version
        _ => GitFileStatus.Modified,
    };
}
