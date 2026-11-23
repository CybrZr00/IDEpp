using System.Text.RegularExpressions;

namespace IdePlusPlus.SourceControl;

/// <summary>
/// The parts of TeamGitWorkspace that an Ultra Pipeline run needs beyond the w&lt;N&gt; team: agents
/// with names of their own (u1, d1, b2, j3), worktrees that sit on a commit rather than a branch (the
/// shared read-only checkout the surveyors and reviewers read, the throwaway gate checkout), and
/// undoing an agent's edits to files it was never allowed to touch.
/// </summary>
public sealed partial class TeamGitWorkspace
{
    /// <summary>The team branch's own name. An agent called this would get a branch that is the team
    /// branch, so it is not a valid agent name.</summary>
    private const string TeamBranchLeaf = "team";

    /// <summary>
    /// True for a name a pipeline agent may have: a lower-case letter, then up to eleven more letters
    /// or digits - "u1", "d1", "b12", "j5". It becomes the last segment of a branch ref and a folder
    /// name, so nothing that git or Windows treats specially (dots, slashes, colons, trailing spaces,
    /// "..") can get in, and "team" is taken.
    /// </summary>
    public static bool IsValidAgentName(string? name) =>
        name is not null
        && AgentNamePattern().IsMatch(name)
        && !string.Equals(name, TeamBranchLeaf, StringComparison.Ordinal);

    /// <summary>
    /// The branch an agent named <paramref name="agentName"/> works on - a sibling of the team branch
    /// under the run's prefix, for the reason <see cref="GetTeamBranchName"/> gives. Throws for a name
    /// <see cref="IsValidAgentName"/> refuses, since a bad one would otherwise fail later, inside git,
    /// with a message about refs rather than about the name.
    /// </summary>
    public static string GetAgentBranchName(string runId, string agentName) =>
        IsValidAgentName(agentName)
            ? BranchFor(runId, agentName)
            : throw new ArgumentException(
                $"'{agentName}' is not a valid agent name: use a lower-case letter followed by up to 11 "
                + $"letters or digits, and not '{TeamBranchLeaf}'.",
                nameof(agentName));

    // The one place the "<prefix><run>/<leaf>" shape is spelled out, so a w<N> branch and an agent
    // branch can never drift apart.
    private static string BranchFor(string runId, string leaf) => $"{BranchPrefix}{runId}/{leaf}";

    /// <summary>
    /// Checks out <paramref name="commit"/> with a detached HEAD into <paramref name="worktreeDirectory"/>,
    /// creating missing parent folders. For a checkout that nobody commits in: no branch means nothing
    /// to merge, nothing to delete afterwards, and several of them can sit on the same commit, which a
    /// branch could not do.
    /// </summary>
    public Task<GitCommandResult> AddDetachedWorktreeAsync(
        string repoRoot, string worktreeDirectory, string commit, CancellationToken cancellationToken = default) =>
        RunAsync(repoRoot, ["worktree", "add", "--detach", worktreeDirectory, commit], cancellationToken);

    /// <summary>
    /// Moves a detached worktree to <paramref name="commit"/>, discarding whatever it had changed. It
    /// is how the shared read-only checkout follows the team branch from one stage to the next, and how
    /// the gate checkout is put back after a formatter or a mutation run has rewritten files in it.
    ///
    /// Refuses a worktree that has a branch checked out: this is a hard reset, and on a branch it
    /// would move the branch. It resets rather than checking out so that no post-checkout hook runs
    /// (a hook that fails would fail the move), the same reason commits here skip hooks.
    /// </summary>
    public async Task<GitCommandResult> CheckoutDetachedAsync(
        string worktreeDirectory, string commit, CancellationToken cancellationToken = default)
    {
        // symbolic-ref -q exits 0 on a branch, 1 on a detached HEAD, and something else on a real error.
        var head = await GitProcess.RunAsync(worktreeDirectory, ["symbolic-ref", "-q", "HEAD"], cancellationToken);
        if (head.ExitCode == 0)
        {
            return new GitCommandResult(
                false, $"'{worktreeDirectory}' has a branch checked out; only a detached worktree can be moved.");
        }

        if (head.ExitCode != 1)
        {
            return new GitCommandResult(false, head.Output);
        }

        return await RunAsync(
            worktreeDirectory, ["reset", "--hard", "--quiet", $"{commit}^{{commit}}"], cancellationToken);
    }

