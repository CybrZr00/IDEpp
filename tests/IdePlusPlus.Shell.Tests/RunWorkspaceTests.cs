using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The pipeline's workspace against a real git repository: what the snapshot leaves alone, what a refusal
/// leaves behind (nothing), the two kinds of worktree it hands out, and above all that closing it removes
/// every worktree and link, keeps work that never reached the team branch, and never follows a link into
/// the user's own folders.
/// </summary>
public sealed class RunWorkspaceTests : IDisposable
{
    private const string RunId = "r1";

    private readonly TeamRepositoryFixture _repo = new();
    private readonly List<RunWorkspace> _opened = [];
    private long _freeBytes = long.MaxValue / 2;

    public void Dispose()
    {
        // A test that failed halfway must not leave worktrees pinning the repository's folders.
        foreach (var workspace in _opened)
        {
            workspace.CloseAsync().GetAwaiter().GetResult();
        }

        _repo.Dispose();
    }

    private string SolutionPath => Path.Combine(_repo.RepositoryRoot, "Scratch.slnx");

    private string Ultra => Path.Combine(_repo.Root, "ultra");

    private string TeamBranch => TeamGitWorkspace.GetTeamBranchName(RunId);

    private RunWorkspace NewWorkspace(Action<string>? progress = null)
    {
        var workspace = new RunWorkspace(_repo.TeamGit, Ultra, RunId, _repo.RepositoryRoot, SolutionPath, _ => _freeBytes, progress);
        _opened.Add(workspace);
        return workspace;
    }

    private async Task<RunWorkspace> OpenAsync(string? startFrom = null, int planned = 0)
    {
        var workspace = NewWorkspace();
        await workspace.OpenAsync(startFrom, planned, CancellationToken.None);
        return workspace;
    }

    private string Git(params string[] arguments) => TeamRepositoryFixture.Git(_repo.RepositoryRoot, arguments);

    /// <summary>Every local branch except the run's team branch, which is the one the workspace is meant to add.</summary>
    private string[] BranchesExceptTeam() =>
        Git("for-each-ref", "--format=%(refname:short)", "refs/heads/")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(branch => branch != TeamBranch)
            .ToArray();

    private async Task<string?> RefAsync(string reference) => await _repo.TeamGit.ResolveRefAsync(_repo.RepositoryRoot, reference);

    /// <summary>Commits a new file in an agent's worktree and fast-forwards the team branch onto it - the
    /// merge an owner's team_merge does.</summary>
    private async Task MergeFileAsync(WorkspaceWorktree worktree, string relativePath, string content)
    {
        var full = Path.Combine(worktree.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        (await _repo.TeamGit.CommitAllAsync(worktree.Path, $"add {relativePath}", worktree.Author)).Status.ShouldBe(TeamCommitStatus.Committed);
        var head = await _repo.TeamGit.GetHeadAsync(worktree.Path);
        var teamHead = (await RefAsync(TeamBranch))!;
        (await _repo.TeamGit.FastForwardBranchAsync(_repo.RepositoryRoot, TeamBranch, head, teamHead))
            .Status.ShouldBe(TeamFastForwardStatus.FastForwarded);
    }

    // --- Opening ---

    [Fact]
    public async Task OpenSnapshotsTheWorkingTree_WithoutTouchingTheUsersRepository()
    {
        File.AppendAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs"), "// edited, not staged\r\n");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "New.cs"), "public class New { }\r\n");
        var headBefore = Git("rev-parse", "HEAD");
        var statusBefore = Git("status", "--porcelain");
        var branchesBefore = BranchesExceptTeam();

        var workspace = await OpenAsync();

        workspace.Snapshot.ShouldNotBe(headBefore);
        workspace.Start.ShouldBe(workspace.Snapshot);
        workspace.StartedFrom.ShouldBeNull();
        Git("show", $"{workspace.Snapshot}:Lib/New.cs").ShouldContain("class New");
        Git("show", $"{workspace.Snapshot}:Lib/Class1.cs").ShouldContain("// edited, not staged");

        (await RefAsync(TeamBranch)).ShouldBe(workspace.Start);
        Git("rev-parse", "HEAD").ShouldBe(headBefore);
        Git("status", "--porcelain").ShouldBe(statusBefore);
        BranchesExceptTeam().ShouldBe(branchesBefore); // Only the team branch was added.
    }

    [Fact]
    public async Task OpenCreatesTheRunFolderAndTheRecordsFolder()
    {
        var workspace = await OpenAsync();

        Directory.Exists(workspace.RunDirectory).ShouldBeTrue();
        workspace.RunDirectory.ShouldBe(Path.Combine(Ultra, RunId));
        Directory.Exists(workspace.PhasesDirectory).ShouldBeTrue();
        workspace.PhasesDirectory.ShouldBe(Path.Combine(workspace.RunDirectory, "_phases"));
        workspace.ReadOnlyDirectory.ShouldBe(Path.Combine(workspace.RunDirectory, "_ro"));
        workspace.GateDirectory.ShouldBe(Path.Combine(workspace.RunDirectory, "_gate"));
        workspace.TeamBranch.ShouldBe("ideplusplus/ultra/r1/team");
    }

    [Fact]
    public async Task OpenTellsTheStatusCardWhatItIsDoing()
    {
        var messages = new List<string>();
        var workspace = NewWorkspace(messages.Add);

        await workspace.OpenAsync(null, 0, CancellationToken.None);

        messages.Count.ShouldBe(2);
        messages.ShouldAllBe(m => m.StartsWith("Creating workspace"));
    }

    [Fact]
    public async Task OpenTwiceIsAnError()
    {
        var workspace = await OpenAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => workspace.OpenAsync(null, 0, CancellationToken.None));
    }

