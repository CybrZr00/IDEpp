using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class UltraTeamRunTests : IDisposable
{
    private readonly TeamRepositoryFixture _repo = new();
    private readonly ScriptedTeamChatClient _client = new();

    public void Dispose() => _repo.Dispose();

    private string UltraRoot => Path.Combine(_repo.Root, "ultra");

    private UltraTeamRun CreateRun(int workers, TimeSpan budget, Func<CancellationToken, Task<bool>>? askToContinue = null) => new(
        _client,
        "model-x",
        new UltraTeamOptions(workers, budget, ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
        {
            StaggerDelay = TimeSpan.Zero,
            HardStopGrace = TimeSpan.FromSeconds(5),
        },
        _repo.ReadOnlyTools,
        new GitService(),
        _repo.TeamGit,
        TimeProvider.System,
        UltraRoot,
        runCommand: null,
        askToContinue);

    private Task<UltraTeamResult> RunAsync(UltraTeamRun run, CancellationToken cancellationToken = default, string? startFrom = null) =>
        run.RunAsync(
            "Make Class1.Value return 2.",
            _repo.RepositoryRoot,
            Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
            [],
            cancellationToken,
            startFrom);

    private static string Worktree(UltraTeamRun run, string worker, string root) => Path.Combine(root, run.RunId, worker);

    /// <summary>A branch off main with one more file, as an earlier run's team branch would be -
    /// the repository is left on main with its files unchanged.</summary>
    private void CreateEarlierBranch(string name, string relativePath, string content)
    {
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "-c", name);
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, relativePath), content);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "add", "-A");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "commit", "-m", "earlier work");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "main");
    }

    [Fact]
    public async Task ARunStartedFromAnEarlierBranch_HasItsWorkInEveryWorktree_AndOffersItForReview()
    {
        CreateEarlierBranch("earlier", "Lib/Earlier.cs", "public class Earlier { }\n");
        _client.Script("w1",
            _client.Call(WorkspaceTools.ReadFileToolName, new { file = "Lib/Earlier.cs" }),
            _client.Call(TeamTools.DoneToolName, new { summary = "it was already done" }));
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10));

        var result = await RunAsync(run, startFrom: "earlier");

        run.StartedFrom.ShouldBe("earlier");
        var requests = _client.Requests.Select(r => r.Messages).ToList();
        requests[0][1].Content.ShouldContain("carries on from `earlier`");
        requests[1].ShouldContain(m => m.Role == ChatRole.Tool && m.Content.Contains("class Earlier"));

        // Compared against the user's tree, so the earlier branch's work is offered too.
        result.Changes.ShouldContain(new TeamFileChange("Lib/Earlier.cs", TeamFileChangeKind.Added));
        result.TeamBranchKept.ShouldBeTrue();
        File.Exists(Path.Combine(_repo.RepositoryRoot, "Lib", "Earlier.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task ARunStartedFromABranchThatConflictsWithTheWorkingTree_IsRefusedBeforeAnythingIsCreated()
    {
        CreateEarlierBranch("earlier", "Lib/Class1.cs", "public class Class1\r\n{\r\n    public int Value => 2;\r\n}\r\n");
        File.WriteAllText(
            Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs"),
            "public class Class1\r\n{\r\n    public int Value => 3;\r\n}\r\n");
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(run, startFrom: "earlier"));

        error.Message.ShouldContain("Lib/Class1.cs");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", "ideplusplus/ultra/*").ShouldBeEmpty();
        _client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATeamThatFinishesTheGoal_EndsEarly_WithTheChangeOnTheTeamBranch_AndCleansUp()
    {
        _client
            .Script("w1",
                _client.Call(TeamTools.BoardWriteToolName, new { type = "CLAIM", text = "Class1.Value -> 2" }),
                _client.Call(TeamTools.EditFileToolName, new
                {
                    file = "Lib/Class1.cs", mode = "replace_text",
                    old_text = "public int Value => 1;", new_text = "public int Value => 2;",
                }),
                _client.Call(TeamTools.CommitToolName, new { message = "Value is 2" }),
                _client.Call(TeamTools.MergeToolName),
                _client.Call(TeamTools.BoardWriteToolName, new
                {
                    type = "PATCH_SUMMARY", text = "files=Lib/Class1.cs | idea=Value 2 | evidence=build ok",
                }),
                _client.Call(TeamTools.DoneToolName, new { summary = "Value is 2" }))
            .Script("w2", _client.Call(TeamTools.DoneToolName, new { summary = "w1 has it" }));
        var run = CreateRun(workers: 2, budget: TimeSpan.FromMinutes(10));

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.TeamBranchKept.ShouldBeTrue();
        result.Changes.ShouldHaveSingleItem().ShouldBe(new TeamFileChange("Lib/Class1.cs", TeamFileChangeKind.Modified));
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{result.TeamBranch}:Lib/Class1.cs").ShouldContain("Value => 2;");
        result.Board.ShouldContain(e => e.Type == TeamBoardEntryType.PatchSummary && e.Author == "w1");
        result.PromptTokens.ShouldBeGreaterThan(0);

        // The user's checkout is untouched, and nothing of the workers is left behind but the team branch.
        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", "ideplusplus/ultra/*")
            .Trim().ShouldBe(result.TeamBranch);
        File.Exists(Path.Combine(UltraRoot, run.RunId, "board.jsonl")).ShouldBeTrue();
    }

    /// <summary>Deleting every worker branch at the end used to throw away whatever had not been
    /// merged - on a run whose merge gate never passed, all of it.</summary>
    [Fact]
    public async Task WorkThatNeverReachedTheTeamBranch_IsKeptOnTheWorkersBranch_CommittedIfItWasNot()
    {
        _client
            .Script("w1",
                _client.Call(TeamTools.EditFileToolName, new
                {
                    file = "Lib/Committed.cs", mode = "whole_file", new_text = "public class Committed { }\n",
                }),
                _client.Call(TeamTools.CommitToolName, new { message = "committed, never merged" }),
                _client.Call(TeamTools.DoneToolName, new { summary = "stopping" }),
                _client.Call(TeamTools.DoneToolName, new { summary = "stopping" }))
            .Script("w2",
                _client.Call(TeamTools.EditFileToolName, new
                {
                    file = "Lib/Uncommitted.cs", mode = "whole_file", new_text = "public class Uncommitted { }\n",
                }),
                _client.Call(TeamTools.DoneToolName, new { summary = "stopping" }),
                _client.Call(TeamTools.DoneToolName, new { summary = "stopping" }))
            .Script("w3", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));
        var run = CreateRun(workers: 3, budget: TimeSpan.FromMinutes(10));

        var result = await RunAsync(run);

        var w1 = TeamGitWorkspace.GetWorkerBranchName(run.RunId, 1);
        var w2 = TeamGitWorkspace.GetWorkerBranchName(run.RunId, 2);
        result.KeptWorkerBranches.ShouldBe([w1, w2], ignoreOrder: true);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{w1}:Lib/Committed.cs").ShouldContain("Committed");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{w2}:Lib/Uncommitted.cs").ShouldContain("Uncommitted");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "log", "-1", "--format=%s", w2).ShouldContain("Uncommitted work");

        // w3 had nothing of its own, so its branch is gone; nothing was merged, so the team branch is too.
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", TeamGitWorkspace.GetWorkerBranchName(run.RunId, 3))
            .ShouldBeEmpty();
        result.TeamBranchKept.ShouldBeFalse();
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
    }

    [Fact]
    public async Task EachWorkerIsToldWhoItIs_AndSeesTheGoalAndTheBoard()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));
        _client.Script("w2", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));

        await RunAsync(CreateRun(workers: 2, budget: TimeSpan.FromMinutes(10)));

        var firstRequests = _client.Requests.GroupBy(r => r.Worker).ToDictionary(g => g.Key, g => g.First().Messages);
        firstRequests.Keys.ShouldBe(["w1", "w2"], ignoreOrder: true);
        firstRequests["w2"][0].Content.ShouldStartWith("You are w2, one of 2 equal workers");
        firstRequests["w2"][1].Content.ShouldContain("Make Class1.Value return 2.");
        firstRequests["w2"][1].Content.ShouldContain("Board now:");
    }

    [Fact]
    public async Task ARunWithNoMerges_DeletesItsTeamBranch()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));

        var result = await RunAsync(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10)));

        result.Changes.ShouldBeEmpty();
        result.TeamBranchKept.ShouldBeFalse();
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", "ideplusplus/ultra/*").ShouldBeEmpty();
    }

    [Fact]
    public async Task AWorkerThatNeverCallsATool_IsStopped_RatherThanCyclingUntilTheDeadline()
    {
        var result = await RunAsync(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10)));

        result.EndReason.ShouldBe(UltraTeamEndReason.WorkersStopped);
        _client.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task WhenTimeRunsOut_WorkersStopBetweenSteps_AndTheRunEndsAsTimeUp()
    {
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);

        var started = DateTime.UtcNow;
        var run = CreateRun(workers: 2, budget: TimeSpan.FromSeconds(2));
        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.TimeUp);
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(30));
        result.Workers.ShouldAllBe(w => w.Phase == TeamWorkerPhase.Stopped);
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
    }

    [Fact]
    public async Task WhenTimeRunsOut_AndTheUserContinues_TheSameTeamWorksAnotherRound()
    {
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);
        var asks = 0;
        var requestsWhenAsked = new List<int>();
        UltraTeamRun? run = null;
        run = CreateRun(workers: 2, budget: TimeSpan.FromSeconds(2), askToContinue: _ =>
        {
            asks++;
            requestsWhenAsked.Add(_client.Requests.Count);
            // Every worker has stopped between steps before the question is asked.
            run!.Workers.ShouldAllBe(w => w.Phase == TeamWorkerPhase.Stopped);
            run.IsAwaitingContinue.ShouldBeTrue();
            return Task.FromResult(asks == 1);
        });

        var result = await RunAsync(run);

        asks.ShouldBe(2);
        run.Round.ShouldBe(2);
        result.EndReason.ShouldBe(UltraTeamEndReason.TimeUp);
        // The second round did real work: more requests by the second question than the first.
        requestsWhenAsked[1].ShouldBeGreaterThan(requestsWhenAsked[0]);
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
    }

    [Fact]
    public async Task Stopping_WhileTheRunAsksWhetherToContinue_EndsItAsCancelled()
    {
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);
        using var stop = new CancellationTokenSource();
        var run = CreateRun(workers: 1, budget: TimeSpan.FromSeconds(1), askToContinue: async ct =>
        {
            await stop.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        });

        var result = await RunAsync(run, stop.Token);

        result.EndReason.ShouldBe(UltraTeamEndReason.Cancelled);
        run.Round.ShouldBe(1);
    }

    [Fact]
    public async Task ARunThatFinishesInTime_NeverAsksToContinue()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));
        var asked = false;

        var result = await RunAsync(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10), askToContinue: _ =>
        {
            asked = true;
            return Task.FromResult(true);
        }));

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        asked.ShouldBeFalse();
    }

    [Fact]
    public async Task TheCard_AsksToContinue_AndAnswersWithTheButtonPressed()
    {
        var card = new TeamRunViewModel(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(30)), isLocalProvider: false, discardBranches: null);

        var keepGoing = card.AskToContinueAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        card.IsAwaitingContinue.ShouldBeTrue();
        card.ContinueLabel.ShouldBe("Keep going (+30 min)");
        card.KeepGoingCommand.Execute(null);

        (await keepGoing).ShouldBeTrue();
        card.IsAwaitingContinue.ShouldBeFalse();

        using var stop = new CancellationTokenSource();
        var stopped = card.AskToContinueAsync(TimeSpan.FromMinutes(30), stop.Token);
        await stop.CancelAsync();
        (await stopped).ShouldBeFalse();

        var finish = card.AskToContinueAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        card.FinishNowCommand.Execute(null);
        (await finish).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_EndsTheRunAsCancelled_AndStillRemovesTheWorktrees()
    {
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var run = CreateRun(workers: 2, budget: TimeSpan.FromMinutes(10));

        var result = await RunAsync(run, cancel.Token);

        result.EndReason.ShouldBe(UltraTeamEndReason.Cancelled);
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
        Directory.Exists(Worktree(run, "w2", UltraRoot)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(30, 24, 28)]
    [InlineData(5, 4, 4.5)]
    public void Reminders_ComeAt80Percent_ThenTwoMinutesOrTenPercentBeforeTheEnd(
        double budgetMinutes, double deadlineMinutes, double finalMinutes)
    {
        var (deadline, final) = UltraTeamRun.ReminderSchedule(TimeSpan.FromMinutes(budgetMinutes));

        deadline.ShouldBe(TimeSpan.FromMinutes(deadlineMinutes));
        final.ShouldBe(TimeSpan.FromMinutes(finalMinutes));
    }
}
