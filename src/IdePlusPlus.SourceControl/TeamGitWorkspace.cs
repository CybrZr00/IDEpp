using System.Text;
using System.Text.RegularExpressions;

namespace IdePlusPlus.SourceControl;

/// <summary>
/// The git side of an Ultra team run: N identical copilot workers with no orchestrator, each in its
/// own worktree and branch, all started from one snapshot of the user's working tree, merging their
/// finished work into a shared team branch. At the end the team's changes are read back out file by
/// file for the user to review.
///
/// Three properties matter more than anything else here:
///  - The user's repository is never disturbed. The snapshot is built in a throwaway index and
///    written with commit-tree, so their index, working tree, HEAD and refs stay exactly as they
///    were. Worktrees go wherever the caller says (outside the repo), so they never show up in the
///    user's status or file listings.
///  - Merges into the team branch can't clobber each other. FastForwardBranchAsync is a
///    compare-and-swap on the ref (update-ref with the expected old value), so when two workers race,
///    exactly one wins and the other is told to sync and retry, with no lock or coordinator needed.
///  - Nothing depends on the user's personal git config. Commits carry an explicit identity, and
///    signing, hooks, autostash and fast-forward policy are pinned per command, the same way
///    GitService.PullAsync pins its reconcile strategy. Otherwise one global setting (commit.gpgsign
///    with a pinentry, merge.ff=only, a husky pre-commit hook in a worktree with no node_modules)
///    quietly breaks every worker at once.
///
/// Stateless, so one instance can be shared by every worker in a run.
/// </summary>
public sealed partial class TeamGitWorkspace
{
    private const string BranchPrefix = "ideplusplus/ultra/";
    private const string SnapshotAuthorName = "IDE++ Ultra";
    private const string SnapshotAuthorEmail = "ultra@ideplusplus.local";
    private const string SnapshotMessage = "IDE++ Ultra snapshot";
    private const string WorkerEmailDomain = "ideplusplus.local";

    /// <summary>
    /// The shared branch a run's workers merge into. It sits beside the worker branches under one
    /// per-run prefix rather than being their parent ("ideplusplus/ultra/&lt;runId&gt;" with the
    /// workers under ".../&lt;runId&gt;/w1"): git refuses a ref whose name is a path prefix of
    /// another ref, so that layout fails with "cannot lock ref ... exists" on the first worktree.
    /// </summary>
    public static string GetTeamBranchName(string runId) => $"{BranchPrefix}{runId}/team";

    public static string GetWorkerBranchName(string runId, int workerNumber) => $"{BranchPrefix}{runId}/w{workerNumber}";

