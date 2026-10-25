using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class TeamToolDispatcherTests : IDisposable
{
    private readonly TeamRepositoryFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    private static JsonNode Args(object values) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(values))!;

    private static Task<(bool IsError, string Text)> EditValueAsync(TeamToolDispatcher dispatcher, int value) =>
        dispatcher.ExecuteAsync(TeamTools.EditFileToolName, Args(new
        {
            file = "Lib/Class1.cs",
            mode = "replace_text",
            old_text = "public int Value => 1;",
            new_text = $"public int Value => {value};",
        }), CancellationToken.None);

    [Fact]
    public async Task EditFile_WritesToTheWorktreeOnly_KeepingItsLineEndings()
    {
        var (dispatcher, context, _) = await _repo.CreateWorkerAsync(1);

        var (isError, text) = await EditValueAsync(dispatcher, 2);

        isError.ShouldBeFalse(text);
        var edited = File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs"));
        edited.ShouldBe("public class Class1\r\n{\r\n    public int Value => 2;\r\n}\r\n");
        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
    }

    [Fact]
    public async Task EditFile_OutsideTheWorktree_IsRefused()
    {
        var (dispatcher, _, _) = await _repo.CreateWorkerAsync(1);

        var (isError, text) = await dispatcher.ExecuteAsync(TeamTools.EditFileToolName, Args(new
        {
            file = "../repo/Lib/Class1.cs",
            mode = "whole_file",
            new_text = "gone",
        }), CancellationToken.None);

        isError.ShouldBeTrue();
        text.ShouldContain("outside your worktree");
        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
    }

    /// <summary>The worktree's .git file names the repository git uses; rewriting it would point
    /// every later git call somewhere else, hooks and all.</summary>
    [Theory]
    [InlineData(".git")]
    [InlineData(".GIT/config")]
    [InlineData("Lib/.git/hooks/pre-commit")]
    public async Task EditFile_IntoGitMetadata_IsRefused(string path)
    {
        var (dispatcher, context, _) = await _repo.CreateWorkerAsync(1);
        var gitFileBefore = File.ReadAllText(Path.Combine(context.WorktreeRoot, ".git"));

        var (isError, text) = await dispatcher.ExecuteAsync(TeamTools.EditFileToolName, Args(new
        {
            file = path,
            mode = "whole_file",
            new_text = "gitdir: C:/somewhere/else",
        }), CancellationToken.None);

        isError.ShouldBeTrue();
        text.ShouldContain("git's own metadata");
        File.ReadAllText(Path.Combine(context.WorktreeRoot, ".git")).ShouldBe(gitFileBefore);
    }

    /// <summary>An MSBuild Exec target in a project file runs on the next build, with no prompt - so
    /// with shell commands off, build files are off limits too.</summary>
    [Fact]
    public async Task EditFile_OfABuildFile_IsRefusedWithShellOff_AndAllowedWithShellOn()
    {
        var edit = Args(new
        {
            file = "Lib/Lib.csproj",
            mode = "replace_text",
            old_text = "<Nullable>enable</Nullable>",
            new_text = "<Nullable>disable</Nullable>",
        });
        var (withoutShell, _, _) = await _repo.CreateWorkerAsync(1);
        var (withShell, _, _) = await _repo.CreateWorkerAsync(2, (_, _, _, _) => Task.FromResult((false, "ok")));

        var (refusedError, refused) = await withoutShell.ExecuteAsync(TeamTools.EditFileToolName, edit, CancellationToken.None);
        var (allowedError, allowed) = await withShell.ExecuteAsync(TeamTools.EditFileToolName, edit, CancellationToken.None);

        refusedError.ShouldBeTrue();
        refused.ShouldContain("defines how the solution builds");
        allowedError.ShouldBeFalse(allowed);
    }

    [Theory]
    [InlineData("A/B.csproj", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("build/Custom.targets", true)]
    [InlineData("Scratch.slnx", true)]
    [InlineData("NuGet.Config", true)]
    [InlineData("global.json", true)]
    [InlineData("Lib/Class1.cs", false)]
    [InlineData("appsettings.json", false)]
    public void IsBuildDefiningFile_CoversProjectSolutionAndSdkFiles(string path, bool expected)
    {
        TeamToolDispatcher.IsBuildDefiningFile(path).ShouldBe(expected);
    }

    [Fact]
    public async Task Merge_WithUncommittedChanges_IsRefused()
    {
        var (dispatcher, _, _) = await _repo.CreateWorkerAsync(1);
        await EditValueAsync(dispatcher, 2);

        var (isError, text) = await dispatcher.ExecuteAsync(TeamTools.MergeToolName, null, CancellationToken.None);

        isError.ShouldBeTrue();
        text.ShouldContain("team_commit first");
    }

    [Fact]
    public async Task CommitThenMerge_BuildsAndMovesTheTeamBranch_AndAnnouncesIt()
    {
        var (dispatcher, context, state) = await _repo.CreateWorkerAsync(1);
        await EditValueAsync(dispatcher, 2);
        (await dispatcher.ExecuteAsync(TeamTools.CommitToolName, Args(new { message = "Value is 2" }), CancellationToken.None))
            .IsError.ShouldBeFalse();

        var (isError, text) = await dispatcher.ExecuteAsync(TeamTools.MergeToolName, null, CancellationToken.None);

        isError.ShouldBeFalse(text);
        var workerHead = TeamRepositoryFixture.Git(context.WorktreeRoot, "rev-parse", "HEAD");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", _repo.TeamBranch).ShouldBe(workerHead);
        state.Merges.ShouldBe(1);
        state.LastGoodCommit.ShouldBe(workerHead);
        _repo.Bus.TakeChannel("w2").ShouldContain(m => m.From == "w1" && m.Text.StartsWith("Merged"));
    }

    [Fact]
    public async Task Merge_AfterAPeerMergedFirst_SaysToSync_AndSyncThenMergeSucceeds()
    {
        var (first, _, _) = await _repo.CreateWorkerAsync(1);
        var (second, _, _) = await _repo.CreateWorkerAsync(2);

        await EditValueAsync(first, 2);
        await first.ExecuteAsync(TeamTools.CommitToolName, Args(new { message = "w1" }), CancellationToken.None);
        (await first.ExecuteAsync(TeamTools.MergeToolName, null, CancellationToken.None)).IsError.ShouldBeFalse();

        await second.ExecuteAsync(TeamTools.EditFileToolName, Args(new
        {
            file = "Lib/Class2.cs",
            mode = "whole_file",
            new_text = "public class Class2 { }\n",
        }), CancellationToken.None);
        await second.ExecuteAsync(TeamTools.CommitToolName, Args(new { message = "w2" }), CancellationToken.None);

        var (refusedError, refused) = await second.ExecuteAsync(TeamTools.MergeToolName, null, CancellationToken.None);
        refusedError.ShouldBeTrue();
        refused.ShouldContain("team_sync");

        (await second.ExecuteAsync(TeamTools.SyncToolName, null, CancellationToken.None)).IsError.ShouldBeFalse();
        var (mergedError, merged) = await second.ExecuteAsync(TeamTools.MergeToolName, null, CancellationToken.None);

        mergedError.ShouldBeFalse(merged);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{_repo.TeamBranch}:Lib/Class1.cs").ShouldContain("Value => 2;");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{_repo.TeamBranch}:Lib/Class2.cs").ShouldContain("Class2");
    }

    [Fact]
    public async Task Merge_OfACommitThatDoesNotBuild_IsRefused_AndTheTeamBranchStays()
    {
        var (dispatcher, _, _) = await _repo.CreateWorkerAsync(1);
        var teamBefore = TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", _repo.TeamBranch);
        await dispatcher.ExecuteAsync(TeamTools.EditFileToolName, Args(new
        {
            file = "Lib/Class1.cs",
            mode = "replace_text",
            old_text = "public int Value => 1;",
            new_text = "public int Value => ;",
        }), CancellationToken.None);
        await dispatcher.ExecuteAsync(TeamTools.CommitToolName, Args(new { message = "broken" }), CancellationToken.None);

        var (isError, text) = await dispatcher.ExecuteAsync(TeamTools.MergeToolName, null, CancellationToken.None);

        isError.ShouldBeTrue();
        text.ShouldContain("does not build");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", _repo.TeamBranch).ShouldBe(teamBefore);
    }

    [Fact]
    public async Task Done_WithUnmergedWork_WarnsOnce_ThenAccepts()
    {
        var (dispatcher, _, state) = await _repo.CreateWorkerAsync(1);
        _repo.Board.Write("w1", "CLAIM", "Class1 value", null);
        await EditValueAsync(dispatcher, 2);

        var (warnedError, warned) = await dispatcher.ExecuteAsync(
            TeamTools.DoneToolName, Args(new { summary = "gave up" }), CancellationToken.None);
        warnedError.ShouldBeTrue();
        warned.ShouldContain("uncommitted changes");
        state.IsDone.ShouldBeFalse();

        var (doneError, _) = await dispatcher.ExecuteAsync(
            TeamTools.DoneToolName, Args(new { summary = "gave up" }), CancellationToken.None);

        doneError.ShouldBeFalse();
        state.IsDone.ShouldBeTrue();
        _repo.Board.ActiveClaims().ShouldBeEmpty();
    }

    [Fact]
    public async Task BoardWrite_OfAClaim_ShowsAsTheWorkersCurrentClaim()
    {
        var (dispatcher, _, state) = await _repo.CreateWorkerAsync(1);

        await dispatcher.ExecuteAsync(
            TeamTools.BoardWriteToolName, Args(new { type = "CLAIM", text = "Class1: make Value configurable" }), CancellationToken.None);

        state.CurrentClaim.ShouldBe("Class1: make Value configurable");
        state.HadVisibleActivityThisCycle.ShouldBeTrue();
    }

    [Fact]
    public async Task DrainNotices_DeliversADirectMessageFromAPeer()
    {
        var (dispatcher, _, _) = await _repo.CreateWorkerAsync(1);
        _repo.Bus.SendDirect("w2", "w1", "I'm on Class1 too");

        dispatcher.DrainNotices()!.ShouldContain("DM from w2: I'm on Class1 too");
        dispatcher.DrainNotices().ShouldBeNull();
    }

    [Fact]
    public async Task RunCommand_IsOnlyOffered_WhenTheRunAllowsIt_AndRunsInTheWorktree()
    {
        var (withoutShell, _, _) = await _repo.CreateWorkerAsync(1);
        withoutShell.Tools.ShouldNotContain(t => t.Name == "run_command");

        string? workingDirectory = null;
        var (withShell, context, _) = await _repo.CreateWorkerAsync(2, (_, _, directory, _) =>
        {
            workingDirectory = directory;
            return Task.FromResult((false, "ok"));
        });

        withShell.Tools.ShouldContain(t => t.Name == "run_command");
        await withShell.ExecuteAsync("run_command", Args(new { command = "dir", reason = "look" }), CancellationToken.None);
        workingDirectory.ShouldBe(context.SolutionDirectory);
    }
}
