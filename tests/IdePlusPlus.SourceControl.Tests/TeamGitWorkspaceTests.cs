using System.Diagnostics;
using System.Text;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.SourceControl.Tests;

/// <summary>
/// Exercises TeamGitWorkspace against real scratch repositories and worktrees, like GitServiceTests:
/// what matters is how the actual git binary behaves - an index left alone, a ref that really is
/// compare-and-swapped, a conflict really left in progress - so that's what gets verified.
/// </summary>
public sealed class TeamGitWorkspaceTests : IDisposable
{
    private const string RunId = "run1";

    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-team-{Guid.NewGuid()}");
    private readonly string _repositoryDirectory;
    private readonly TeamGitWorkspace _workspace = new();

    public TeamGitWorkspaceTests()
    {
        _repositoryDirectory = Path.Combine(_rootDirectory, "repo");
        Directory.CreateDirectory(_repositoryDirectory);
        RunGit("init", "--initial-branch=main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test User");
        RunGit("config", "commit.gpgsign", "false");
        // Git for Windows sets core.autocrlf=true system-wide; pin it off so content assertions don't
        // depend on the machine. The line-ending test turns it back on deliberately.
        RunGit("config", "core.autocrlf", "false");
    }

    private static string TeamBranch => TeamGitWorkspace.GetTeamBranchName(RunId);

    public void Dispose()
    {
        try
        {
            // git marks object files read-only on Windows - Directory.Delete can't remove those
            // until the attribute is cleared first.
            foreach (var path in Directory.EnumerateFileSystemEntries(_rootDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - a lingering file handle on Windows shouldn't fail the test run.
        }
    }

    // --- Finding the repository ---

    [Fact]
    public async Task FindRepositoryRootAsync_FromASubdirectory_ReturnsTheRootAsAWindowsPath()
    {
        var subdirectory = Directory.CreateDirectory(Path.Combine(_repositoryDirectory, "src", "deep")).FullName;

        var root = await _workspace.FindRepositoryRootAsync(subdirectory);

        root.ShouldNotBeNull();
        root.ShouldNotContain("/");
        root.ShouldBe(_repositoryDirectory, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public async Task FindRepositoryRootAsync_OutsideARepository_ReturnsNull()
    {
        var plainDirectory = Directory.CreateDirectory(Path.Combine(_rootDirectory, "plain")).FullName;

        (await _workspace.FindRepositoryRootAsync(plainDirectory)).ShouldBeNull();
        (await _workspace.FindRepositoryRootAsync(Path.Combine(_rootDirectory, "does-not-exist"))).ShouldBeNull();
    }

    // --- Snapshot ---

    [Fact]
    public async Task CreateSnapshotCommitAsync_CapturesTheWorkingTreeAndLeavesTheUsersRepositoryUntouched()
    {
        WriteFile(".gitignore", "*.log\n");
        WriteFile("tracked.txt", "committed");
        WriteFile("deleted.txt", "about to be deleted");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Initial commit");
        WriteFile("tracked.txt", "modified, not staged");
        WriteFile("staged.txt", "staged only");
        RunGit("add", "staged.txt");
        WriteFile("untracked.txt", "untracked");
        WriteFile("build.log", "ignored");
        File.Delete(Path.Combine(_repositoryDirectory, "deleted.txt"));

        var statusBefore = RunGit("status", "--porcelain");
        var indexBefore = RunGit("ls-files", "--stage");
        var headBefore = RunGit("rev-parse", "HEAD").Trim();
        var refsBefore = RunGit("for-each-ref");

        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);

        RunGit("status", "--porcelain").ShouldBe(statusBefore);
        RunGit("ls-files", "--stage").ShouldBe(indexBefore);
        RunGit("rev-parse", "HEAD").Trim().ShouldBe(headBefore);
        RunGit("for-each-ref").ShouldBe(refsBefore);
        ReadFile("tracked.txt").ShouldBe("modified, not staged");

        RunGit("rev-parse", $"{snapshot}^").Trim().ShouldBe(headBefore);
        TreeFiles(snapshot).ShouldBe([".gitignore", "staged.txt", "tracked.txt", "untracked.txt"], ignoreOrder: true);
        RunGit("show", $"{snapshot}:tracked.txt").ShouldBe("modified, not staged");
        RunGit("log", "-1", "--format=%an <%ae>|%s", snapshot).Trim()
            .ShouldBe("IDE++ Ultra <ultra@ideplusplus.local>|IDE++ Ultra snapshot");
    }

    [Fact]
    public async Task CreateSnapshotCommitAsync_InARepositoryWithNoCommits_CreatesAParentlessCommit()
    {
        WriteFile("first.txt", "first");

        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);

        RunGit("rev-list", "--parents", "-n", "1", snapshot).Trim().ShouldBe(snapshot);
        TreeFiles(snapshot).ShouldBe(["first.txt"]);
        RunGit("status", "--porcelain").Trim().ShouldBe("?? first.txt");
    }

    [Fact]
    public async Task CreateSnapshotCommitAsync_LeavesOutTheIdesOwnUntrackedStateFolders()
    {
        WriteFile("code.cs", "class A { }");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Initial commit");
        Directory.CreateDirectory(Path.Combine(_repositoryDirectory, ".ideplusplus", "copilot"));
        Directory.CreateDirectory(Path.Combine(_repositoryDirectory, "src", "App", ".ideplusplus", "copilot"));
        WriteFile(".ideplusplus/copilot/chat-history.json", "[]");
        WriteFile("src/App/.ideplusplus/copilot/embeddings.json", "{}");
        WriteFile("new.cs", "class B { }");

        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);

        TreeFiles(snapshot).ShouldBe(["code.cs", "new.cs"], ignoreOrder: true);
    }

    // --- Starting a run from an earlier branch ---

    /// <summary>Commits <paramref name="content"/> to <paramref name="relativePath"/> on a new branch
    /// off HEAD, and puts the repository back on its original branch and contents.</summary>
    private void CommitOnBranch(string branch, string relativePath, string content)
    {
        RunGit("switch", "-c", branch);
        WriteFile(relativePath, content);
        RunGit("add", "-A");
        RunGit("commit", "-m", $"{branch} work");
        RunGit("switch", "main");
    }

    [Fact]
    public async Task MergeIntoSnapshotAsync_CombinesTheWorkingTreeWithTheBranch_WithoutTouchingAnything()
    {
        WriteFile("a.txt", "a");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Initial commit");
        CommitOnBranch("earlier", "earlier.txt", "from the earlier run");
        WriteFile("mine.txt", "uncommitted in the user's tree");
        var statusBefore = RunGit("status", "--porcelain");
        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);

        var result = await _workspace.MergeIntoSnapshotAsync(_repositoryDirectory, snapshot, "earlier");

        result.Status.ShouldBe(TeamStartMergeStatus.Merged);
        TreeFiles(result.Commit!).ShouldBe(["a.txt", "earlier.txt", "mine.txt"], ignoreOrder: true);
        RunGit("rev-list", "--parents", "-n", "1", result.Commit!).Trim().Split(' ').Length.ShouldBe(3);
        RunGit("status", "--porcelain").ShouldBe(statusBefore);
        RunGit("branch", "--show-current").Trim().ShouldBe("main");
    }

