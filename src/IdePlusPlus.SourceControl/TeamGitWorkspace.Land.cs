namespace IdePlusPlus.SourceControl;

public enum TeamLandStatus
{
    /// <summary>The team branch was merged into the checked-out branch.</summary>
    Landed,

    /// <summary>The checked-out branch already has everything the team branch does.</summary>
    NothingToLand,

    /// <summary>It could not be merged cleanly; nothing in the checkout was changed. Message says why.</summary>
    Refused,
}

/// <summary>Commit is the merge commit and ChangedPaths the repository-relative files it changed,
/// both only when Landed. BranchName is the branch it landed on, or null on a detached HEAD.</summary>
public sealed record TeamLandResult(
    TeamLandStatus Status,
    string Message,
    string? Commit = null,
    string? BranchName = null,
    IReadOnlyList<string>? ChangedPaths = null);

public sealed partial class TeamGitWorkspace
{
    private static readonly string[] InProgressMarkers =
        ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "rebase-merge", "rebase-apply", "BISECT_LOG"];

    /// <summary>
    /// Merges <paramref name="teamBranch"/> into whatever branch the user has checked out, as a real
    /// merge commit - for a run left to go until done, whose result nobody is there to review file by
    /// file. Either it lands completely or nothing in the checkout changes and the result says why;
    /// the caller then offers the work for review instead.
    ///
    /// Done with plumbing rather than <c>git merge</c>, for the user's uncommitted files. The team
    /// branch starts from a snapshot of the working tree, so it already contains whatever was
    /// uncommitted when the run began - and <c>git merge</c> refuses to touch a file with local
    /// changes even when they are exactly what the merge would write. Here a local change that
    /// already matches the merge result is staged and so counts as merged, one that differs refuses
    /// the landing, and local changes to anything the merge does not touch are left as they are. In
    /// order: the merge tree (refused on a conflict with commits made since the run started), the
    /// commit, then the index and working tree via <c>read-tree -m -u</c> - which applies git's own
    /// would-overwrite checks - and only then the branch, moved with update-ref against the HEAD
    /// read at the start, so a commit the user makes meanwhile is never overwritten.
    ///
    /// The commit is the user's own, made with their git identity, on their branch.
    /// </summary>
    /// <param name="vetoChangedPaths">Shown the files the merge would change before anything is
    /// written; returning a reason refuses the landing - the chat pane uses it to stop at files open
    /// with unsaved edits.</param>
    public async Task<TeamLandResult> LandOnCheckedOutBranchAsync(
        string repoRoot,
        string teamBranch,
        string message,
        Func<IReadOnlyList<string>, string?>? vetoChangedPaths = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var marker in InProgressMarkers)
        {
            var markerPath = (await RunOrThrowAsync(repoRoot, ["rev-parse", "--git-path", marker], cancellationToken)).Trim();
            var fullPath = Path.IsPathRooted(markerPath) ? markerPath : Path.Combine(repoRoot, markerPath);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                return Refused($"a git operation is in progress in your checkout ({marker})");
            }
        }

        if (await ResolveRefAsync(repoRoot, "HEAD", cancellationToken) is not { } head)
        {
            return Refused("your checkout has no commits yet");
        }

        if (await ResolveRefAsync(repoRoot, teamBranch, cancellationToken) is not { } teamCommit)
        {
            return Refused($"there is no branch '{teamBranch}'");
        }

        var symbolic = await GitProcess.RunAsync(repoRoot, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        var branchName = symbolic.Success ? symbolic.StdOut.Trim() : null;
        var where = branchName is null ? "your detached HEAD" : $"'{branchName}'";

        if (await IsAncestorAsync(repoRoot, teamCommit, head, cancellationToken))
        {
            return new TeamLandResult(TeamLandStatus.NothingToLand, $"{where} already has everything on {teamBranch}");
        }

        var merge = await GitProcess.RunAsync(
            repoRoot, ["merge-tree", "--write-tree", "--name-only", "--no-messages", head, teamCommit], cancellationToken);
        if (merge.ExitCode is not (0 or 1))
        {
            throw new InvalidOperationException($"git merge-tree failed: {merge.Output}");
        }

        var lines = merge.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (merge.ExitCode == 1)
        {
            return Refused($"it conflicts with commits made on {where} since the run started, in {string.Join(", ", lines.Skip(1).Distinct())}");
        }

        var tree = lines[0];
        var changed = SplitNul(await RunOrThrowAsync(repoRoot, ["diff", "--name-only", "--no-renames", "-z", head, tree], cancellationToken));
        if (changed.Count == 0)
        {
            return new TeamLandResult(TeamLandStatus.NothingToLand, $"{where} already has all of the team's changes");
        }

        if (vetoChangedPaths?.Invoke(changed) is { } veto)
        {
            return Refused(veto);
        }

        // Local changes - tracked and untracked - to files the merge changes: fine only where the
        // working tree already holds what the merge would write.
        var localChanges = SplitNul(await RunOrThrowAsync(repoRoot, ["diff", "--name-only", "--no-renames", "-z", "HEAD"], cancellationToken))
            .Concat(SplitNul(await RunOrThrowAsync(repoRoot, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken)))
            .ToHashSet(StringComparer.Ordinal);
        var alreadyMerged = changed.Where(localChanges.Contains).ToList();
        var differing = new List<string>();
        foreach (var path in alreadyMerged)
        {
            if (!await WorkingTreeMatchesAsync(repoRoot, tree, path, cancellationToken))
            {
                differing.Add(path);
            }
        }

        if (differing.Count > 0)
        {
            return Refused($"you have uncommitted changes to {Describe(differing)} that differ from the team's");
        }

        var commit = await GitProcess.RunAsync(repoRoot, ["commit-tree", tree, "-p", head, "-p", teamCommit, "-m", message], cancellationToken);
        if (!commit.Success)
        {
            return Refused($"git could not make the merge commit: {commit.Output}");
        }

        var mergeCommit = commit.StdOut.Trim();
        if (alreadyMerged.Count > 0)
        {
            await RunOrThrowAsync(repoRoot, ["add", "-A", "--", .. alreadyMerged], cancellationToken);
        }

        var update = await GitProcess.RunAsync(repoRoot, ["read-tree", "-m", "-u", head, mergeCommit], cancellationToken);
        if (!update.Success)
        {
            await UnstageAsync(repoRoot, alreadyMerged);
            return Refused($"git would not update your working tree: {update.Output}");
        }

        var moved = await GitProcess.RunAsync(
            repoRoot, ["update-ref", "-m", $"IDE++ Ultra: merge {teamBranch}", "HEAD", mergeCommit, head], cancellationToken);
        if (!moved.Success)
        {
            // HEAD moved between the start and now. Put the index and working tree back over the
            // HEAD that is there, so the checkout is left as the user had it.
            if (await ResolveRefAsync(repoRoot, "HEAD", CancellationToken.None) is { } current)
            {
                await GitProcess.RunAsync(repoRoot, ["read-tree", "-m", "-u", mergeCommit, current], CancellationToken.None);
            }

            await UnstageAsync(repoRoot, alreadyMerged);
            return Refused($"{where} moved while the merge was being made: {moved.Output}");
        }

        return new TeamLandResult(
            TeamLandStatus.Landed,
            $"merged {teamBranch} into {where}",
            mergeCommit,
            branchName,
            changed);

        static TeamLandResult Refused(string why) => new(TeamLandStatus.Refused, why);
    }

    /// <summary>Whether the working tree's <paramref name="path"/> is exactly what
    /// <paramref name="tree"/> holds there - both absent counts. Hashed the way git add would store
    /// it, so line-ending conversion is taken into account.</summary>
    private static async Task<bool> WorkingTreeMatchesAsync(string repoRoot, string tree, string path, CancellationToken cancellationToken)
    {
        var wanted = await GitProcess.RunAsync(repoRoot, ["rev-parse", "--verify", "--quiet", $"{tree}:{path}"], cancellationToken);
        var exists = File.Exists(Path.Combine(repoRoot, path));
        if (!wanted.Success || !exists)
        {
            return !wanted.Success && !exists;
        }

        var actual = await RunOrThrowAsync(repoRoot, ["hash-object", "--", path], cancellationToken);
        return string.Equals(actual.Trim(), wanted.StdOut.Trim(), StringComparison.Ordinal);
    }

    private static async Task UnstageAsync(string repoRoot, IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            await GitProcess.RunAsync(repoRoot, ["reset", "-q", "--", .. paths], CancellationToken.None);
        }
    }

    private static List<string> SplitNul(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count <= 3 ? string.Join(", ", paths) : $"{string.Join(", ", paths.Take(3))} and {paths.Count - 3} more";
}
