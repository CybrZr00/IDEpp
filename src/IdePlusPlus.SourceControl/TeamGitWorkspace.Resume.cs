namespace IdePlusPlus.SourceControl;

public enum TeamStartMergeStatus
{
    /// <summary>The working-tree snapshot already contains the branch; nothing to merge.</summary>
    AlreadyContained,

    /// <summary>A merge commit of the snapshot and the branch was made without conflicts.</summary>
    Merged,

    /// <summary>The branch and the snapshot change the same lines; nothing was created.</summary>
    Conflicted,
}

/// <summary>Commit is where the run starts - the merge commit, or the snapshot itself when the branch
/// was already in it - and null on a conflict, when ConflictedPaths names the files.</summary>
public sealed record TeamStartMergeResult(TeamStartMergeStatus Status, string? Commit, IReadOnlyList<string> ConflictedPaths);

/// <summary>A later run's team branch that already carried on from the one asked about.
/// LaterCommits counts the work it has on top - commits the run machinery itself did not make.</summary>
public sealed record TeamBranchSuccessor(string Branch, int LaterCommits);

public sealed partial class TeamGitWorkspace
{
    private const string StartMergeAuthorName = "IDE++ Ultra";

    /// <summary>
    /// The run's own team branches in this repository - the ones a later run can carry on from -
    /// newest first.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListTeamBranchesAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        var output = await RunOrThrowAsync(
            repoRoot,
            ["for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", $"refs/heads/{BranchPrefix}"],
            cancellationToken);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.EndsWith("/team", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>The total size of the files in <paramref name="commit"/>'s tree - what a worktree of
    /// it takes on disk before anything is built in it.</summary>
    public async Task<long> GetTreeSizeAsync(string repoRoot, string commit, CancellationToken cancellationToken = default)
    {
        var output = await RunOrThrowAsync(repoRoot, ["ls-tree", "-r", "-l", "-z", commit], cancellationToken);
        long total = 0;
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <type> <object> <size>\t<path>" - the size is "-" for a submodule.
            var fields = entry.Split('\t', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 4 && long.TryParse(fields[3], out var size))
            {
                total += size;
            }
        }

        return total;
    }

    /// <summary>
    /// Another team branch that already contains <paramref name="branch"/> and has work on top of it,
    /// or null - so carrying on from an old run's branch can be caught before it leaves out a later
    /// run that carried on from it too, and the team redoes that run's work.
    ///
    /// Commits by the run machinery itself (the snapshot and start-from merge, both authored as
    /// "IDE++ Ultra") are not work: a run stopped before merging anything still keeps a team branch
    /// containing the one it started from, and that is no reason to suggest it. Of the rest, the one
    /// with the most work on top wins rather than the newest, for the same reason - a run started
    /// from the old branch by mistake is newer than the run it should have carried on from.
    ///
    /// Only asked about team branches: every team branch contains master, and "a later run carried
    /// on from master" is no warning at all.
    /// </summary>
    public async Task<TeamBranchSuccessor?> FindLaterTeamBranchAsync(
        string repoRoot, string branch, CancellationToken cancellationToken = default)
    {
        var name = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
        if (!name.StartsWith(BranchPrefix, StringComparison.Ordinal) || !name.EndsWith("/team", StringComparison.Ordinal)
            || await ResolveRefAsync(repoRoot, branch, cancellationToken) is not { } branchCommit)
        {
            return null;
        }

        TeamBranchSuccessor? best = null;
        foreach (var candidate in await ListTeamBranchesAsync(repoRoot, cancellationToken))
        {
            if (!await IsAncestorAsync(repoRoot, branchCommit, candidate, cancellationToken))
            {
                continue;
            }

            var authors = await RunOrThrowAsync(repoRoot, ["log", "--format=%an", $"{branchCommit}..{candidate}"], cancellationToken);
            var laterCommits = authors
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(author => author != SnapshotAuthorName);
            if (laterCommits > 0 && (best is null || laterCommits > best.LaterCommits))
            {
                best = new TeamBranchSuccessor(candidate, laterCommits);
            }
        }

        return best;
    }

    /// <summary>
    /// Makes the commit a run carrying on from <paramref name="branch"/> starts from: the user's
    /// working-tree <paramref name="snapshot"/> merged with that branch, so workers see both what the
    /// user has now and what the earlier work built.
    ///
    /// Done with merge-tree and commit-tree, like the snapshot itself: no worktree, index or ref is
    /// touched, and on a conflict nothing at all is created. A conflict is reported rather than left
    /// in files for someone to resolve, because nobody is there yet - the run has not started.
    /// </summary>
    public async Task<TeamStartMergeResult> MergeIntoSnapshotAsync(
        string repoRoot, string snapshot, string branch, CancellationToken cancellationToken = default)
    {
        var branchCommit = await ResolveRefAsync(repoRoot, branch, cancellationToken)
                           ?? throw new InvalidOperationException($"There is no branch or commit named '{branch}' in this repository.");

        if (await IsAncestorAsync(repoRoot, branchCommit, snapshot, cancellationToken))
        {
            return new TeamStartMergeResult(TeamStartMergeStatus.AlreadyContained, snapshot, []);
        }

        var merge = await GitProcess.RunAsync(
            repoRoot,
            ["merge-tree", "--write-tree", "--name-only", "--no-messages", snapshot, branchCommit],
            cancellationToken);

        // Exit 0 is a clean merge and 1 a conflicted one; either way the first line is a tree. Anything
        // else means git could not merge at all.
        if (merge.ExitCode is not (0 or 1))
        {
            throw new InvalidOperationException($"git merge-tree failed: {merge.Output}");
        }

        var lines = merge.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (merge.ExitCode == 1)
        {
            return new TeamStartMergeResult(TeamStartMergeStatus.Conflicted, null, lines.Skip(1).Distinct().ToList());
        }

        var commit = await RunOrThrowAsync(
            repoRoot,
            [
                .. CommitConfigArguments(StartMergeAuthorName, SnapshotAuthorEmail),
                "commit-tree", lines[0], "-p", snapshot, "-p", branchCommit,
                "-m", $"IDE++ Ultra: start from {branch}",
            ],
            cancellationToken);
        return new TeamStartMergeResult(TeamStartMergeStatus.Merged, commit.Trim(), []);
    }
}