    /// <summary>
    /// Puts each of <paramref name="relativePaths"/> back to how <paramref name="fromCommit"/> has it,
    /// in the index and on disk: a path the commit has is restored, one it lacks is deleted. This is
    /// what undoes an agent's edit to a file it does not own - a corrective commit then records it.
    ///
    /// Paths are repository-relative, spelled the way git spells them (as GetChangedFilesAsync gives
    /// them). Anything that is not a plain path inside the worktree - rooted, with a ".." or ".git"
    /// segment, with a colon - is refused before git runs, so a path reported by a model cannot reach
    /// outside the worktree. Pathspec magic is off, so a file called "a*.cs" means that file.
    /// </summary>
    public async Task<GitCommandResult> RestorePathsAsync(
        string worktreeDirectory,
        string fromCommit,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        if (relativePaths.Count == 0)
        {
            return new GitCommandResult(true, "");
        }

        var root = Path.GetFullPath(worktreeDirectory);
        var paths = new List<string>(relativePaths.Count);
        foreach (var path in relativePaths)
        {
            if (TryNormalizeWorktreePath(root, path) is not { } normalized)
            {
                return new GitCommandResult(false, $"Refusing to restore '{path}': it is not a path inside the worktree.");
            }

            if (!paths.Contains(normalized, StringComparer.Ordinal))
            {
                paths.Add(normalized);
            }
        }

        var listing = await GitProcess.RunAsync(
            worktreeDirectory,
            ["--literal-pathspecs", "ls-tree", "-r", "-z", "--name-only", $"{fromCommit}^{{commit}}", "--", .. paths],
            cancellationToken);
        if (!listing.Success)
        {
            return new GitCommandResult(false, listing.Output);
        }

        var inCommit = listing.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var toRestore = paths.Where(inCommit.Contains).ToList();
        var toDelete = paths.Where(p => !inCommit.Contains(p)).ToList();

        if (toRestore.Count > 0)
        {
            // restore, not checkout: checkout runs the post-checkout hook, restore does not.
            var restored = await GitProcess.RunAsync(
                worktreeDirectory,
                ["--literal-pathspecs", "restore", $"--source={fromCommit}", "--staged", "--worktree", "--", .. toRestore],
                cancellationToken);
            if (!restored.Success)
            {
                return new GitCommandResult(false, restored.Output);
            }
        }

        if (toDelete.Count > 0)
        {
            // Out of the index first (a no-op for a file git never tracked), then off the disk.
            var unstaged = await GitProcess.RunAsync(
                worktreeDirectory,
                ["--literal-pathspecs", "rm", "-f", "--cached", "--ignore-unmatch", "-q", "--", .. toDelete],
                cancellationToken);
            if (!unstaged.Success)
            {
                return new GitCommandResult(false, unstaged.Output);
            }

            foreach (var path in toDelete)
            {
                var fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
                try
                {
                    if (File.Exists(fullPath))
                    {
                        File.SetAttributes(fullPath, FileAttributes.Normal);
                        File.Delete(fullPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new GitCommandResult(false, $"Could not delete '{path}': {ex.Message}");
                }
            }
        }

        return new GitCommandResult(true, $"Restored {toRestore.Count} and removed {toDelete.Count} path(s).");
    }

    /// <summary>The path with forward slashes and no leading "./", or null when it is not a plain
    /// relative path that stays inside <paramref name="worktreeRoot"/>.</summary>
    private static string? TryNormalizeWorktreePath(string worktreeRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains(':'))
        {
            return null;
        }

        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.EndsWith('/'))
        {
            return null;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var full = Path.GetFullPath(Path.Combine(worktreeRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = worktreeRoot.EndsWith(Path.DirectorySeparatorChar) ? worktreeRoot : worktreeRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? normalized : null;
    }

    // \z, not $: $ also matches before a trailing newline, which would let "u1\n" become a ref name.
    [GeneratedRegex(@"^[a-z][a-z0-9]{0,11}\z")]
    private static partial Regex AgentNamePattern();
}