    [Fact]
    public async Task StartingFromABranchMergesItIntoTheSnapshot()
    {
        Git("checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Feature.cs"), "public class Feature { }\r\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "feature");
        Git("checkout", "-q", "main");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Mine.cs"), "public class Mine { }\r\n");

        var workspace = await OpenAsync("feature");

        workspace.StartedFrom.ShouldBe("feature");
        workspace.Start.ShouldNotBe(workspace.Snapshot);
        Git("show", $"{workspace.Start}:Lib/Feature.cs").ShouldContain("class Feature");
        Git("show", $"{workspace.Start}:Lib/Mine.cs").ShouldContain("class Mine");
        // The result is still compared with what the user had, so the branch's work is offered too.
        Git("ls-tree", "-r", "--name-only", workspace.Snapshot).ShouldNotContain("Feature.cs");
        (await RefAsync(TeamBranch)).ShouldBe(workspace.Start);
    }

    [Fact]
    public async Task StartingFromABranchThatConflictsIsRefused_BeforeAnythingIsCreated()
    {
        Git("checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs"), "public class Class1\r\n{\r\n    public int Value => 2;\r\n}\r\n");
        Git("commit", "-q", "-am", "feature");
        Git("checkout", "-q", "main");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs"), "public class Class1\r\n{\r\n    public int Value => 3;\r\n}\r\n");
        var workspace = NewWorkspace();

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => workspace.OpenAsync("feature", 0, CancellationToken.None));

        thrown.Message.ShouldContain("Can't start from feature");
        thrown.Message.ShouldContain("Lib/Class1.cs");
        (await RefAsync(TeamBranch)).ShouldBeNull();
        Directory.Exists(workspace.PhasesDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task StartingFromABranchThatDoesNotExistIsAnError()
    {
        var workspace = NewWorkspace();

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => workspace.OpenAsync("no-such-branch", 0, CancellationToken.None));

        thrown.Message.ShouldContain("no-such-branch");
    }

    // --- The disk ---

    [Fact]
    public async Task ADiskTooSmallForThePlannedWorktreesIsRefused_BeforeTheTeamBranchExists()
    {
        _freeBytes = 0;
        var workspace = NewWorkspace();

        var thrown = await Should.ThrowAsync<UltraTeamDiskSpaceException>(
            () => workspace.OpenAsync(null, plannedWorktrees: 3, CancellationToken.None));

        thrown.Message.ShouldContain("There isn't room on the disk for the pipeline's worktrees");
        thrown.Message.ShouldContain("3 worktree(s)");
        (await RefAsync(TeamBranch)).ShouldBeNull();
    }

    [Fact]
    public async Task NoWorktreesNeedNoSpace()
    {
        _freeBytes = 0;

        await OpenAsync(planned: 0);
    }

    [Fact]
    public async Task TheDiskCheckLeavesTheHeadroomFree_ToTheByte()
    {
        var workspace = await OpenAsync();
        var bytesEach = 10L * 1024 * 1024;

        _freeBytes = (2 * bytesEach) + RunWorkspace.DiskHeadroomBytes;
        workspace.EnsureDiskSpace(2, bytesEach, "the owners"); // Exactly enough.

        _freeBytes--;
        var thrown = Should.Throw<UltraTeamDiskSpaceException>(() => workspace.EnsureDiskSpace(2, bytesEach, "the owners"));
        thrown.Message.ShouldContain("for the owners");
        thrown.Message.ShouldContain("2 worktree(s) need about");
        thrown.Message.ShouldContain("is kept spare");
    }

    [Fact]
    public async Task TheWorktreeSizeIsGuessedFromTheCheckout_ThenReplacedByWhatABuildMeasures()
    {
        var workspace = await OpenAsync();
        workspace.CheckoutBytes.ShouldBeGreaterThan(0);
        workspace.WorktreeBytes.ShouldBe(workspace.CheckoutBytes * RunWorkspace.BuildOutputFactor);

        var worktree = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(worktree.Path, "obj"));
        File.WriteAllBytes(Path.Combine(worktree.Path, "obj", "big.bin"), new byte[100_000]);

        var growth = workspace.RememberBuiltWorktree(worktree.Path);

        growth.ShouldBeInRange(90_000L, 120_000L);
        workspace.WorktreeBytes.ShouldBe(workspace.CheckoutBytes + growth);
    }

    // --- Agent worktrees ---

    [Fact]
    public async Task AnAgentGetsItsOwnBranchAndFolder_AtTheStartCommit()
    {
        var workspace = await OpenAsync();

        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);

        b1.Name.ShouldBe("b1");
        b1.Path.ShouldBe(Path.Combine(workspace.RunDirectory, "b1"));
        b1.Branch.ShouldBe("ideplusplus/ultra/r1/b1");
        b1.Author.ShouldBe("IDE++ Ultra b1");
        Directory.Exists(b1.Path).ShouldBeTrue();
        File.Exists(Path.Combine(b1.Path, "Lib", "Class1.cs")).ShouldBeTrue();
        (await _repo.TeamGit.GetHeadAsync(b1.Path)).ShouldBe(workspace.Start);
        TeamRepositoryFixture.Git(b1.Path, "branch", "--show-current").ShouldBe(b1.Branch);
        workspace.Worktrees.Select(w => w.Name).ShouldBe(["b1"]);
    }

    [Fact]
    public async Task TheContextForAnAgentPointsEverythingIntoItsOwnWorktree()
    {
        var workspace = await OpenAsync();
        var testProject = Path.Combine(_repo.RepositoryRoot, "Lib.Tests", "Lib.Tests.csproj");
        var d1 = await workspace.AddWorktreeAsync("d1", workspace.Start, CancellationToken.None);

        var context = workspace.ContextFor(d1, [testProject]);

        context.Name.ShouldBe("d1");
        context.RepositoryRoot.ShouldBe(_repo.RepositoryRoot);
        context.WorktreeRoot.ShouldBe(d1.Path);
        context.SolutionDirectory.ShouldBe(d1.Path);
        context.SolutionPath.ShouldBe(Path.Combine(d1.Path, "Scratch.slnx"));
        context.TestProjectPaths.ShouldBe([Path.Combine(d1.Path, "Lib.Tests", "Lib.Tests.csproj")]);
        context.TeamBranch.ShouldBe(workspace.TeamBranch);
        context.AuthorName.ShouldBe("IDE++ Ultra d1");
    }

    [Fact]
    public async Task ASolutionInASubfolderKeepsItsRelativeLocationInEveryWorktree()
    {
        // A solution below the repository root: the worktree's solution directory is the same subfolder.
        Directory.CreateDirectory(Path.Combine(_repo.RepositoryRoot, "src", "App"));
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "src", "App", "App.slnx"), "<Solution />");
        Git("add", "-A");
        Git("commit", "-q", "-m", "nested solution");
        var nested = Path.Combine(_repo.RepositoryRoot, "src", "App", "App.slnx");
        var workspace = new RunWorkspace(_repo.TeamGit, Ultra, "nested", _repo.RepositoryRoot, nested, _ => _freeBytes);
        _opened.Add(workspace);
        await workspace.OpenAsync(null, 0, CancellationToken.None);
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);

        var context = workspace.ContextFor(b1, []);

        workspace.SolutionRelativeDirectory.ShouldBe(Path.Combine("src", "App"));
        context.SolutionDirectory.ShouldBe(Path.Combine(b1.Path, "src", "App"));
        context.SolutionPath.ShouldBe(Path.Combine(b1.Path, "src", "App", "App.slnx"));
    }

    [Fact]
    public async Task PathsAreTheSameInTheDesignAndInGit_WhenTheSolutionIsAtTheRepositoryRoot()
    {
        var workspace = await OpenAsync();

        workspace.ToRepositoryPath("Lib/Class1.cs").ShouldBe("Lib/Class1.cs");
        workspace.ToRepositoryPath("Lib\\Class1.cs").ShouldBe("Lib/Class1.cs");
        workspace.ToSolutionPath("Lib/Class1.cs").ShouldBe("Lib/Class1.cs");
    }

    [Fact]
    public async Task PathsAreTranslatedBothWays_WhenTheSolutionIsInASubfolder_AndOutsideItNothingIsNamed()
    {
        Directory.CreateDirectory(Path.Combine(_repo.RepositoryRoot, "src", "App"));
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "src", "App", "App.slnx"), "<Solution />");
        Git("add", "-A");
        Git("commit", "-q", "-m", "nested solution");
        var workspace = new RunWorkspace(
            _repo.TeamGit, Ultra, "nested2", _repo.RepositoryRoot, Path.Combine(_repo.RepositoryRoot, "src", "App", "App.slnx"), _ => _freeBytes);
        _opened.Add(workspace);

        workspace.ToRepositoryPath("Lib/Class1.cs").ShouldBe("src/App/Lib/Class1.cs");
        workspace.ToSolutionPath("src/App/Lib/Class1.cs").ShouldBe("Lib/Class1.cs");
        workspace.ToSolutionPath("SRC\\App\\Lib\\Class1.cs").ShouldBe("Lib/Class1.cs");
        workspace.ToSolutionPath("README.md").ShouldBeNull();
        workspace.ToSolutionPath("src/Other/File.cs").ShouldBeNull();
        workspace.ToSolutionPath("src/AppExtra/File.cs").ShouldBeNull(); // not a folder of the solution, only a name that starts the same
    }

    [Fact]
    public async Task LandingACommit_MovesTheTeamBranchOntoIt()
    {
        var workspace = await OpenAsync();
        var d1 = await workspace.AddWorktreeAsync("d1", workspace.Start, CancellationToken.None);
        File.WriteAllText(Path.Combine(d1.Path, "Lib", "IGreeter.cs"), "public interface IGreeter { }\r\n");
        (await _repo.TeamGit.CommitAllAsync(d1.Path, "contract", d1.Author)).Status.ShouldBe(TeamCommitStatus.Committed);
        var head = await _repo.TeamGit.GetHeadAsync(d1.Path);

        await workspace.LandAsync(head, CancellationToken.None);

        (await RefAsync(TeamBranch)).ShouldBe(head);
        (await _repo.TeamGit.GetChangedFilesAsync(_repo.RepositoryRoot, workspace.Snapshot, TeamBranch))
            .ShouldHaveSingleItem().RelativePath.ShouldBe("Lib/IGreeter.cs");
    }

    [Fact]
    public async Task ACommitThatDoesNotDescendFromTheTeamBranch_CannotBeLanded()
    {
        var workspace = await OpenAsync();
        var d1 = await workspace.AddWorktreeAsync("d1", workspace.Start, CancellationToken.None);
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        await MergeFileAsync(b1, "Lib/Other.cs", "public class Other { }\r\n"); // the team branch moves on
        File.WriteAllText(Path.Combine(d1.Path, "Lib", "IGreeter.cs"), "public interface IGreeter { }\r\n");
        await _repo.TeamGit.CommitAllAsync(d1.Path, "contract", d1.Author);
        var head = await _repo.TeamGit.GetHeadAsync(d1.Path);
        var before = await RefAsync(TeamBranch);

        await Should.ThrowAsync<InvalidOperationException>(() => workspace.LandAsync(head, CancellationToken.None));

        (await RefAsync(TeamBranch)).ShouldBe(before);
    }

    [Fact]
    public async Task LandingBeforeTheBranchExists_IsAnError()
    {
        var workspace = NewWorkspace();

        await Should.ThrowAsync<InvalidOperationException>(() => workspace.LandAsync("0000000000000000000000000000000000000000", CancellationToken.None));
    }

    [Theory]
    [InlineData("team")]
    [InlineData("../x")]
    [InlineData("B1")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task AnInvalidAgentNameIsRefused_AndNothingIsCreated(string name)
    {
        var workspace = await OpenAsync();

        await Should.ThrowAsync<ArgumentException>(() => workspace.AddWorktreeAsync(name, workspace.Start, CancellationToken.None));

        workspace.Worktrees.ShouldBeEmpty();
        Directory.GetDirectories(workspace.RunDirectory).Select(Path.GetFileName).ShouldBe(["_phases"]);
    }

    [Fact]
    public async Task TheSameAgentCannotHaveTwoWorktrees()
    {
        var workspace = await OpenAsync();
        await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => workspace.AddWorktreeAsync("B1".ToLowerInvariant(), workspace.Start, CancellationToken.None));

        thrown.Message.ShouldContain("already has a worktree");
    }

    [Fact]
    public async Task AWorktreeCannotBeAddedBeforeOpening_OrAfterClosing()
    {
        var unopened = NewWorkspace();
        (await Should.ThrowAsync<InvalidOperationException>(() => unopened.AddWorktreeAsync("b1", "HEAD", CancellationToken.None)))
            .Message.ShouldContain("Open the workspace");

        var workspace = await OpenAsync();
        await workspace.CloseAsync();
        (await Should.ThrowAsync<InvalidOperationException>(() => workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None)))
            .Message.ShouldContain("closed");
    }

    [Fact]
    public async Task SeveralAgentsCanBeAddedAtOnce()
    {
        var workspace = await OpenAsync();

        var added = await Task.WhenAll(new[] { "b1", "b2", "b3", "j4", "d1" }
            .Select(name => workspace.AddWorktreeAsync(name, workspace.Start, CancellationToken.None)));

        added.Length.ShouldBe(5);
        workspace.Worktrees.Count.ShouldBe(5);
        added.ShouldAllBe(w => Directory.Exists(w.Path));
        BranchesExceptTeam().Count(b => b.StartsWith("ideplusplus/ultra/r1/", StringComparison.Ordinal)).ShouldBe(5);
    }

    // --- Detached worktrees ---

    [Fact]
    public async Task TheSharedReadOnlyCheckoutAndTheGateAreDetached()
    {
        var workspace = await OpenAsync();

        var ro = await workspace.AddDetachedWorktreeAsync("_ro", workspace.Start, linkDependencies: false, CancellationToken.None);
        var gate = await workspace.AddDetachedWorktreeAsync("_gate", workspace.Start, linkDependencies: true, CancellationToken.None);

        ro.Path.ShouldBe(workspace.ReadOnlyDirectory);
        gate.Path.ShouldBe(workspace.GateDirectory);
        ro.Branch.ShouldBeNull();
        gate.Branch.ShouldBeNull();
        TeamRepositoryFixture.Git(ro.Path, "branch", "--show-current").ShouldBeEmpty(); // Detached HEAD.
        (await _repo.TeamGit.GetHeadAsync(gate.Path)).ShouldBe(workspace.Start);
        BranchesExceptTeam().ShouldNotContain(b => b.StartsWith("ideplusplus/ultra/r1/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("b1")]
    [InlineData("_phases")]
    [InlineData("_other")]
    [InlineData("team")]
    public async Task OnlyTheTwoReservedFoldersAreDetachedCheckouts(string name)
    {
        var workspace = await OpenAsync();

        await Should.ThrowAsync<ArgumentException>(
            () => workspace.AddDetachedWorktreeAsync(name, workspace.Start, false, CancellationToken.None));
    }

    [Fact]
    public async Task ADetachedCheckoutFollowsTheTeamBranchAndDiscardsWhatWasChangedInIt()
    {
        var workspace = await OpenAsync();
        var d1 = await workspace.AddWorktreeAsync("d1", workspace.Start, CancellationToken.None);
        var ro = await workspace.AddDetachedWorktreeAsync("_ro", workspace.Start, false, CancellationToken.None);
        File.WriteAllText(Path.Combine(ro.Path, "Lib", "Class1.cs"), "scribbled on by a reviewer");

        await MergeFileAsync(d1, "Lib/Contract.cs", "public interface IContract { }\r\n");
        var newHead = (await RefAsync(TeamBranch))!;
        await workspace.MoveDetachedAsync(ro, newHead, CancellationToken.None);

        (await _repo.TeamGit.GetHeadAsync(ro.Path)).ShouldBe(newHead);
        File.ReadAllText(Path.Combine(ro.Path, "Lib", "Contract.cs")).ShouldContain("IContract");
        File.ReadAllText(Path.Combine(ro.Path, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
    }

    [Fact]
    public async Task OnlyADetachedCheckoutCanBeMoved()
    {
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);

        await Should.ThrowAsync<ArgumentException>(() => workspace.MoveDetachedAsync(b1, workspace.Start, CancellationToken.None));
    }

    [Fact]
    public async Task MovingToACommitThatDoesNotExistFailsClearly()
    {
        var workspace = await OpenAsync();
        var ro = await workspace.AddDetachedWorktreeAsync("_ro", workspace.Start, false, CancellationToken.None);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => workspace.MoveDetachedAsync(ro, "0000000000000000000000000000000000000000", CancellationToken.None));

        thrown.Message.ShouldContain("Could not move _ro");
    }

    // --- Links ---

    private void IgnoreNodeModules()
    {
        File.AppendAllText(Path.Combine(_repo.RepositoryRoot, ".gitignore"), "node_modules/\n");
        var modules = Path.Combine(_repo.RepositoryRoot, "node_modules", "pkg");
        Directory.CreateDirectory(modules);
        File.WriteAllText(Path.Combine(modules, "index.js"), "module.exports = 1;");
        File.WriteAllBytes(Path.Combine(modules, "big.bin"), new byte[500_000]);
    }

    [Fact]
    public async Task AnIgnoredDependencyFolderIsLinkedIntoAgentWorktrees_NotIntoTheReadOnlyCheckout()
    {
        IgnoreNodeModules();
        var workspace = await OpenAsync();
        workspace.IgnoredDependencyFolders.ShouldBe(["node_modules"]);

        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        var ro = await workspace.AddDetachedWorktreeAsync("_ro", workspace.Start, linkDependencies: false, CancellationToken.None);

        File.ReadAllText(Path.Combine(b1.Path, "node_modules", "pkg", "index.js")).ShouldBe("module.exports = 1;");
        new DirectoryInfo(Path.Combine(b1.Path, "node_modules")).Attributes.HasFlag(FileAttributes.ReparsePoint).ShouldBeTrue();
        Directory.Exists(Path.Combine(ro.Path, "node_modules")).ShouldBeFalse();
        // Git ignores the link in the worktree too, so an agent's `git add -A` never stages it.
        TeamRepositoryFixture.Git(b1.Path, "status", "--porcelain").ShouldBeEmpty();
        workspace.NotMirrored.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheLinkedDependenciesAreNotCountedInAWorktreesSize()
    {
        IgnoreNodeModules();
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(b1.Path, "obj"));
        File.WriteAllBytes(Path.Combine(b1.Path, "obj", "out.bin"), new byte[100_000]);

        var growth = workspace.RememberBuiltWorktree(b1.Path);

        // The 500 KB in the user's node_modules is shared, so it is not this worktree's weight.
        growth.ShouldBeInRange(90_000L, 150_000L);
    }

    [Fact]
    public async Task ASiblingProjectIsLinkedIntoTheRunFolder_AndOnlyTheLinkIsRemoved()
    {
        var sibling = Path.Combine(_repo.Root, "Sibling");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "Sib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(sibling, "Sib.cs"), "public class Sib { }");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\..\Sibling\Sib.csproj" /></ItemGroup>
            </Project>
            """);
        var workspace = await OpenAsync();

        workspace.ExternalFolders.Siblings.Select(f => f.Name).ShouldBe(["Sibling"]);
        var link = Path.Combine(workspace.RunDirectory, "Sibling");
        File.ReadAllText(Path.Combine(link, "Sib.cs")).ShouldContain("class Sib");
        new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint).ShouldBeTrue();

        await workspace.CloseAsync();

        Directory.Exists(link).ShouldBeFalse();
        File.ReadAllText(Path.Combine(sibling, "Sib.cs")).ShouldContain("class Sib"); // The user's folder is untouched.
    }

    // --- Closing ---

    [Fact]
    public async Task ClosingRemovesEveryWorktreeAndLink_AndLeavesTheUsersDependenciesAlone()
    {
        IgnoreNodeModules();
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        var ro = await workspace.AddDetachedWorktreeAsync("_ro", workspace.Start, false, CancellationToken.None);
        var gate = await workspace.AddDetachedWorktreeAsync("_gate", workspace.Start, true, CancellationToken.None);

        var kept = await workspace.CloseAsync();

        kept.ShouldBeEmpty();
        new[] { b1.Path, ro.Path, gate.Path }.ShouldAllBe(path => !Directory.Exists(path));
        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "node_modules", "pkg", "index.js")).ShouldBe("module.exports = 1;");
        File.ReadAllBytes(Path.Combine(_repo.RepositoryRoot, "node_modules", "pkg", "big.bin")).Length.ShouldBe(500_000);
        Git("worktree", "list", "--porcelain").ShouldNotContain("ultra");
    }

    [Fact]
    public async Task AnAgentWhoseWorkReachedTheTeamBranchHasItsBranchDeleted()
    {
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        await MergeFileAsync(b1, "Lib/B1.cs", "public class B1 { }\r\n");

        var kept = await workspace.CloseAsync();

        kept.ShouldBeEmpty();
        (await RefAsync($"refs/heads/{b1.Branch}")).ShouldBeNull();
        (await RefAsync(TeamBranch)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AnAgentWithACommitThatNeverReachedTheTeamBranchHasItsBranchKept()
    {
        var workspace = await OpenAsync();
        var j4 = await workspace.AddWorktreeAsync("j4", workspace.Start, CancellationToken.None);
        File.WriteAllText(Path.Combine(j4.Path, "FailingTest.cs"), "// a failing test: evidence, never merged\r\n");
        await _repo.TeamGit.CommitAllAsync(j4.Path, "failing test", j4.Author);

        var kept = await workspace.CloseAsync();

        kept.ShouldBe([j4.Branch!]);
        (await RefAsync($"refs/heads/{j4.Branch}")).ShouldNotBeNull();
        Git("show", $"{j4.Branch}:FailingTest.cs").ShouldContain("evidence");
        Directory.Exists(j4.Path).ShouldBeFalse(); // The folder goes either way; the branch is what is kept.
    }

    [Fact]
    public async Task UncommittedEditsAreCommittedAsUnverified_AndTheBranchIsKept()
    {
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        File.WriteAllText(Path.Combine(b1.Path, "Lib", "Half.cs"), "public class Half { }\r\n");

        var kept = await workspace.CloseAsync();

        kept.ShouldBe([b1.Branch!]);
        Git("log", "-1", "--format=%s", b1.Branch!).ShouldBe(RunWorkspace.UnfinishedWorkCommitMessage);
        Git("show", $"{b1.Branch}:Lib/Half.cs").ShouldContain("class Half");
    }

    [Fact]
    public async Task AnAgentThatDidNothingHasItsBranchDeleted()
    {
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);

        (await workspace.CloseAsync()).ShouldBeEmpty();

        (await RefAsync($"refs/heads/{b1.Branch}")).ShouldBeNull();
    }

    [Fact]
    public async Task ClosingTwiceIsHarmless_AndSecondTimeKeepsNothingNew()
    {
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        File.WriteAllText(Path.Combine(b1.Path, "Lib", "Half.cs"), "x");

        (await workspace.CloseAsync()).Count.ShouldBe(1);
        (await workspace.CloseAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ClosingAWorkspaceThatWasNeverOpenedDoesNothing()
    {
        var workspace = NewWorkspace();

        (await workspace.CloseAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AWorktreeThatWasAlreadyDeletedByHandDoesNotStopTheCloseFromFinishing()
    {
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        var b2 = await workspace.AddWorktreeAsync("b2", workspace.Start, CancellationToken.None);
        File.SetAttributes(Path.Combine(b1.Path, ".git"), FileAttributes.Normal);
        Directory.Delete(b1.Path, recursive: true);

        var kept = await workspace.CloseAsync();

        // b1's folder is gone, so it cannot be inspected: keeping the branch loses nothing.
        kept.ShouldContain(b1.Branch!);
        Directory.Exists(b2.Path).ShouldBeFalse();
    }

    // --- Finishing ---

    [Fact]
    public async Task FinishReportsWhatTheTeamBranchChanged_AndKeepsTheBranch()
    {
        var workspace = await OpenAsync();
        var b1 = await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        await MergeFileAsync(b1, "Lib/B1.cs", "public class B1 { }\r\n");
        await workspace.CloseAsync();

        var outcome = await workspace.FinishAsync();

        outcome.TeamBranchKept.ShouldBeTrue();
        outcome.Changes.Select(c => (c.RelativePath, c.Kind)).ShouldBe([("Lib/B1.cs", TeamFileChangeKind.Added)]);
        (await RefAsync(TeamBranch)).ShouldNotBeNull();
    }

    [Fact]
    public async Task FinishDeletesTheTeamBranchWhenItChangedNothing()
    {
        var workspace = await OpenAsync();
        await workspace.AddWorktreeAsync("b1", workspace.Start, CancellationToken.None);
        await workspace.CloseAsync();

        var outcome = await workspace.FinishAsync();

        outcome.TeamBranchKept.ShouldBeFalse();
        outcome.Changes.ShouldBeEmpty();
        (await RefAsync(TeamBranch)).ShouldBeNull();
    }

    [Fact]
    public async Task ARunThatStartedFromABranchOffersThatBranchesWorkForReviewToo()
    {
        Git("checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Feature.cs"), "public class Feature { }\r\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "feature");
        Git("checkout", "-q", "main");
        var workspace = await OpenAsync("feature");
        await workspace.CloseAsync();

        var outcome = await workspace.FinishAsync();

        outcome.TeamBranchKept.ShouldBeTrue();
        outcome.Changes.Select(c => c.RelativePath).ShouldBe(["Lib/Feature.cs"]);
    }

    [Fact]
    public async Task DiscardingTheTeamBranchForARunThatDidNothingRemovesIt()
    {
        var workspace = await OpenAsync();
        (await RefAsync(TeamBranch)).ShouldNotBeNull();

        await workspace.DiscardTeamBranchAsync();

        (await RefAsync(TeamBranch)).ShouldBeNull();
    }

    [Fact]
    public async Task DiscardingBeforeOpeningIsANoOp()
    {
        await NewWorkspace().DiscardTeamBranchAsync();
    }

    [Fact]
    public async Task NotInTheCopyNamesWhatCouldNotBeLinkedPlusWhatABuildReportMissing()
    {
        var workspace = await OpenAsync();

        workspace.NotInTheCopy("Build succeeded.", Path.Combine(workspace.RunDirectory, "b1")).ShouldBeEmpty();
    }
}
