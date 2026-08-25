using System.Diagnostics;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.SourceControl.Tests;

/// <summary>
/// Exercises GitService against a real, scratch git repository rather than mocking the process -
/// the whole point of shelling out to the actual git binary is that behavior matches what a real
/// git install does, so that's what's worth verifying here.
/// </summary>
public sealed class GitServiceTests : IDisposable
{
    private readonly string _repositoryDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-git-{Guid.NewGuid()}");
    private readonly GitService _gitService = new();

    public GitServiceTests()
    {
        Directory.CreateDirectory(_repositoryDirectory);
        RunGit("init", "--initial-branch=main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test User");
    }

    private readonly List<string> _extraDirectoriesToClean = [];

    public void Dispose()
    {
        foreach (var directory in new[] { _repositoryDirectory }.Concat(_extraDirectoriesToClean))
        {
            DeleteDirectoryBestEffort(directory);
        }
    }

    private static void DeleteDirectoryBestEffort(string directory)
    {
        try
        {
            // git marks object files under .git/objects read-only on Windows - Directory.Delete
            // can't remove those until the attribute is cleared first.
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a lingering file handle on Windows shouldn't fail the test run.
        }
    }

    [Fact]
    public async Task IsGitRepositoryAsync_OnAnInitializedRepo_ReturnsTrue()
    {
        (await _gitService.IsGitRepositoryAsync(_repositoryDirectory)).ShouldBeTrue();
    }

