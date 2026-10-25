using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;
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

    private UltraTeamRun CreateRun(int workers, TimeSpan budget) => new(
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
        runCommand: null);

    private Task<UltraTeamResult> RunAsync(UltraTeamRun run, CancellationToken cancellationToken = default) =>
        run.RunAsync(
            "Make Class1.Value return 2.",
            _repo.RepositoryRoot,
            Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
            [],
            cancellationToken);

    private static string Worktree(UltraTeamRun run, string worker, string root) => Path.Combine(root, run.RunId, worker);

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
