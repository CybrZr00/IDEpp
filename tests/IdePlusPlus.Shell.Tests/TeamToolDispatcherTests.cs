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

    /// <summary>Each of these names a different file to Windows than to a plain string check - GIT~1
    /// is the .git file's 8.3 name, a trailing dot is dropped, ::$DATA is the file itself.</summary>
    [Theory]
    [InlineData("GIT~1")]
    [InlineData(".git.")]
    [InlineData("Lib/Lib.csproj::$DATA")]
    [InlineData("Lib /Class1.cs")]
    public async Task EditFile_ThroughAWindowsPathAlias_IsRefused(string path)
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
        text.ShouldContain("Refused");
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
    [InlineData("Lib/Lib.csproj.user", true)]
    [InlineData("Directory.Build.rsp", true)]
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

    /// <summary>A 16-worker run's merge-gate builds were "stopped after 5 minutes" without ever
    /// starting: the timeout ran while the build queued for one of the team's shared slots.</summary>
    [Fact]
    public async Task ABuildThatQueuesLongerThanItsTimeout_StillGetsItsWholeTimeToRun()
    {
        using var slots = new SemaphoreSlim(1);
        var timeout = TimeSpan.FromSeconds(30);
        var (dispatcher, _, _) = await _repo.CreateWorkerAsync(1, buildTimeout: timeout, buildSlots: slots);
        await slots.WaitAsync();

        var build = dispatcher.ExecuteAsync("build_solution", null, CancellationToken.None);
        await Task.Delay(timeout + TimeSpan.FromSeconds(5));
        build.IsCompleted.ShouldBeFalse();
        slots.Release();
        var (isError, text) = await build;

        isError.ShouldBeFalse(text);
        text.ShouldStartWith("Build succeeded");
        text.ShouldContain("for a free build slot");
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

    // ---- Role-scoped tools and edit scopes ----

    [Fact]
    public async Task AnAdversary_WritesTestsOnly_NeverMerges_AndCanReportFindings()
    {
        var (adversary, _, _) = await _repo.CreateWorkerAsync(1, role: TeamRoles.Adversary);
        // team_next_review is only offered when the run has a queue to answer it; give it one.
        adversary.NextReview = _ => Task.FromResult((false, "nothing queued for the adv lane"));

        // Its edit scope is TestsOnly: the failing test it commits as evidence has to be written
        // somewhere, but production code is out of reach - and nothing of its own is ever merged.
        adversary.Tools.ShouldContain(t => t.Name == TeamTools.EditFileToolName);
        adversary.Tools.ShouldNotContain(t => t.Name == TeamTools.MergeToolName);
        adversary.Tools.ShouldContain(t => t.Name == TeamTools.ReportFindingToolName);
        adversary.Tools.ShouldContain(t => t.Name == TeamTools.NextReviewToolName);

        var (editError, editRefusal) = await EditValueAsync(adversary, 2);
        editError.ShouldBeTrue();
        editRefusal.ShouldContain("writes tests only");
        editRefusal.ShouldContain("no test project");

        var (mergeError, mergeRefusal) = await adversary.ExecuteAsync(TeamTools.MergeToolName, null, CancellationToken.None);
        mergeError.ShouldBeTrue();
        mergeRefusal.ShouldContain("branch:<sha>");

        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
    }

    [Fact]
    public async Task ABuilder_IsNotOfferedTheSpecialistTools_AndCallingOneIsRefused()
    {
        var (builder, _, _) = await _repo.CreateWorkerAsync(1);

        builder.Tools.ShouldNotContain(t => t.Name == TeamTools.NextReviewToolName);
        builder.Tools.ShouldNotContain(t => t.Name == TeamTools.ReportFindingToolName);
        builder.Tools.ShouldNotContain(t => t.Name == TeamTools.RunMutationTestsToolName);

        var (isError, text) = await builder.ExecuteAsync(
            TeamTools.ReportFindingToolName, Args(new { severity = "low", kind = "mutation", text = "x" }), CancellationToken.None);

        isError.ShouldBeTrue();
        text.ShouldContain("not one of the tools");
    }

    [Fact]
    public async Task TheMutationTester_GetsRunMutationTests_AndTheConformityLaneDoesNot()
    {
        var (mutation, _, _) = await _repo.CreateWorkerAsync(1, role: TeamRoles.MutationTester);
        var (lint, _, _) = await _repo.CreateWorkerAsync(2, role: TeamRoles.Conformity);

        mutation.Tools.ShouldContain(t => t.Name == TeamTools.RunMutationTestsToolName);
        mutation.Tools.ShouldContain(t => t.Name == TeamTools.EditFileToolName);
        lint.Tools.ShouldNotContain(t => t.Name == TeamTools.RunMutationTestsToolName);
        lint.Tools.ShouldContain(t => t.Name == TeamTools.CheckConformityToolName);
        lint.Tools.ShouldContain(t => t.Name == TeamTools.MergeToolName);

        // The scratch repository has no test project, so the run refuses before touching the file -
        // which is the tool being routed, not a refusal by the role.
        var (isError, text) = await mutation.ExecuteAsync(
            TeamTools.RunMutationTestsToolName, Args(new { file = "Lib/Class1.cs" }), CancellationToken.None);
        isError.ShouldBeTrue();
        text.ShouldContain("No test projects");
        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
    }

    [Fact]
    public async Task TestsOnly_RefusesAnEditOutsideTheTestProjects()
    {
        var (mutation, context, _) = await _repo.CreateWorkerAsync(1, role: TeamRoles.MutationTester);

        var (isError, text) = await EditValueAsync(mutation, 2);

        isError.ShouldBeTrue();
        text.ShouldContain("writes tests only");
        File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
    }

    [Fact]
    public async Task ChangedFilesStyleOnly_AllowsTheReviewedFile_AndRefusesAnother()
    {
        var (lint, context, _) = await _repo.CreateWorkerAsync(1, role: TeamRoles.Conformity);

        // Before an item is taken the set of files is unknown, so every edit is refused.
        var (beforeError, before) = await EditValueAsync(lint, 2);
        beforeError.ShouldBeTrue();
        before.ShouldContain("team_next_review first");

        lint.CurrentReviewFiles = [Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs")];
        File.WriteAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class2.cs"), "public class Class2 { }\r\n");

        var (otherError, other) = await lint.ExecuteAsync(TeamTools.EditFileToolName, Args(new
        {
            file = "Lib/Class2.cs",
            mode = "replace_text",
            old_text = "public class Class2 { }",
            new_text = "public class Class2 { public int Other => 2; }",
        }), CancellationToken.None);
        otherError.ShouldBeTrue();
        other.ShouldContain("only the files its review item changed");

        var (allowedError, allowed) = await EditValueAsync(lint, 2);
        allowedError.ShouldBeFalse(allowed);
        File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs")).ShouldContain("Value => 2;");
    }

    [Fact]
    public async Task ReportFinding_PostsALaneTaggedFinding_AndAHighOneNeedsEvidence()
    {
        var (adversary, _, _) = await _repo.CreateWorkerAsync(1, role: TeamRoles.Adversary);
        _repo.Board.Write("w1", "PATCH_SUMMARY", "files=Lib/Class1.cs | idea=value | evidence=build", null);

        var (highError, highText) = await adversary.ExecuteAsync(TeamTools.ReportFindingToolName, Args(new
        {
            severity = "high",
            kind = "security",
            text = "path traversal in the reader",
        }), CancellationToken.None);
        highError.ShouldBeTrue();
        highText.ShouldContain("needs evidence");

        var (isError, text) = await adversary.ExecuteAsync(TeamTools.ReportFindingToolName, Args(new
        {
            severity = "medium",
            kind = "robustness",
            text = "empty input throws",
            files = new[] { "Lib/Class1.cs" },
        }), CancellationToken.None);

        isError.ShouldBeFalse(text);
        var finding = _repo.Board.Snapshot().Where(e => e.Type == TeamBoardEntryType.Finding).ShouldHaveSingleItem();
        finding.Lane.ShouldBe("adv");
        finding.Text.ShouldContain("severity=medium");
        finding.Text.ShouldContain("evidence=");
    }

    [Fact]
    public async Task MergeLanded_IsCalledWithTheRangeThatLanded()
    {
        var (dispatcher, context, _) = await _repo.CreateWorkerAsync(1);
        var landed = new List<MergeLandedEvent>();
        dispatcher.MergeLanded = (e, _) => { landed.Add(e); return Task.CompletedTask; };

        await EditValueAsync(dispatcher, 2);
        await dispatcher.ExecuteAsync(TeamTools.CommitToolName, Args(new { message = "Value is 2" }), CancellationToken.None);
        (await dispatcher.ExecuteAsync(TeamTools.MergeToolName, null, CancellationToken.None)).IsError.ShouldBeFalse();

        var single = landed.ShouldHaveSingleItem();
        single.Author.ShouldBe("w1");
        single.To.ShouldBe(TeamRepositoryFixture.Git(context.WorktreeRoot, "rev-parse", "HEAD"));
        single.From.ShouldNotBe(single.To);
    }
}
