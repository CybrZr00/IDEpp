using System.Diagnostics;
using System.Text;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.SourceControl.Tests;

/// <summary>
/// The pipeline's additions to TeamGitWorkspace (agent branch names, detached worktrees, restoring
/// paths), against real scratch repositories like TeamGitWorkspaceTests: what matters is what the
/// actual git binary does with a ref name, a detached HEAD and a pathspec.
/// </summary>
public sealed class TeamGitWorkspaceAgentsTests : IDisposable
{
    private const string RunId = "run1";

    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-agents-{Guid.NewGuid()}");
    private readonly string _repositoryDirectory;
    private readonly TeamGitWorkspace _workspace = new();

    public TeamGitWorkspaceAgentsTests()
    {
        _repositoryDirectory = Path.Combine(_rootDirectory, "repo");
        Directory.CreateDirectory(_repositoryDirectory);
        RunGit("init", "--initial-branch=main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test User");
        RunGit("config", "commit.gpgsign", "false");
        RunGit("config", "core.autocrlf", "false");
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_rootDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - a lingering handle on Windows shouldn't fail the run.
        }
    }

    // --- Names ---

    [Theory]
    [InlineData("u1")]
    [InlineData("d1")]
    [InlineData("b12")]
    [InlineData("j5")]
    [InlineData("w3")]
    [InlineData("a")]
    [InlineData("abcdefghijkl")] // 12 characters: the longest allowed.
    public void IsValidAgentName_AcceptsALowerCaseLetterThenLettersOrDigits(string name) =>
        TeamGitWorkspace.IsValidAgentName(name).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("U1")] // Upper case: the name is also a folder, and Windows would not tell U1 from u1.
    [InlineData("1u")]
    [InlineData("u-1")]
    [InlineData("u.1")]
    [InlineData("u/1")]
    [InlineData("u\\1")]
    [InlineData("u1\n")] // $ would let this through; \z does not.
    [InlineData("..")]
    [InlineData("abcdefghijklm")] // 13 characters.
    [InlineData("team")] // Would be the team branch itself.
    public void IsValidAgentName_RefusesAnythingElse(string? name) =>
        TeamGitWorkspace.IsValidAgentName(name).ShouldBeFalse();

    [Fact]
    public void GetAgentBranchName_IsASiblingOfTheTeamBranchUnderTheRunsPrefix()
    {
        TeamGitWorkspace.GetAgentBranchName(RunId, "u1").ShouldBe("ideplusplus/ultra/run1/u1");
        TeamGitWorkspace.GetAgentBranchName(RunId, "j12").ShouldBe("ideplusplus/ultra/run1/j12");
    }

    [Fact]
    public void GetAgentBranchName_WithAnInvalidName_Throws()
    {
        var thrown = Should.Throw<ArgumentException>(() => TeamGitWorkspace.GetAgentBranchName(RunId, "team"));
        thrown.Message.ShouldContain("team");
        Should.Throw<ArgumentException>(() => TeamGitWorkspace.GetAgentBranchName(RunId, "../x"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void GetWorkerBranchName_IsUnchanged_AndMatchesTheAgentName(int number)
    {
        var expected = $"ideplusplus/ultra/run1/w{number}";

        TeamGitWorkspace.GetWorkerBranchName(RunId, number).ShouldBe(expected);
        TeamGitWorkspace.GetAgentBranchName(RunId, $"w{number}").ShouldBe(expected);
    }

    /// <summary>Git refuses a ref that is a path prefix of another, so the team branch and every
    /// agent's branch have to coexist - a name scheme that nested them would fail on the first one.</summary>
    [Fact]
    public void TheTeamBranchAndEveryKindOfAgentBranch_CanExistTogether()
    {
        CommitFile("a.txt", "a");
        var head = RunGit("rev-parse", "HEAD").Trim();

        foreach (var branch in new[]
                 {
                     TeamGitWorkspace.GetTeamBranchName(RunId),
                     TeamGitWorkspace.GetAgentBranchName(RunId, "u1"),
                     TeamGitWorkspace.GetAgentBranchName(RunId, "d1"),
                     TeamGitWorkspace.GetAgentBranchName(RunId, "b1"),
                     TeamGitWorkspace.GetAgentBranchName(RunId, "j1"),
                     TeamGitWorkspace.GetWorkerBranchName(RunId, 1),
                 })
        {
            RunGit("branch", branch, head);
        }

        RunGit("for-each-ref", "--format=%(refname:short)", $"refs/heads/ideplusplus/ultra/{RunId}/")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(6);
    }

    // --- Detached worktrees ---

    [Fact]
    public async Task AddDetachedWorktreeAsync_ChecksOutTheCommitWithoutABranch_AndLeavesTheMainCheckoutAlone()
    {
        CommitFile("hello.txt", "base");
        var commit = RunGit("rev-parse", "HEAD").Trim();
        var branchesBefore = RunGit("branch", "--list");
        var worktree = WorktreePath("_ro");

        var result = await _workspace.AddDetachedWorktreeAsync(_repositoryDirectory, worktree, commit);

        result.Success.ShouldBeTrue(result.Output);
        File.ReadAllText(Path.Combine(worktree, "hello.txt")).ShouldBe("base");
        (await _workspace.GetHeadAsync(worktree)).ShouldBe(commit);
        GitExitCodeIn(worktree, "symbolic-ref", "-q", "HEAD").ShouldBe(1); // 1 = detached.
        RunGit("branch", "--list").ShouldBe(branchesBefore);
        RunGit("status", "--porcelain").ShouldBeEmpty();
    }

    [Fact]
    public async Task AddDetachedWorktreeAsync_TwiceOnTheSameCommit_Works()
    {
        // A branch can only be checked out in one worktree; a detached HEAD has no such limit.
        CommitFile("hello.txt", "base");
        var commit = RunGit("rev-parse", "HEAD").Trim();

        var first = await _workspace.AddDetachedWorktreeAsync(_repositoryDirectory, WorktreePath("_ro"), commit);
        var second = await _workspace.AddDetachedWorktreeAsync(_repositoryDirectory, WorktreePath("_gate"), commit);

        first.Success.ShouldBeTrue(first.Output);
        second.Success.ShouldBeTrue(second.Output);
    }

    [Fact]
    public async Task AddDetachedWorktreeAsync_WithAnUnknownCommit_Fails()
    {
        CommitFile("hello.txt", "base");

        var result = await _workspace.AddDetachedWorktreeAsync(
            _repositoryDirectory, WorktreePath("_ro"), "0000000000000000000000000000000000000000");

        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckoutDetachedAsync_MovesToTheCommit_AndDiscardsLocalChanges()
    {
        CommitFile("hello.txt", "one");
        var first = RunGit("rev-parse", "HEAD").Trim();
        CommitFile("hello.txt", "two");
        CommitFile("later.txt", "later");
        var second = RunGit("rev-parse", "HEAD").Trim();
        var worktree = WorktreePath("_gate");
        (await _workspace.AddDetachedWorktreeAsync(_repositoryDirectory, worktree, first)).Success.ShouldBeTrue();
        File.WriteAllText(Path.Combine(worktree, "hello.txt"), "rewritten by a formatter");

        var forward = await _workspace.CheckoutDetachedAsync(worktree, second);

        forward.Success.ShouldBeTrue(forward.Output);
        (await _workspace.GetHeadAsync(worktree)).ShouldBe(second);
        File.ReadAllText(Path.Combine(worktree, "hello.txt")).ShouldBe("two");
        File.Exists(Path.Combine(worktree, "later.txt")).ShouldBeTrue();
        RunGitIn(worktree, "status", "--porcelain").ShouldBeEmpty();
        GitExitCodeIn(worktree, "symbolic-ref", "-q", "HEAD").ShouldBe(1); // Still detached.

        var back = await _workspace.CheckoutDetachedAsync(worktree, first);

        back.Success.ShouldBeTrue(back.Output);
        File.ReadAllText(Path.Combine(worktree, "hello.txt")).ShouldBe("one");
        File.Exists(Path.Combine(worktree, "later.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task CheckoutDetachedAsync_OnAWorktreeWithABranch_RefusesAndMovesNothing()
    {
        CommitFile("hello.txt", "one");
        var first = RunGit("rev-parse", "HEAD").Trim();
        CommitFile("hello.txt", "two");
        var second = RunGit("rev-parse", "HEAD").Trim();
        var worktree = WorktreePath("b1");
        var branch = TeamGitWorkspace.GetAgentBranchName(RunId, "b1");
        (await _workspace.AddWorktreeAsync(_repositoryDirectory, worktree, branch, second)).Success.ShouldBeTrue();

        var result = await _workspace.CheckoutDetachedAsync(worktree, first);

        result.Success.ShouldBeFalse();
        result.Output.ShouldContain("branch");
        (await _workspace.ResolveRefAsync(_repositoryDirectory, $"refs/heads/{branch}")).ShouldBe(second);
        File.ReadAllText(Path.Combine(worktree, "hello.txt")).ShouldBe("two");
    }

    // --- Restoring paths ---

    [Fact]
    public async Task RestorePathsAsync_PutsBackModifiedDeletedAndAddedFiles_AndLeavesTheRestAlone()
    {
        CommitFile("keep.txt", "keep");
        CommitFile("gone.txt", "gone");
        CommitFile("other.txt", "other");
        var commit = RunGit("rev-parse", "HEAD").Trim();
        var worktree = await AddDetachedAsync(commit);
        File.WriteAllText(Path.Combine(worktree, "keep.txt"), "changed");
        File.Delete(Path.Combine(worktree, "gone.txt"));
        File.WriteAllText(Path.Combine(worktree, "other.txt"), "touched, but not in the list");
        File.WriteAllText(Path.Combine(worktree, "untracked.txt"), "new");
        File.WriteAllText(Path.Combine(worktree, "staged.txt"), "new and staged");
        RunGitIn(worktree, "add", "--", "staged.txt");

        var result = await _workspace.RestorePathsAsync(
            worktree, commit, ["keep.txt", "gone.txt", "untracked.txt", "staged.txt"]);

        result.Success.ShouldBeTrue(result.Output);
        File.ReadAllText(Path.Combine(worktree, "keep.txt")).ShouldBe("keep");
        File.ReadAllText(Path.Combine(worktree, "gone.txt")).ShouldBe("gone");
        File.Exists(Path.Combine(worktree, "untracked.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(worktree, "staged.txt")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(worktree, "other.txt")).ShouldBe("touched, but not in the list");
        RunGitIn(worktree, "status", "--porcelain").Trim().ShouldBe("M other.txt");
    }

    [Fact]
    public async Task RestorePathsAsync_RestoresFromTheGivenCommit_NotFromHead()
    {
        CommitFile("a.txt", "v1");
        var v1 = RunGit("rev-parse", "HEAD").Trim();
        CommitFile("a.txt", "v2");
        var v2 = RunGit("rev-parse", "HEAD").Trim();
        var worktree = await AddDetachedAsync(v2);

        var result = await _workspace.RestorePathsAsync(worktree, v1, ["a.txt"]);

        result.Success.ShouldBeTrue(result.Output);
        File.ReadAllText(Path.Combine(worktree, "a.txt")).ShouldBe("v1");
    }

    [Fact]
    public async Task RestorePathsAsync_AcceptsBackslashesAndFilesInSubfolders()
    {
        Directory.CreateDirectory(Path.Combine(_repositoryDirectory, "src", "deep"));
        CommitFile(Path.Combine("src", "deep", "a.cs"), "class A {}");
        var commit = RunGit("rev-parse", "HEAD").Trim();
        var worktree = await AddDetachedAsync(commit);
        File.WriteAllText(Path.Combine(worktree, "src", "deep", "a.cs"), "class Changed {}");

        var result = await _workspace.RestorePathsAsync(worktree, commit, [@"src\deep\a.cs"]);

        result.Success.ShouldBeTrue(result.Output);
        File.ReadAllText(Path.Combine(worktree, "src", "deep", "a.cs")).ShouldBe("class A {}");
    }

    [Fact]
    public async Task RestorePathsAsync_TreatsPathspecCharactersLiterally()
    {
        // With pathspec magic on, "a[1].txt" would be a glob that matches a1.txt - and not itself.
        CommitFile("a1.txt", "plain");
        CommitFile("a[1].txt", "bracketed");
        var commit = RunGit("rev-parse", "HEAD").Trim();
        var worktree = await AddDetachedAsync(commit);
        File.WriteAllText(Path.Combine(worktree, "a1.txt"), "edited plain");
        File.WriteAllText(Path.Combine(worktree, "a[1].txt"), "edited bracketed");

        var result = await _workspace.RestorePathsAsync(worktree, commit, ["a[1].txt"]);

        result.Success.ShouldBeTrue(result.Output);
        File.ReadAllText(Path.Combine(worktree, "a[1].txt")).ShouldBe("bracketed");
        File.ReadAllText(Path.Combine(worktree, "a1.txt")).ShouldBe("edited plain");
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("sub/../a.txt")] // Harmless here, but ".." is refused wherever it appears.
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData(".git/config")]
    [InlineData(".GIT/config")]
    [InlineData("a.txt:stream")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("dir/")]
    public async Task RestorePathsAsync_RefusesAnythingThatIsNotAPlainPathInsideTheWorktree(string path)
    {
        CommitFile("a.txt", "a");
        var commit = RunGit("rev-parse", "HEAD").Trim();
        var worktree = await AddDetachedAsync(commit);
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "edited");
        var outside = Path.Combine(_rootDirectory, "outside.txt");
        File.WriteAllText(outside, "must survive");

        var result = await _workspace.RestorePathsAsync(worktree, commit, ["a.txt", path]);

        result.Success.ShouldBeFalse();
        result.Output.ShouldContain("Refusing");
        // Checked before git ran at all: not even the valid path was touched.
        File.ReadAllText(Path.Combine(worktree, "a.txt")).ShouldBe("edited");
        File.ReadAllText(outside).ShouldBe("must survive");
    }

    [Fact]
    public async Task RestorePathsAsync_WithNoPaths_SucceedsAndDoesNothing()
    {
        CommitFile("a.txt", "a");
        var commit = RunGit("rev-parse", "HEAD").Trim();
        var worktree = await AddDetachedAsync(commit);
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "edited");

        var result = await _workspace.RestorePathsAsync(worktree, commit, []);

        result.Success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(worktree, "a.txt")).ShouldBe("edited");
    }

    [Fact]
    public async Task RestorePathsAsync_ListedTwice_IsStillFine()
    {
        CommitFile("a.txt", "a");
        var commit = RunGit("rev-parse", "HEAD").Trim();
        var worktree = await AddDetachedAsync(commit);
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "edited");

        var result = await _workspace.RestorePathsAsync(worktree, commit, ["a.txt", "./a.txt", "a.txt"]);

        result.Success.ShouldBeTrue(result.Output);
        File.ReadAllText(Path.Combine(worktree, "a.txt")).ShouldBe("a");
    }

    [Fact]
    public async Task RestorePathsAsync_WithAnUnknownCommit_FailsWithoutTouchingAnything()
    {
        CommitFile("a.txt", "a");
        var commit = RunGit("rev-parse", "HEAD").Trim();
        var worktree = await AddDetachedAsync(commit);
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "edited");

        var result = await _workspace.RestorePathsAsync(
            worktree, "0000000000000000000000000000000000000000", ["a.txt"]);

        result.Success.ShouldBeFalse();
        File.ReadAllText(Path.Combine(worktree, "a.txt")).ShouldBe("edited");
    }

    // --- Helpers ---

    private async Task<string> AddDetachedAsync(string commit)
    {
        var worktree = WorktreePath($"wt{Guid.NewGuid():N}"[..10]);
        var result = await _workspace.AddDetachedWorktreeAsync(_repositoryDirectory, worktree, commit);
        result.Success.ShouldBeTrue(result.Output);
        return worktree;
    }

    // Outside the repository, under a folder that doesn't exist yet - the shape of the real
    // <work folder>\<runId>\<agent> layout.
    private string WorktreePath(string name) => Path.Combine(_rootDirectory, "ultra", RunId, name);

    private void CommitFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repositoryDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        RunGit("add", "--", relativePath);
        RunGit("commit", "-m", $"Write {relativePath}");
    }

    private string RunGit(params string[] arguments) => RunGitIn(_repositoryDirectory, arguments);

    private static string RunGitIn(string workingDirectory, params string[] arguments)
    {
        var (exitCode, output, error) = RunGitProcess(workingDirectory, arguments);
        return exitCode == 0
            ? output
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
    }

    private static int GitExitCodeIn(string workingDirectory, params string[] arguments) =>
        RunGitProcess(workingDirectory, arguments).ExitCode;

    private static (int ExitCode, string Output, string Error) RunGitProcess(string workingDirectory, string[] arguments)
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

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }
}