    /// <summary>The top of the working tree containing <paramref name="directory"/>, as a Windows
    /// full path (git reports it with forward slashes), or null when it isn't inside one.</summary>
    public async Task<string?> FindRepositoryRootAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await GitProcess.RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken);
        var topLevel = result.StdOut.Trim();
        return result.Success && topLevel.Length > 0 ? Path.GetFullPath(topLevel) : null;
    }

    /// <summary>
    /// Commits the user's working tree exactly as it is right now and returns the commit's sha, so
    /// every worker starts from what the user actually sees rather than from HEAD. That means tracked
    /// changes (staged or not), untracked files that aren't ignored, and deletions.
    ///
    /// The commit is built in a throwaway index (GIT_INDEX_FILE) and written with commit-tree, which
    /// creates the commit object without moving any ref. The user's index, working tree, HEAD and
    /// branches are never touched, so a half-staged change they were in the middle of survives
    /// intact. The commit stays unreferenced until the caller points a branch at it. HEAD is resolved
    /// once and used both to seed the index and as the parent, so a commit the user makes mid-snapshot
    /// can't leave the two disagreeing. In a repo with no commits yet, the snapshot has no parent.
    /// </summary>
    public async Task<string> CreateSnapshotCommitAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        // A path that doesn't exist yet, not Path.GetTempFileName(): git rejects an existing empty
        // file as a corrupt index ("index file smaller than expected").
        var indexFile = Path.Combine(Path.GetTempPath(), $"ideplusplus-snapshot-{Guid.NewGuid():N}.index");
        var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = indexFile };

        try
        {
            var head = await ResolveRefAsync(repoRoot, "HEAD", cancellationToken);
            if (head is not null)
            {
                await RunOrThrowAsync(repoRoot, ["read-tree", head], cancellationToken, environment);
            }

            await RunOrThrowAsync(repoRoot, ["add", "-A"], cancellationToken, environment);
            var tree = (await RunOrThrowAsync(repoRoot, ["write-tree"], cancellationToken, environment)).Trim();

            List<string> commitTree =
                [.. CommitConfigArguments(SnapshotAuthorName, SnapshotAuthorEmail), "commit-tree", tree, "-m", SnapshotMessage];
            if (head is not null)
            {
                commitTree.Add("-p");
                commitTree.Add(head);
            }

            return (await RunOrThrowAsync(repoRoot, commitTree, cancellationToken)).Trim();
        }
        finally
        {
            DeleteFileBestEffort(indexFile);
            DeleteFileBestEffort(indexFile + ".lock");
        }
    }

    /// <summary>Creates <paramref name="branchName"/> at <paramref name="commit"/> without checking
    /// it out (unlike GitService.CreateBranchAsync), so the user's current branch doesn't change.
    /// This is for the team branch, which no worktree ever checks out: workers only merge from it
    /// and fast-forward it.</summary>
    public Task<GitCommandResult> CreateBranchAsync(
        string repoRoot, string branchName, string commit, CancellationToken cancellationToken = default) =>
        RunAsync(repoRoot, ["branch", branchName, commit], cancellationToken);

    /// <summary>Checks out a new branch <paramref name="newBranchName"/> at
    /// <paramref name="startCommit"/> into <paramref name="worktreeDirectory"/>, creating missing
    /// parent folders. The worktree shares the repo's object store and refs, which is what lets a
    /// worker's commits be merged and fast-forwarded into the team branch with no push or fetch. It
    /// has its own HEAD, index and files, so workers never see each other's edits until they
    /// merge.</summary>
    public Task<GitCommandResult> AddWorktreeAsync(
        string repoRoot, string worktreeDirectory, string newBranchName, string startCommit,
        CancellationToken cancellationToken = default) =>
        RunAsync(repoRoot, ["worktree", "add", "-b", newBranchName, worktreeDirectory, startCommit], cancellationToken);

    /// <summary>The full sha HEAD points at in <paramref name="directory"/>, a repo or one of its
    /// worktrees (each worktree has its own HEAD). Throws when there isn't one, as on an unborn
    /// branch, since every caller needs a real commit to work from.</summary>
    public async Task<string> GetHeadAsync(string directory, CancellationToken cancellationToken = default) =>
        (await RunOrThrowAsync(directory, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken)).Trim();

    /// <summary>The commit sha <paramref name="refName"/> resolves to, or null when it doesn't
    /// exist. Pass a full name ("refs/heads/...") to be exact: git looks a bare name up in its usual
    /// search order, where a tag of the same name wins over a branch.</summary>
    public async Task<string?> ResolveRefAsync(string repoRoot, string refName, CancellationToken cancellationToken = default)
    {
        var result = await GitProcess.RunAsync(
            repoRoot, ["rev-parse", "--verify", "--quiet", $"{refName}^{{commit}}"], cancellationToken);
        var sha = result.StdOut.Trim();
        return result.Success && sha.Length > 0 ? sha : null;
    }

    /// <summary>True when the worktree differs from its HEAD in any way, untracked files included,
    /// which is what a merge gate needs to know. --untracked-files is pinned because a user's
    /// status.showUntrackedFiles=no would otherwise hide new files from this check.</summary>
    public async Task<bool> HasUncommittedChangesAsync(string worktreeDirectory, CancellationToken cancellationToken = default)
    {
        var output = await RunOrThrowAsync(
            worktreeDirectory, ["status", "--porcelain", "--untracked-files=normal"], cancellationToken);
        return !string.IsNullOrWhiteSpace(output);
    }

    /// <summary>
    /// Stages everything in the worktree (add -A, so new and deleted files too) and commits it as
    /// <paramref name="authorName"/>, with a matching "&lt;slug&gt;@ideplusplus.local" email so the
    /// team branch's log shows which worker wrote what.
    ///
    /// Refuses (Failed) while a file from an unfinished merge still contains conflict markers,
    /// because add -A would mark it resolved and commit the markers into the team's history. During
    /// a merge it commits even when the resolved tree matches HEAD: that commit is what concludes the
    /// merge, and reporting NothingToCommit would leave the worker stuck mid-merge.
    ///
    /// Hooks are skipped (--no-verify). These are internal checkpoint commits, and a pre-commit hook
    /// that expects node_modules or some installed tool fails in a fresh worktree and would block every
    /// worker. The user's hooks still run on the real commit, when they commit the landed changes.
    /// </summary>
    public async Task<TeamCommitResult> CommitAllAsync(
        string worktreeDirectory, string message, string authorName, CancellationToken cancellationToken = default)
    {
        var unresolved = await FindFilesWithConflictMarkersAsync(worktreeDirectory, cancellationToken);
        if (unresolved.Count > 0)
        {
            return new TeamCommitResult(
                TeamCommitStatus.Failed,
                null,
                $"Not committed: {string.Join(", ", unresolved)} still contain conflict markers from the merge. " +
                "Resolve them, then commit again.");
        }

        var add = await GitProcess.RunAsync(worktreeDirectory, ["add", "-A"], cancellationToken);
        if (!add.Success)
        {
            return new TeamCommitResult(TeamCommitStatus.Failed, null, add.Output);
        }

        var merging = (await GitProcess.RunAsync(
            worktreeDirectory, ["rev-parse", "--verify", "--quiet", "MERGE_HEAD"], cancellationToken)).Success;
        if (!merging)
        {
            // diff --quiet exits 1 for "there are differences", 0 for none, anything else for an error.
            var staged = await GitProcess.RunAsync(worktreeDirectory, ["diff", "--cached", "--quiet"], cancellationToken);
            if (staged.ExitCode == 0)
            {
                return new TeamCommitResult(
                    TeamCommitStatus.NothingToCommit,
                    await GetHeadAsync(worktreeDirectory, cancellationToken),
                    "Nothing to commit: the worktree already matches its last commit.");
            }

            if (staged.ExitCode != 1)
            {
                return new TeamCommitResult(TeamCommitStatus.Failed, null, staged.Output);
            }
        }

        var commit = await GitProcess.RunAsync(
            worktreeDirectory,
            [.. CommitConfigArguments(authorName, ToWorkerEmail(authorName)), "commit", "--no-verify", "-m", message],
            cancellationToken);
        if (!commit.Success)
        {
            return new TeamCommitResult(TeamCommitStatus.Failed, null, commit.Output);
        }

        return new TeamCommitResult(
            TeamCommitStatus.Committed, await GetHeadAsync(worktreeDirectory, cancellationToken), commit.Output);
    }

    /// <summary>
    /// Merges <paramref name="branchName"/> (normally the team branch) into the worktree's own
    /// branch. This is the "sync before you merge" step, and it's where git catches two workers'
    /// textual conflicts. UpToDate and Merged are told apart by whether HEAD moved, not by parsing
    /// git's localised "Already up to date." text.
    ///
    /// On a conflict the merge is deliberately left in progress, markers and all: the worker resolves
    /// the files, then CommitAllAsync concludes the merge. Any other refusal (say, uncommitted
    /// changes the merge would overwrite) comes back as Failed with git's message, with nothing
    /// changed.
    /// </summary>
    public async Task<TeamSyncResult> MergeBranchIntoWorktreeAsync(
        string worktreeDirectory, string branchName, string authorName, CancellationToken cancellationToken = default)
    {
        var before = await GitProcess.RunAsync(worktreeDirectory, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        if (!before.Success)
        {
            return new TeamSyncResult(TeamSyncStatus.Failed, [], before.Output);
        }

        // --ff, --no-autostash and --no-verify-signatures override merge.ff=only, merge.autoStash and
        // merge.verifySignatures in the user's config, any of which would make a team merge fail or
        // behave differently on their machine. --no-edit because there's no terminal for an editor.
        var merge = await GitProcess.RunAsync(
            worktreeDirectory,
            [
                .. CommitConfigArguments(authorName, ToWorkerEmail(authorName)),
                "merge", "--no-edit", "--ff", "--no-autostash", "--no-verify", "--no-verify-signatures", "--no-stat",
                branchName,
            ],
            cancellationToken);

        if (merge.Success)
        {
            var after = await GetHeadAsync(worktreeDirectory, cancellationToken);
            return after == before.StdOut.Trim()
                ? new TeamSyncResult(TeamSyncStatus.UpToDate, [], merge.Output)
                : new TeamSyncResult(TeamSyncStatus.Merged, [], merge.Output);
        }

        var conflicted = await GetConflictedPathsAsync(worktreeDirectory, cancellationToken);
        return conflicted.Count > 0
            ? new TeamSyncResult(TeamSyncStatus.Conflicted, conflicted, merge.Output)
            : new TeamSyncResult(TeamSyncStatus.Failed, [], merge.Output);
    }

    /// <summary>Whether <paramref name="ancestor"/> is reachable from <paramref name="descendant"/>
    /// (a commit counts as its own ancestor), meaning that moving a branch from one to the other is a
    /// fast-forward. merge-base exits 1 for "no". Any other failure, such as an unknown commit,
    /// throws rather than reading as "no".</summary>
    public async Task<bool> IsAncestorAsync(
        string repoRoot, string ancestor, string descendant, CancellationToken cancellationToken = default)
    {
        var result = await GitProcess.RunAsync(
            repoRoot, ["merge-base", "--is-ancestor", ancestor, descendant], cancellationToken);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException($"git merge-base --is-ancestor failed: {result.Output}"),
        };
    }

    /// <summary>
    /// Moves <paramref name="branchName"/> from <paramref name="expectedOldCommit"/> to
    /// <paramref name="newCommit"/>, but only as a fast-forward and only if nobody else moved it
    /// first. This is how a worker publishes its merge to the team.
    ///
    /// The update is a single update-ref with the expected old value, which git applies atomically
    /// under the ref's lock: it either swaps old for new or changes nothing. Two workers racing from
    /// the same old commit therefore can't both win. The loser gets RefMoved, syncs, and tries again,
    /// and no worker's merge can ever be overwritten by another's. <paramref name="expectedOldCommit"/>
    /// must be a sha read earlier (ResolveRefAsync), not the branch's name, or there is nothing to
    /// compare against.
    /// </summary>
    public async Task<TeamFastForwardResult> FastForwardBranchAsync(
        string repoRoot, string branchName, string newCommit, string expectedOldCommit,
        CancellationToken cancellationToken = default)
    {
        var refName = branchName.StartsWith("refs/", StringComparison.Ordinal) ? branchName : $"refs/heads/{branchName}";

        // Full shas up front, so the "did the ref move?" check below compares like with like even when
        // the caller passed abbreviated ones.
        var newSha = await ResolveRefAsync(repoRoot, newCommit, cancellationToken);
        var expectedOldSha = await ResolveRefAsync(repoRoot, expectedOldCommit, cancellationToken);
        if (newSha is null || expectedOldSha is null)
        {
            return new TeamFastForwardResult(
                TeamFastForwardStatus.Failed,
                $"Not a commit: {(newSha is null ? newCommit : expectedOldCommit)}");
        }

        var ancestry = await GitProcess.RunAsync(
            repoRoot, ["merge-base", "--is-ancestor", expectedOldSha, newSha], cancellationToken);
        if (ancestry.ExitCode == 1)
        {
            return new TeamFastForwardResult(
                TeamFastForwardStatus.NotFastForward,
                $"{Abbreviate(newSha)} doesn't contain {branchName}'s current commit {Abbreviate(expectedOldSha)}, " +
                $"so moving {branchName} there would drop work already merged. Merge {branchName} into your branch first.");
        }

        if (!ancestry.Success)
        {
            return new TeamFastForwardResult(TeamFastForwardStatus.Failed, ancestry.Output);
        }

        // update-ref fails at once if another update holds the ref's lock. Giving it a few seconds to
        // retry means two workers finishing at the same moment get the clean outcome (one wins, the
        // other sees the moved ref) rather than a spurious lock failure. The first setting covers the
        // default files backend and the second covers reftable; each backend ignores the other's.
        var update = await GitProcess.RunAsync(
            repoRoot,
            [
                "-c", "core.filesRefLockTimeout=3000", "-c", "reftable.lockTimeout=3000",
                "update-ref", "-m", "IDE++ Ultra: fast-forward", refName, newSha, expectedOldSha,
            ],
            cancellationToken);
        if (update.Success)
        {
            return new TeamFastForwardResult(
                TeamFastForwardStatus.FastForwarded,
                $"{branchName} moved from {Abbreviate(expectedOldSha)} to {Abbreviate(newSha)}.");
        }

        // update-ref's own message for a failed compare is localised, so check the ref directly to
        // tell "someone else got there first" from a real failure.
        var actualSha = await ResolveRefAsync(repoRoot, refName, cancellationToken);
        if (actualSha is null)
        {
            return new TeamFastForwardResult(TeamFastForwardStatus.Failed, $"{branchName} doesn't exist. {update.Output}");
        }

        if (!string.Equals(actualSha, expectedOldSha, StringComparison.OrdinalIgnoreCase))
        {
            return new TeamFastForwardResult(
                TeamFastForwardStatus.RefMoved,
                $"{branchName} moved to {Abbreviate(actualSha)} while you were merging: another merge got in first. " +
                $"Merge {branchName} into your branch again, then retry.");
        }

        return new TeamFastForwardResult(TeamFastForwardStatus.Failed, update.Output);
    }

    /// <summary>
    /// What changed between <paramref name="fromCommit"/> (the snapshot) and
    /// <paramref name="toRef"/> (the team branch), with one entry per path. Renames come back as a
    /// delete plus an add (--no-renames) because the landing step works file by file, and a rename
    /// pair would need both halves applied together. -z keeps paths containing spaces or non-ASCII
    /// characters unquoted.
    /// </summary>
    public async Task<IReadOnlyList<TeamFileChange>> GetChangedFilesAsync(
        string repoRoot, string fromCommit, string toRef, CancellationToken cancellationToken = default)
    {
        var output = await RunOrThrowAsync(
            repoRoot, ["diff", "--name-status", "--no-renames", "-z", fromCommit, toRef, "--"], cancellationToken);

        // -z output alternates status and path, each NUL-terminated: "M\0a.txt\0A\0b.txt\0".
        var fields = output.Split('\0');
        var changes = new List<TeamFileChange>();
        for (var i = 0; i + 1 < fields.Length && fields[i].Length > 0; i += 2)
        {
            var kind = fields[i][0] switch
            {
                'A' => TeamFileChangeKind.Added,
                'D' => TeamFileChangeKind.Deleted,
                _ => TeamFileChangeKind.Modified,
            };
            changes.Add(new TeamFileChange(fields[i + 1], kind));
        }

        return changes;
    }

    /// <summary>
    /// The file's content at <paramref name="revision"/> in working-tree form, or null when the file
    /// doesn't exist there. <paramref name="relativePath"/> is repo-root-relative; backslashes are
    /// fine and become the forward slashes git's rev:path syntax needs.
    ///
    /// This uses cat-file --filters rather than show. show returns the blob as stored, which with
    /// core.autocrlf=true (Git for Windows' default) is LF while the user's file on disk is CRLF, so
    /// compared or 3-way merged against their current file, every line would differ. --filters
    /// applies the same end-of-line conversion and smudge filters a checkout would, so the result
    /// lines up with what's on disk. A revision that doesn't exist throws rather than reading as
    /// "file missing", so a mistyped revision can't pass for every file having been deleted.
    /// </summary>
    public async Task<string?> GetFileAtAsync(
        string repoRoot, string revision, string relativePath, CancellationToken cancellationToken = default)
    {
        var objectName = $"{revision}:{relativePath.Replace('\\', '/')}";
        var result = await GitProcess.RunAsync(repoRoot, ["cat-file", "--filters", objectName], cancellationToken);
        if (result.Success)
        {
            return result.StdOut;
        }

        var revisionExists = await GitProcess.RunAsync(
            repoRoot, ["rev-parse", "--verify", "--quiet", $"{revision}^{{tree}}"], cancellationToken);
        return revisionExists.Success
            ? null
            : throw new InvalidOperationException($"'{revision}' is not a revision in {repoRoot}. {result.Output}");
    }

    /// <summary>
    /// Three-way merges text the way git merges a file: <paramref name="current"/> (the user's file
    /// now) and <paramref name="theirs"/> (the team's version) against their common ancestor
    /// <paramref name="baseContent"/> (the snapshot). Conflicting hunks come back between markers
    /// labelled "yours" and "team" (plus "snapshot" under the diff3 conflict style), so a reviewer
    /// can tell whose side is whose.
    ///
    /// git merge-file's exit code is the number of conflicts, capped at 127. Any other non-zero code
    /// (binary input, git missing) is a real failure, so it throws rather than passing broken output
    /// off as a merge.
    /// </summary>
    public async Task<(string Merged, bool HasConflicts)> MergeFileContentsAsync(
        string current, string baseContent, string theirs, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ideplusplus-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var currentPath = Path.Combine(directory, "yours");
            var basePath = Path.Combine(directory, "snapshot");
            var theirsPath = Path.Combine(directory, "team");

            // Without a BOM, so git sees exactly the text it was given; the output is decoded as
            // UTF-8 on the way back in.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(currentPath, current, utf8, cancellationToken);
            await File.WriteAllTextAsync(basePath, baseContent, utf8, cancellationToken);
            await File.WriteAllTextAsync(theirsPath, theirs, utf8, cancellationToken);

            var result = await GitProcess.RunAsync(
                directory,
                ["merge-file", "-p", "-L", "yours", "-L", "snapshot", "-L", "team", currentPath, basePath, theirsPath],
                cancellationToken);

            return result.ExitCode switch
            {
                0 => (result.StdOut, false),
                >= 1 and <= 127 => (result.StdOut, true),
                _ => throw new InvalidOperationException($"git merge-file failed (exit code {result.ExitCode}): {result.StdErr}"),
            };
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a few stray bytes in %TEMP% aren't worth failing a merge over.
            }
        }
    }

    /// <summary>
    /// Removes a worker's worktree, uncommitted changes and all (--force), then prunes git's records
    /// of any worktree whose directory has already gone. Success means the directory no longer
    /// exists afterwards, which makes cleanup idempotent: removing a worktree that the user or a crash
    /// already deleted still succeeds, and the run doesn't get stuck in cleanup.
    /// </summary>
    public async Task<GitCommandResult> RemoveWorktreeAsync(
        string repoRoot, string worktreeDirectory, CancellationToken cancellationToken = default)
    {
        var remove = await GitProcess.RunAsync(
            repoRoot, ["worktree", "remove", "--force", worktreeDirectory], cancellationToken);
        var prune = await GitProcess.RunAsync(repoRoot, ["worktree", "prune"], cancellationToken);

        var output = string.Join("\n", new[] { remove.Output, prune.Output }.Where(s => s.Length > 0));
        return new GitCommandResult(remove.Success || !Directory.Exists(worktreeDirectory), output);
    }

    /// <summary>Force-deletes <paramref name="branchName"/> (branch -D). Worker branches are
    /// usually not merged into whatever the user has checked out, and plain -d would refuse. This
    /// fails while a worktree still has the branch checked out, so remove the worktree first.</summary>
    public Task<GitCommandResult> DeleteBranchAsync(
        string repoRoot, string branchName, CancellationToken cancellationToken = default) =>
        RunAsync(repoRoot, ["branch", "-D", branchName], cancellationToken);

    private static async Task<IReadOnlyList<string>> GetConflictedPathsAsync(
        string worktreeDirectory, CancellationToken cancellationToken)
    {
        var result = await GitProcess.RunAsync(
            worktreeDirectory, ["diff", "--name-only", "--diff-filter=U", "-z"], cancellationToken);
        return result.Success
            ? result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList()
            : [];
    }

    private static async Task<IReadOnlyList<string>> FindFilesWithConflictMarkersAsync(
        string worktreeDirectory, CancellationToken cancellationToken)
    {
        var withMarkers = new List<string>();
        foreach (var relativePath in await GetConflictedPathsAsync(worktreeDirectory, cancellationToken))
        {
            var fullPath = Path.Combine(worktreeDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath) && ConflictMarkerPattern().IsMatch(await File.ReadAllTextAsync(fullPath, cancellationToken)))
            {
                withMarkers.Add(relativePath);
            }
        }

        return withMarkers;
    }

    // Git's own markers: seven '<' or '>' at the start of a line, then the side's label.
    [GeneratedRegex(@"^(?:<{7}|>{7})(?: |\r?$)", RegexOptions.Multiline)]
    private static partial Regex ConflictMarkerPattern();

    // -c applies to this one git invocation only, so nothing is written to the user's config.
    // commit.gpgsign=false because these commits never leave the machine as they are (the user
    // signs the real commit when they commit the landed changes), and a signing setup that wants a
    // pinentry would otherwise hang or fail every worker. commit-tree honours commit.gpgsign too.
    private static string[] CommitConfigArguments(string name, string email) =>
        ["-c", $"user.name={name}", "-c", $"user.email={email}", "-c", "commit.gpgsign=false"];

    // "Worker 2" becomes "worker-2@ideplusplus.local": a valid address that stays the same for each
    // worker, so the log and blame attribute every commit without anyone's real identity.
    private static string ToWorkerEmail(string authorName)
    {
        var slug = new StringBuilder();
        foreach (var character in authorName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var local = slug.ToString().TrimEnd('-');
        return $"{(local.Length > 0 ? local : "worker")}@{WorkerEmailDomain}";
    }

    private static string Abbreviate(string sha) => sha.Length > 10 ? sha[..10] : sha;

    private static async Task<GitCommandResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await GitProcess.RunAsync(workingDirectory, arguments, cancellationToken);
        return new GitCommandResult(result.Success, result.Output);
    }

    private static async Task<string> RunOrThrowAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await GitProcess.RunAsync(workingDirectory, arguments, cancellationToken, environment);
        return result.Success
            ? result.StdOut
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Output}");
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp index is harmless; it isn't worth failing a snapshot over.
        }
    }
}