    [Fact]
    public async Task MergeIntoSnapshotAsync_WhenTheBranchIsAlreadyIn_StartsFromTheSnapshot()
    {
        WriteFile("a.txt", "a");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Initial commit");
        RunGit("branch", "same");
        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);

        var result = await _workspace.MergeIntoSnapshotAsync(_repositoryDirectory, snapshot, "same");

        result.Status.ShouldBe(TeamStartMergeStatus.AlreadyContained);
        result.Commit.ShouldBe(snapshot);
    }

    [Fact]
    public async Task MergeIntoSnapshotAsync_OnAConflict_NamesTheFiles_AndCreatesNothing()
    {
        WriteFile("shared.txt", "original\n");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Initial commit");
        CommitOnBranch("earlier", "shared.txt", "the earlier run's version\n");
        WriteFile("shared.txt", "the user's own edit\n");
        var refsBefore = RunGit("for-each-ref");
        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);

        var result = await _workspace.MergeIntoSnapshotAsync(_repositoryDirectory, snapshot, "earlier");

        result.Status.ShouldBe(TeamStartMergeStatus.Conflicted);
        result.Commit.ShouldBeNull();
        result.ConflictedPaths.ShouldBe(["shared.txt"]);
        RunGit("for-each-ref").ShouldBe(refsBefore);
        ReadFile("shared.txt").ShouldBe("the user's own edit\n");
    }

    [Fact]
    public async Task MergeIntoSnapshotAsync_ForAnUnknownBranch_SaysSo()
    {
        WriteFile("a.txt", "a");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Initial commit");
        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => _workspace.MergeIntoSnapshotAsync(_repositoryDirectory, snapshot, "no-such-branch"));

        error.Message.ShouldContain("no-such-branch");
    }

    [Fact]
    public async Task ListTeamBranchesAsync_ListsOnlyTeamBranches_NewestFirst()
    {
        WriteFile("a.txt", "a");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Initial commit");
        var head = RunGit("rev-parse", "HEAD").Trim();
        RunGit("branch", TeamGitWorkspace.GetTeamBranchName("older"), head);
        RunGit("branch", TeamGitWorkspace.GetWorkerBranchName("older", 1), head);
        RunGit("branch", "unrelated", head);
        WriteFile("b.txt", "b");
        RunGit("add", "-A");
        // A later committer date, so newest-first is not a tie.
        RunGitWithEnvironment(["commit", "-m", "Later"], "GIT_COMMITTER_DATE", "2099-01-01T00:00:00");
        RunGit("branch", TeamGitWorkspace.GetTeamBranchName("newer"));

        var branches = await _workspace.ListTeamBranchesAsync(_repositoryDirectory);

        branches.ShouldBe([TeamGitWorkspace.GetTeamBranchName("newer"), TeamGitWorkspace.GetTeamBranchName("older")]);
    }

    // --- Worktrees, commits and merges between workers ---

    [Fact]
    public async Task TwoWorkers_CommitAndMergeThroughTheTeamBranch()
    {
        var (snapshot, w1, w2) = await SetUpTeamAsync();

        File.WriteAllText(Path.Combine(w1, "a.txt"), "from w1");
        var w1Commit = await _workspace.CommitAllAsync(w1, "Add a.txt", "Worker 1");
        w1Commit.Status.ShouldBe(TeamCommitStatus.Committed, w1Commit.Message);
        (await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, w1Commit.Commit!, snapshot))
            .Status.ShouldBe(TeamFastForwardStatus.FastForwarded);

        File.WriteAllText(Path.Combine(w2, "b.txt"), "from w2");
        var w2Commit = await _workspace.CommitAllAsync(w2, "Add b.txt", "Worker 2");
        w2Commit.Status.ShouldBe(TeamCommitStatus.Committed, w2Commit.Message);

        var sync = await _workspace.MergeBranchIntoWorktreeAsync(w2, TeamBranch, "Worker 2");

        sync.Status.ShouldBe(TeamSyncStatus.Merged, sync.Message);
        sync.ConflictedPaths.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(w2, "a.txt")).ShouldBe("from w1");
        File.ReadAllText(Path.Combine(w2, "b.txt")).ShouldBe("from w2");

        var w2Head = await _workspace.GetHeadAsync(w2);
        (await _workspace.IsAncestorAsync(_repositoryDirectory, TeamBranch, w2Head)).ShouldBeTrue();
        (await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, w2Head, w1Commit.Commit!))
            .Status.ShouldBe(TeamFastForwardStatus.FastForwarded);
        (await _workspace.ResolveRefAsync(_repositoryDirectory, TeamBranch)).ShouldBe(w2Head);

        (await _workspace.MergeBranchIntoWorktreeAsync(w2, TeamBranch, "Worker 2"))
            .Status.ShouldBe(TeamSyncStatus.UpToDate);
    }

    [Fact]
    public async Task AddWorktreeAsync_ChecksOutTheStartCommitOnANewBranch_WithoutTouchingTheMainCheckout()
    {
        CommitFile("hello.txt", "base");
        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);
        var worktree = WorktreePath("w1");

        var result = await _workspace.AddWorktreeAsync(
            _repositoryDirectory, worktree, TeamGitWorkspace.GetWorkerBranchName(RunId, 1), snapshot);

        result.Success.ShouldBeTrue(result.Output);
        File.ReadAllText(Path.Combine(worktree, "hello.txt")).ShouldBe("base");
        (await _workspace.GetHeadAsync(worktree)).ShouldBe(snapshot);
        RunGitIn(worktree, "branch", "--show-current").Trim().ShouldBe("ideplusplus/ultra/run1/w1");
        RunGit("branch", "--show-current").Trim().ShouldBe("main");
        RunGit("status", "--porcelain").ShouldBeEmpty();
    }

    /// <summary>The naming scheme matters: git won't create a ref that is a path prefix of another
    /// (e.g. "ultra/run1" and "ultra/run1/w1"), so the team and worker branches must coexist.</summary>
    [Fact]
    public async Task TeamAndWorkerBranchNames_CanAllExistTogether()
    {
        CommitFile("hello.txt", "base");
        var head = RunGit("rev-parse", "HEAD").Trim();

        (await _workspace.CreateBranchAsync(_repositoryDirectory, TeamBranch, head)).Success.ShouldBeTrue();
        foreach (var worker in new[] { 1, 2, 10 })
        {
            var result = await _workspace.CreateBranchAsync(
                _repositoryDirectory, TeamGitWorkspace.GetWorkerBranchName(RunId, worker), head);
            result.Success.ShouldBeTrue(result.Output);
        }
    }

    [Fact]
    public async Task CommitAllAsync_CommitsNewModifiedAndDeletedFilesUnderTheWorkersIdentity()
    {
        CommitFile("keep.txt", "keep");
        CommitFile("remove.txt", "remove");
        var worktree = await AddWorkerAsync(1);
        File.WriteAllText(Path.Combine(worktree, "keep.txt"), "changed");
        File.Delete(Path.Combine(worktree, "remove.txt"));
        File.WriteAllText(Path.Combine(worktree, "new.txt"), "new");

        var result = await _workspace.CommitAllAsync(worktree, "Worker change", "Worker 2");

        result.Status.ShouldBe(TeamCommitStatus.Committed, result.Message);
        result.Commit.ShouldBe(await _workspace.GetHeadAsync(worktree));
        RunGitIn(worktree, "log", "-1", "--format=%an|%ae|%cn|%s").Trim()
            .ShouldBe("Worker 2|worker-2@ideplusplus.local|Worker 2|Worker change");
        TreeFiles(result.Commit!).ShouldBe(["keep.txt", "new.txt"], ignoreOrder: true);
        (await _workspace.HasUncommittedChangesAsync(worktree)).ShouldBeFalse();
    }

    [Fact]
    public async Task CommitAllAsync_WithNothingChanged_ReportsNothingToCommitAndTheUnchangedHead()
    {
        CommitFile("hello.txt", "base");
        var worktree = await AddWorkerAsync(1);
        var head = await _workspace.GetHeadAsync(worktree);

        var result = await _workspace.CommitAllAsync(worktree, "Nothing", "Worker 1");

        result.Status.ShouldBe(TeamCommitStatus.NothingToCommit);
        result.Commit.ShouldBe(head);
        (await _workspace.GetHeadAsync(worktree)).ShouldBe(head);
    }

    [Fact]
    public async Task HasUncommittedChangesAsync_CountsUntrackedAndModifiedFiles()
    {
        CommitFile("hello.txt", "base");
        var worktree = await AddWorkerAsync(1);

        (await _workspace.HasUncommittedChangesAsync(worktree)).ShouldBeFalse();

        File.WriteAllText(Path.Combine(worktree, "untracked.txt"), "new");
        (await _workspace.HasUncommittedChangesAsync(worktree)).ShouldBeTrue();

        File.Delete(Path.Combine(worktree, "untracked.txt"));
        File.WriteAllText(Path.Combine(worktree, "hello.txt"), "modified");
        (await _workspace.HasUncommittedChangesAsync(worktree)).ShouldBeTrue();
    }

    [Fact]
    public async Task MergeBranchIntoWorktreeAsync_WithAConflict_ReportsThePathsAndLeavesTheMergeForTheWorkerToResolve()
    {
        var (snapshot, w1, w2) = await SetUpTeamAsync();
        File.WriteAllText(Path.Combine(w1, "hello.txt"), "line 1\nfrom w1\nline 3\n");
        var w1Commit = await _workspace.CommitAllAsync(w1, "w1 edits line 2", "Worker 1");
        await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, w1Commit.Commit!, snapshot);
        File.WriteAllText(Path.Combine(w2, "hello.txt"), "line 1\nfrom w2\nline 3\n");
        await _workspace.CommitAllAsync(w2, "w2 edits line 2", "Worker 2");

        var sync = await _workspace.MergeBranchIntoWorktreeAsync(w2, TeamBranch, "Worker 2");

        sync.Status.ShouldBe(TeamSyncStatus.Conflicted, sync.Message);
        sync.ConflictedPaths.ShouldBe(["hello.txt"]);
        var conflicted = File.ReadAllText(Path.Combine(w2, "hello.txt"));
        conflicted.ShouldContain("<<<<<<<");
        conflicted.ShouldContain("from w1");
        conflicted.ShouldContain("from w2");
        GitExitCodeIn(w2, "rev-parse", "--verify", "--quiet", "MERGE_HEAD").ShouldBe(0, "the merge must not be aborted");

        // Committing with the markers still in place is refused rather than baked into history...
        var refused = await _workspace.CommitAllAsync(w2, "Merge team", "Worker 2");
        refused.Status.ShouldBe(TeamCommitStatus.Failed);
        refused.Message.ShouldContain("hello.txt");

        // ...and once resolved, the commit concludes the merge and can fast-forward the team branch.
        File.WriteAllText(Path.Combine(w2, "hello.txt"), "line 1\nfrom w1 and w2\nline 3\n");
        var resolved = await _workspace.CommitAllAsync(w2, "Merge team", "Worker 2");
        resolved.Status.ShouldBe(TeamCommitStatus.Committed, resolved.Message);
        GitExitCodeIn(w2, "rev-parse", "--verify", "--quiet", "MERGE_HEAD").ShouldNotBe(0);
        (await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, resolved.Commit!, w1Commit.Commit!))
            .Status.ShouldBe(TeamFastForwardStatus.FastForwarded);
    }

    /// <summary>Resolving a conflict by taking the worktree's own side leaves the tree identical to
    /// HEAD; the commit is still needed to conclude the merge, so it must not read as "nothing to
    /// commit".</summary>
    [Fact]
    public async Task CommitAllAsync_ConcludesAMergeEvenWhenTheResolutionMatchesHead()
    {
        var (snapshot, w1, w2) = await SetUpTeamAsync();
        File.WriteAllText(Path.Combine(w1, "hello.txt"), "line 1\nfrom w1\nline 3\n");
        var w1Commit = await _workspace.CommitAllAsync(w1, "w1", "Worker 1");
        await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, w1Commit.Commit!, snapshot);
        File.WriteAllText(Path.Combine(w2, "hello.txt"), "line 1\nfrom w2\nline 3\n");
        await _workspace.CommitAllAsync(w2, "w2", "Worker 2");
        await _workspace.MergeBranchIntoWorktreeAsync(w2, TeamBranch, "Worker 2");

        File.WriteAllText(Path.Combine(w2, "hello.txt"), "line 1\nfrom w2\nline 3\n");
        var result = await _workspace.CommitAllAsync(w2, "Keep w2's version", "Worker 2");

        result.Status.ShouldBe(TeamCommitStatus.Committed, result.Message);
        (await _workspace.IsAncestorAsync(_repositoryDirectory, TeamBranch, result.Commit!)).ShouldBeTrue();
    }

    [Fact]
    public async Task MergeBranchIntoWorktreeAsync_WhenUncommittedChangesWouldBeOverwritten_FailsWithoutConflicts()
    {
        var (snapshot, w1, w2) = await SetUpTeamAsync();
        File.WriteAllText(Path.Combine(w1, "hello.txt"), "changed by w1\n");
        var w1Commit = await _workspace.CommitAllAsync(w1, "w1", "Worker 1");
        await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, w1Commit.Commit!, snapshot);
        File.WriteAllText(Path.Combine(w2, "hello.txt"), "uncommitted in w2\n");

        var sync = await _workspace.MergeBranchIntoWorktreeAsync(w2, TeamBranch, "Worker 2");

        sync.Status.ShouldBe(TeamSyncStatus.Failed);
        sync.ConflictedPaths.ShouldBeEmpty();
        sync.Message.ShouldNotBeEmpty();
        File.ReadAllText(Path.Combine(w2, "hello.txt")).ShouldBe("uncommitted in w2\n");
    }

    // --- Compare-and-swap fast-forward ---

    [Fact]
    public async Task FastForwardBranchAsync_TwoConcurrentMergesFromTheSameOldCommit_ExactlyOneWins()
    {
        var (snapshot, w1, w2) = await SetUpTeamAsync();
        File.WriteAllText(Path.Combine(w1, "a.txt"), "from w1");
        var first = (await _workspace.CommitAllAsync(w1, "w1", "Worker 1")).Commit!;
        File.WriteAllText(Path.Combine(w2, "b.txt"), "from w2");
        var second = (await _workspace.CommitAllAsync(w2, "w2", "Worker 2")).Commit!;

        // Several rounds, each on a fresh branch at the snapshot, to give the race a real chance to
        // interleave both ways.
        for (var round = 0; round < 5; round++)
        {
            var branch = $"ideplusplus/ultra/race{round}/team";
            (await _workspace.CreateBranchAsync(_repositoryDirectory, branch, snapshot)).Success.ShouldBeTrue();

            var results = await Task.WhenAll(
                Task.Run(() => _workspace.FastForwardBranchAsync(_repositoryDirectory, branch, first, snapshot)),
                Task.Run(() => _workspace.FastForwardBranchAsync(_repositoryDirectory, branch, second, snapshot)));

            results.Count(r => r.Status == TeamFastForwardStatus.FastForwarded).ShouldBe(1, string.Join(" / ", results.Select(r => r.Message)));
            results.Count(r => r.Status == TeamFastForwardStatus.RefMoved).ShouldBe(1, string.Join(" / ", results.Select(r => r.Message)));
            var winner = results[0].Status == TeamFastForwardStatus.FastForwarded ? first : second;
            (await _workspace.ResolveRefAsync(_repositoryDirectory, branch)).ShouldBe(winner);
        }
    }

    [Fact]
    public async Task FastForwardBranchAsync_WhenTheBranchMovedSinceItWasRead_ReturnsRefMovedAndChangesNothing()
    {
        var (snapshot, w1, w2) = await SetUpTeamAsync();
        File.WriteAllText(Path.Combine(w1, "a.txt"), "from w1");
        var first = (await _workspace.CommitAllAsync(w1, "w1", "Worker 1")).Commit!;
        File.WriteAllText(Path.Combine(w2, "b.txt"), "from w2");
        var second = (await _workspace.CommitAllAsync(w2, "w2", "Worker 2")).Commit!;
        await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, first, snapshot);

        // w2 still believes the team branch is at the snapshot.
        var result = await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, second, snapshot);

        result.Status.ShouldBe(TeamFastForwardStatus.RefMoved);
        (await _workspace.ResolveRefAsync(_repositoryDirectory, TeamBranch)).ShouldBe(first);
    }

    [Fact]
    public async Task FastForwardBranchAsync_ToACommitThatDoesNotContainTheBranch_IsRefusedAsNotFastForward()
    {
        var (snapshot, w1, w2) = await SetUpTeamAsync();
        File.WriteAllText(Path.Combine(w1, "a.txt"), "from w1");
        var first = (await _workspace.CommitAllAsync(w1, "w1", "Worker 1")).Commit!;
        File.WriteAllText(Path.Combine(w2, "b.txt"), "from w2");
        var second = (await _workspace.CommitAllAsync(w2, "w2", "Worker 2")).Commit!;
        await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, first, snapshot);

        // w2 read the branch correctly but never synced, so its commit would drop w1's work.
        var result = await _workspace.FastForwardBranchAsync(_repositoryDirectory, TeamBranch, second, first);

        result.Status.ShouldBe(TeamFastForwardStatus.NotFastForward);
        (await _workspace.ResolveRefAsync(_repositoryDirectory, TeamBranch)).ShouldBe(first);
        (await _workspace.IsAncestorAsync(_repositoryDirectory, first, second)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsAncestorAsync_ForAnUnknownCommit_Throws()
    {
        CommitFile("hello.txt", "base");

        await Should.ThrowAsync<InvalidOperationException>(
            () => _workspace.IsAncestorAsync(_repositoryDirectory, "HEAD", "0123456789abcdef0123456789abcdef01234567"));
    }

    [Fact]
    public async Task ResolveRefAsync_ForAMissingBranch_ReturnsNull()
    {
        CommitFile("hello.txt", "base");

        (await _workspace.ResolveRefAsync(_repositoryDirectory, "refs/heads/no-such-branch")).ShouldBeNull();
        (await _workspace.ResolveRefAsync(_repositoryDirectory, "main")).ShouldBe(await _workspace.GetHeadAsync(_repositoryDirectory));
    }

    // --- Reading the result back ---

    [Fact]
    public async Task GetChangedFilesAsync_ReportsAddedModifiedAndDeletedFiles()
    {
        CommitFile("unchanged.txt", "same");
        CommitFile("modified.txt", "before");
        CommitFile("deleted.txt", "doomed");
        var from = RunGit("rev-parse", "HEAD").Trim();
        WriteFile("modified.txt", "after");
        File.Delete(Path.Combine(_repositoryDirectory, "deleted.txt"));
        Directory.CreateDirectory(Path.Combine(_repositoryDirectory, "sub dir"));
        WriteFile("sub dir/added é.txt", "new");
        RunGit("add", "-A");
        RunGit("commit", "-m", "Changes");

        var changes = await _workspace.GetChangedFilesAsync(_repositoryDirectory, from, "main");

        changes.ShouldBe(
            [
                new TeamFileChange("deleted.txt", TeamFileChangeKind.Deleted),
                new TeamFileChange("modified.txt", TeamFileChangeKind.Modified),
                new TeamFileChange("sub dir/added é.txt", TeamFileChangeKind.Added),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task GetChangedFilesAsync_ReportsARenameAsADeleteAndAnAdd()
    {
        CommitFile("old-name.txt", "content that stays the same across the rename\n");
        var from = RunGit("rev-parse", "HEAD").Trim();
        RunGit("mv", "old-name.txt", "new-name.txt");
        RunGit("commit", "-m", "Rename");

        var changes = await _workspace.GetChangedFilesAsync(_repositoryDirectory, from, "HEAD");

        changes.ShouldBe(
            [
                new TeamFileChange("new-name.txt", TeamFileChangeKind.Added),
                new TeamFileChange("old-name.txt", TeamFileChangeKind.Deleted),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task GetFileAtAsync_ReturnsContentAtTheRevision_AndNullForAMissingFile()
    {
        Directory.CreateDirectory(Path.Combine(_repositoryDirectory, "src"));
        CommitFile("src/code.txt", "version 1");
        var first = RunGit("rev-parse", "HEAD").Trim();
        CommitFile("src/code.txt", "version 2");

        (await _workspace.GetFileAtAsync(_repositoryDirectory, first, "src/code.txt")).ShouldBe("version 1");
        (await _workspace.GetFileAtAsync(_repositoryDirectory, "HEAD", @"src\code.txt")).ShouldBe("version 2");
        (await _workspace.GetFileAtAsync(_repositoryDirectory, "HEAD", "src/missing.txt")).ShouldBeNull();
    }

    [Fact]
    public async Task GetFileAtAsync_ForARevisionThatDoesNotExist_Throws()
    {
        CommitFile("hello.txt", "base");

        await Should.ThrowAsync<InvalidOperationException>(
            () => _workspace.GetFileAtAsync(_repositoryDirectory, "no-such-revision", "hello.txt"));
    }

    [Fact]
    public async Task GetFileAtAsync_PreservesNonAsciiText()
    {
        const string content = "café – naïve ✓ 日本語\n";
        File.WriteAllText(Path.Combine(_repositoryDirectory, "unicode.txt"), content, new UTF8Encoding(false));
        RunGit("add", "unicode.txt");
        RunGit("commit", "-m", "Unicode");

        (await _workspace.GetFileAtAsync(_repositoryDirectory, "HEAD", "unicode.txt")).ShouldBe(content);
    }

    /// <summary>With core.autocrlf=true the blob is stored with LF but the file on disk has CRLF;
    /// the content must come back in its on-disk form or it can never match the user's file.</summary>
    [Fact]
    public async Task GetFileAtAsync_WithAutoCrlf_ReturnsTheWorkingTreeLineEndings()
    {
        RunGit("config", "core.autocrlf", "true");
        WriteFile("crlf.txt", "first\r\nsecond\r\n");
        RunGit("add", "crlf.txt");
        RunGit("commit", "-m", "CRLF file");
        RunGit("show", "HEAD:crlf.txt").ShouldBe("first\nsecond\n", "precondition: the blob itself is stored with LF");

        (await _workspace.GetFileAtAsync(_repositoryDirectory, "HEAD", "crlf.txt")).ShouldBe("first\r\nsecond\r\n");
    }

    [Fact]
    public async Task MergeFileContentsAsync_WithNonOverlappingChanges_MergesCleanly()
    {
        var (merged, hasConflicts) = await _workspace.MergeFileContentsAsync(
            current: "A\nb\nc\nd\ne\n",
            baseContent: "a\nb\nc\nd\ne\n",
            theirs: "a\nb\nc\nd\nE\n");

        hasConflicts.ShouldBeFalse();
        merged.ShouldBe("A\nb\nc\nd\nE\n");
    }

    [Fact]
    public async Task MergeFileContentsAsync_WithOverlappingChanges_ReturnsLabelledConflictMarkers()
    {
        var (merged, hasConflicts) = await _workspace.MergeFileContentsAsync(
            current: "a\nyour line\nc\n",
            baseContent: "a\nb\nc\n",
            theirs: "a\nteam line\nc\n");

        hasConflicts.ShouldBeTrue();
        merged.ShouldContain("<<<<<<< yours");
        merged.ShouldContain("your line");
        merged.ShouldContain("=======");
        merged.ShouldContain("team line");
        merged.ShouldContain(">>>>>>> team");
    }

    [Fact]
    public async Task MergeFileContentsAsync_WithBinaryInput_Throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _workspace.MergeFileContentsAsync("x\0y", "x", "z"));
    }

    // --- Cleanup ---

    [Fact]
    public async Task RemoveWorktreeAndDeleteBranch_CleanUpAWorkerCompletely()
    {
        CommitFile("hello.txt", "base");
        var worktree = await AddWorkerAsync(1);
        File.WriteAllText(Path.Combine(worktree, "uncommitted.txt"), "work in progress");
        var branch = TeamGitWorkspace.GetWorkerBranchName(RunId, 1);

        var removed = await _workspace.RemoveWorktreeAsync(_repositoryDirectory, worktree);
        var deleted = await _workspace.DeleteBranchAsync(_repositoryDirectory, branch);

        removed.Success.ShouldBeTrue(removed.Output);
        deleted.Success.ShouldBeTrue(deleted.Output);
        Directory.Exists(worktree).ShouldBeFalse();
        RunGit("worktree", "list", "--porcelain").ShouldNotContain("w1");
        (await _workspace.ResolveRefAsync(_repositoryDirectory, $"refs/heads/{branch}")).ShouldBeNull();
    }

    [Fact]
    public async Task RemoveWorktreeAsync_WhenTheDirectoryIsAlreadyGone_StillSucceedsAndPrunes()
    {
        CommitFile("hello.txt", "base");
        var worktree = await AddWorkerAsync(1);
        Directory.Delete(worktree, recursive: true);

        var removed = await _workspace.RemoveWorktreeAsync(_repositoryDirectory, worktree);

        removed.Success.ShouldBeTrue(removed.Output);
        RunGit("worktree", "list", "--porcelain").ShouldNotContain("w1");
        (await _workspace.DeleteBranchAsync(_repositoryDirectory, TeamGitWorkspace.GetWorkerBranchName(RunId, 1)))
            .Success.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteBranchAsync_WhileAWorktreeHasItCheckedOut_Fails()
    {
        CommitFile("hello.txt", "base");
        await AddWorkerAsync(1);

        var deleted = await _workspace.DeleteBranchAsync(_repositoryDirectory, TeamGitWorkspace.GetWorkerBranchName(RunId, 1));

        deleted.Success.ShouldBeFalse();
    }

    /// <summary>A snapshot of a three-line hello.txt, the team branch at it, and two worker worktrees
    /// - the state an Ultra run is in right after setup.</summary>
    private async Task<(string Snapshot, string W1, string W2)> SetUpTeamAsync()
    {
        CommitFile("hello.txt", "line 1\nline 2\nline 3\n");
        var snapshot = await _workspace.CreateSnapshotCommitAsync(_repositoryDirectory);
        var team = await _workspace.CreateBranchAsync(_repositoryDirectory, TeamBranch, snapshot);
        team.Success.ShouldBeTrue(team.Output);
        return (snapshot, await AddWorkerAsync(1, snapshot), await AddWorkerAsync(2, snapshot));
    }

    private async Task<string> AddWorkerAsync(int workerNumber, string? startCommit = null)
    {
        var worktree = WorktreePath($"w{workerNumber}");
        var result = await _workspace.AddWorktreeAsync(
            _repositoryDirectory,
            worktree,
            TeamGitWorkspace.GetWorkerBranchName(RunId, workerNumber),
            startCommit ?? await _workspace.GetHeadAsync(_repositoryDirectory));
        result.Success.ShouldBeTrue(result.Output);
        return worktree;
    }

    // Outside the repository, under a folder that doesn't exist yet - the same shape as the real
    // %LOCALAPPDATA%\IdePlusPlus\ultra\<runId>\w<k> layout.
    private string WorktreePath(string name) => Path.Combine(_rootDirectory, "ultra", RunId, name);

    private void CommitFile(string relativePath, string content)
    {
        WriteFile(relativePath, content);
        RunGit("add", "--", relativePath);
        RunGit("commit", "-m", $"Write {relativePath}");
    }

    private void WriteFile(string relativePath, string content) =>
        File.WriteAllText(Path.Combine(_repositoryDirectory, relativePath), content);

    private string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(_repositoryDirectory, relativePath));

    private string[] TreeFiles(string commit) =>
        RunGit("ls-tree", "-r", "-z", "--name-only", commit).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private string RunGit(params string[] arguments) => RunGitIn(_repositoryDirectory, arguments);

    private static string RunGitIn(string workingDirectory, params string[] arguments)
    {
        var (exitCode, output, error) = RunGitProcess(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
        }

        return output;
    }

    private static int GitExitCodeIn(string workingDirectory, params string[] arguments) =>
        RunGitProcess(workingDirectory, arguments).ExitCode;

    private void RunGitWithEnvironment(string[] arguments, string variable, string value)
    {
        var (exitCode, _, error) = RunGitProcess(_repositoryDirectory, arguments, (variable, value));
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }
    }

    private static (int ExitCode, string Output, string Error) RunGitProcess(
        string workingDirectory, string[] arguments, (string Name, string Value)? environment = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is { } variable)
        {
            startInfo.Environment[variable.Name] = variable.Value;
        }

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }
}