    [Fact]
    public async Task IsGitRepositoryAsync_OnAPlainDirectory_ReturnsFalse()
    {
        var plainDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-plain-{Guid.NewGuid()}");
        Directory.CreateDirectory(plainDirectory);
        try
        {
            (await _gitService.IsGitRepositoryAsync(plainDirectory)).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(plainDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetStatusAsync_OnAFreshRepo_ReportsUntrackedFile()
    {
        WriteFile("hello.txt", "hello");

        var status = await _gitService.GetStatusAsync(_repositoryDirectory);

        status.Branch.ShouldBe("main");
        var change = status.Unstaged.ShouldHaveSingleItem();
        change.Path.ShouldBe("hello.txt");
        change.Status.ShouldBe(GitFileStatus.Untracked);
    }

    [Fact]
    public async Task StageAsync_MovesFileFromUnstagedToStaged()
    {
        WriteFile("hello.txt", "hello");

        var result = await _gitService.StageAsync(_repositoryDirectory, "hello.txt");

        result.Success.ShouldBeTrue();
        var status = await _gitService.GetStatusAsync(_repositoryDirectory);
        status.Unstaged.ShouldBeEmpty();
        status.Staged.ShouldHaveSingleItem().Status.ShouldBe(GitFileStatus.Added);
    }

    [Fact]
    public async Task StageAllAsync_StagesEveryTrackedAndUntrackedChange()
    {
        CommitInitialFile();
        WriteFile("hello.txt", "modified content");
        WriteFile("new.txt", "brand new");

        var result = await _gitService.StageAllAsync(_repositoryDirectory);

        result.Success.ShouldBeTrue();
        var status = await _gitService.GetStatusAsync(_repositoryDirectory);
        status.Unstaged.ShouldBeEmpty();
        status.Staged.Select(c => c.Path).ShouldBe(["hello.txt", "new.txt"], ignoreOrder: true);
    }

    [Fact]
    public async Task UnstageAsync_MovesFileBackToUnstaged()
    {
        WriteFile("hello.txt", "hello");
        await _gitService.StageAsync(_repositoryDirectory, "hello.txt");

        var result = await _gitService.UnstageAsync(_repositoryDirectory, "hello.txt");

        result.Success.ShouldBeTrue();
        var status = await _gitService.GetStatusAsync(_repositoryDirectory);
        status.Staged.ShouldBeEmpty();
        status.Unstaged.ShouldHaveSingleItem().Status.ShouldBe(GitFileStatus.Untracked);
    }

    [Fact]
    public async Task CommitAsync_ClearsStatusAndIsReflectedInGetFileAtHead()
    {
        WriteFile("hello.txt", "hello world");
        await _gitService.StageAsync(_repositoryDirectory, "hello.txt");

        var result = await _gitService.CommitAsync(_repositoryDirectory, "Add hello.txt");

        result.Success.ShouldBeTrue();
        var status = await _gitService.GetStatusAsync(_repositoryDirectory);
        status.Staged.ShouldBeEmpty();
        status.Unstaged.ShouldBeEmpty();

        var headContent = await _gitService.GetFileAtHeadAsync(_repositoryDirectory, "hello.txt");
        headContent.ShouldBe("hello world");
    }

    [Fact]
    public async Task GetFileAtHeadAsync_ForAFileNotYetCommitted_ReturnsEmptyString()
    {
        WriteFile("new.txt", "brand new");

        var headContent = await _gitService.GetFileAtHeadAsync(_repositoryDirectory, "new.txt");

        headContent.ShouldBe("");
    }

    [Fact]
    public async Task CreateBranchAsync_SwitchesToANewBranch()
    {
        CommitInitialFile();

        var result = await _gitService.CreateBranchAsync(_repositoryDirectory, "feature-x");

        result.Success.ShouldBeTrue();
        var branches = await _gitService.GetBranchesAsync(_repositoryDirectory);
        branches.ShouldContain(b => b.Name == "feature-x" && b.IsCurrent);
        branches.ShouldContain(b => b.Name == "main" && !b.IsCurrent);
    }

    [Fact]
    public async Task CheckoutBranchAsync_SwitchesBackToAnExistingBranch()
    {
        CommitInitialFile();
        await _gitService.CreateBranchAsync(_repositoryDirectory, "feature-x");

        var result = await _gitService.CheckoutBranchAsync(_repositoryDirectory, "main");

        result.Success.ShouldBeTrue();
        var branches = await _gitService.GetBranchesAsync(_repositoryDirectory);
        branches.ShouldContain(b => b.Name == "main" && b.IsCurrent);
    }

    [Fact]
    public async Task DiscardChangesAsync_RevertsAModifiedTrackedFileToItsCommittedContent()
    {
        CommitInitialFile();
        WriteFile("hello.txt", "modified content");

        var result = await _gitService.DiscardChangesAsync(_repositoryDirectory, "hello.txt");

        result.Success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_repositoryDirectory, "hello.txt")).ShouldBe("committed content");
    }

    /// <summary>
    /// Reproduces the out-of-the-box state on a machine that has never set pull.rebase/pull.ff:
    /// with nothing configured, git 2.29+ refuses a pull against diverged branches outright
    /// ("fatal: Need to specify how to reconcile divergent branches") instead of pulling anything.
    /// PullAsync must pin a reconcile strategy itself rather than depend on ambient global config.
    /// </summary>
    [Fact]
    public async Task PullAsync_WithDivergentNonConflictingHistory_MergesWithoutNeedingGlobalReconcileConfig()
    {
        var remoteDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-remote-{Guid.NewGuid()}");
        Directory.CreateDirectory(remoteDirectory);
        _extraDirectoriesToClean.Add(remoteDirectory);
        RunGitIn(remoteDirectory, "init", "--bare", "--initial-branch=main");

        RunGit("remote", "add", "origin", remoteDirectory);
        CommitInitialFile();
        RunGit("push", "-u", "origin", "main");

        var cloneDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-clone-{Guid.NewGuid()}");
        _extraDirectoriesToClean.Add(cloneDirectory);
        RunGitIn(Path.GetTempPath(), "clone", "--quiet", remoteDirectory, cloneDirectory);
        RunGitIn(cloneDirectory, "config", "user.email", "clone@example.com");
        RunGitIn(cloneDirectory, "config", "user.name", "Clone User");
        File.WriteAllText(Path.Combine(cloneDirectory, "from-clone.txt"), "from clone");
        RunGitIn(cloneDirectory, "add", "from-clone.txt");
        RunGitIn(cloneDirectory, "commit", "-m", "from clone");
        RunGitIn(cloneDirectory, "push");

        // The original repo now has a local, unpushed commit that diverges from what the clone
        // just pushed - the exact shape that needs an explicit reconcile strategy to pull.
        WriteFile("from-original.txt", "from original");
        RunGit("add", "from-original.txt");
        RunGit("commit", "-m", "from original");

        var result = await _gitService.PullAsync(_repositoryDirectory);

        result.Success.ShouldBeTrue(result.Output);
        File.Exists(Path.Combine(_repositoryDirectory, "from-clone.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_repositoryDirectory, "from-original.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task CommitAsync_WithAMessageContainingShellMetacharacters_CommitsSafely()
    {
        WriteFile("hello.txt", "hello");
        await _gitService.StageAsync(_repositoryDirectory, "hello.txt");

        var message = "fix: handle \"quotes\" && semicolons; `backticks` $(injection)";
        var result = await _gitService.CommitAsync(_repositoryDirectory, message);

        result.Success.ShouldBeTrue();
        var log = RunGit("log", "-1", "--format=%B");
        log.Trim().ShouldBe(message);
    }

    // --- Diff and short status: what the copilot uses to check its own edits ---

    [Fact]
    public async Task GetDiffAsync_ForAModifiedTrackedFile_ShowsTheChange()
    {
        CommitInitialFile();
        WriteFile("hello.txt", "changed content");

        var (success, output) = await _gitService.GetDiffAsync(_repositoryDirectory);

        success.ShouldBeTrue();
        output.ShouldContain("-committed content");
        output.ShouldContain("+changed content");
    }

    [Fact]
    public async Task GetDiffAsync_WithNoChanges_SucceedsWithEmptyOutput()
    {
        CommitInitialFile();

        var (success, output) = await _gitService.GetDiffAsync(_repositoryDirectory);

        success.ShouldBeTrue();
        output.ShouldBeEmpty();
    }

    /// <summary>The distinction GetStatusAsync cannot make: an empty diff and a failed diff must
    /// not look the same, or the model reads "git is broken" as "nothing changed".</summary>
    [Fact]
    public async Task GetDiffAsync_OutsideARepository_FailsRatherThanReturningEmpty()
    {
        var notARepository = Path.Combine(Path.GetTempPath(), $"ideplusplus-notgit-{Guid.NewGuid()}");
        Directory.CreateDirectory(notARepository);
        _extraDirectoriesToClean.Add(notARepository);

        var (success, output) = await _gitService.GetDiffAsync(notARepository);

        success.ShouldBeFalse();
        output.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetDiffAsync_LimitedToAPath_ExcludesOtherFiles()
    {
        CommitInitialFile();
        WriteFile("other.txt", "other");
        RunGit("add", "other.txt");
        RunGit("commit", "-m", "Add other");
        WriteFile("hello.txt", "changed content");
        WriteFile("other.txt", "other changed");

        var (_, output) = await _gitService.GetDiffAsync(_repositoryDirectory, "hello.txt");

        output.ShouldContain("hello.txt");
        output.ShouldNotContain("other.txt");
    }

    [Fact]
    public async Task GetDiffAsync_Unstaged_DoesNotShowStagedChanges()
    {
        CommitInitialFile();
        WriteFile("hello.txt", "changed content");
        RunGit("add", "hello.txt");

        var (_, unstaged) = await _gitService.GetDiffAsync(_repositoryDirectory);
        var (_, staged) = await _gitService.GetDiffAsync(_repositoryDirectory, staged: true);

        unstaged.ShouldBeEmpty();
        staged.ShouldContain("+changed content");
    }

    /// <summary>Worth pinning because it is the one blind spot the tool description has to warn
    /// about: a brand-new file the model just created shows up nowhere in a diff.</summary>
    [Fact]
    public async Task GetDiffAsync_DoesNotShowUntrackedFiles()
    {
        CommitInitialFile();
        WriteFile("brand-new.txt", "new file");

        var (_, output) = await _gitService.GetDiffAsync(_repositoryDirectory);

        output.ShouldNotContain("brand-new.txt");
    }

    [Fact]
    public async Task GetShortStatusAsync_ListsModifiedAndUntrackedFiles()
    {
        CommitInitialFile();
        WriteFile("hello.txt", "changed content");
        WriteFile("brand-new.txt", "new file");

        var (success, output) = await _gitService.GetShortStatusAsync(_repositoryDirectory);

        success.ShouldBeTrue();
        output.ShouldContain("hello.txt");
        output.ShouldContain("brand-new.txt");
    }

    [Fact]
    public async Task GetShortStatusAsync_IncludesTheBranchName()
    {
        CommitInitialFile();

        var (_, output) = await _gitService.GetShortStatusAsync(_repositoryDirectory);

        output.ShouldContain("main");
    }

    [Fact]
    public async Task GetShortStatusAsync_OutsideARepository_Fails()
    {
        var notARepository = Path.Combine(Path.GetTempPath(), $"ideplusplus-notgit-{Guid.NewGuid()}");
        Directory.CreateDirectory(notARepository);
        _extraDirectoriesToClean.Add(notARepository);

        var (success, _) = await _gitService.GetShortStatusAsync(notARepository);

        success.ShouldBeFalse();
    }

    private void CommitInitialFile()
    {
        WriteFile("hello.txt", "committed content");
        RunGit("add", "hello.txt");
        RunGit("commit", "-m", "Initial commit");
    }

    private void WriteFile(string relativePath, string content) =>
        File.WriteAllText(Path.Combine(_repositoryDirectory, relativePath), content);

    private string RunGit(params string[] arguments) => RunGitIn(_repositoryDirectory, arguments);

    private static string RunGitIn(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
