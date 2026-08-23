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
