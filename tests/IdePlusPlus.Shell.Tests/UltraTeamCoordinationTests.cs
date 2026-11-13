using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// How an Ultra team stays out of its own way, from a 100-minute, 37-million-token run that built a
/// small change: a worker claimed a task that could not start yet and polled the board for an hour, a
/// peer judged it stalled and redid its work, and the team went on auditing for 28 minutes after the
/// last planned task was merged.
///
/// These drive the run's own rules directly - who may claim what, what team_wait wakes on, when the team
/// is told to wrap up - rather than through scripted model replies, since the rules do not depend on any.
/// </summary>
public sealed class UltraTeamCoordinationTests : IDisposable
{
    private readonly TeamRepositoryFixture _repo = new();
    private readonly ScriptedTeamChatClient _client = new();
    private readonly ShiftableClock _clock = new();

    public void Dispose() => _repo.Dispose();

    /// <summary>The real clock plus an offset the test moves, so a claim can lapse and a worker go quiet
    /// without anyone waiting. Timers stay real, which a short team_wait relies on.</summary>
    private sealed class ShiftableClock : TimeProvider
    {
        private long _shiftTicks;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _shiftTicks));

        public void Advance(TimeSpan by) => Interlocked.Add(ref _shiftTicks, by.Ticks);
    }

    private UltraTeamRun CreateRun(int workers = 3, TimeSpan? budget = null) => new(
        _client,
        "model-x",
        new UltraTeamOptions(workers, budget ?? TimeSpan.FromMinutes(10), ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
        {
            PreflightBuild = false,
            ClaimLifetime = TimeSpan.FromMinutes(10),
            PeerActiveWindow = TimeSpan.FromMinutes(10),
        },
        _repo.ReadOnlyTools,
        new GitService(),
        _repo.TeamGit,
        _clock,
        Path.Combine(_repo.Root, "ultra"),
        runCommand: null);

    private static string Merged(string taskId) => $"files=x | idea={taskId} done | evidence=build ok";

    private static TeamWorkerState Worker(UltraTeamRun run, string name) => run.Workers.Single(w => w.Name == name);

    /// <summary>The worker is mid-task: working, and just took a step.</summary>
    private static void Working(TeamWorkerState worker)
    {
        worker.Phase = TeamWorkerPhase.Working;
        worker.RecordStep("board_read");
    }

    // ---- Claims ----

    [Fact]
    public void ABlockedTask_CannotBeClaimed_UntilWhatItWaitsForIsMerged()
    {
        var run = CreateRun();
        run.Board.Write("lead", "TASK", "Add the shared helper", null);
        run.Board.Write("lead", "TASK", "Document the helper", null, [1]);

        var refusal = run.ClaimRefusal("w3", "G2: docs for the helper");

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("G2 is blocked");
        refusal.ShouldContain("G1 (not started)");
        refusal.ShouldContain("team_wait");

        run.Board.Write("w1", "CLAIM", "G1: the helper", null);
        run.ClaimRefusal("w3", "G2: docs").ShouldNotBeNull().ShouldContain("G1 (being worked on)");

        run.Board.Write("w1", "PATCH_SUMMARY", Merged("G1"), null);
        run.ClaimRefusal("w3", "G2: docs").ShouldBeNull("what it waited for is merged");
    }

    [Fact]
    public void ATaskAnActivePeerHolds_CannotBeTakenOver_EvenAfterTheirClaimHasLapsed()
    {
        var run = CreateRun();
        run.Board.Write("lead", "TASK", "Update the docs", null);
        Working(Worker(run, "w3"));
        run.Board.Write("w3", "CLAIM", "G1: the docs", null);

        // Twenty-five minutes on, w3 has said nothing new - the claim has lapsed - but it has been taking
        // steps all along: waiting for its prerequisite, re-reading the board.
        _clock.Advance(TimeSpan.FromMinutes(25));
        run.Board.ActiveClaims().ShouldBeEmpty("the claim lapsed on its timer");
        Working(Worker(run, "w3"));

        var refusal = run.ClaimRefusal("w2", "G1: taking over from w3");

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("G1 is held by w3, who is still working");
        refusal.ShouldContain("team_dm");
    }

    [Fact]
    public void ATaskCanBeTakenOver_OnceItsHolderIsGone_Quiet_Done_OrFinishedWithIt()
    {
        var run = CreateRun();
        run.Board.Write("lead", "TASK", "Update the docs", null);
        Working(Worker(run, "w3"));
        run.Board.Write("w3", "CLAIM", "G1: the docs", null);
        run.ClaimRefusal("w2", "G1: the docs").ShouldNotBeNull();

        // Quiet for longer than the window: no step in eleven minutes is a worker that has stopped.
        _clock.Advance(TimeSpan.FromMinutes(11));
        run.ClaimRefusal("w2", "G1: the docs").ShouldBeNull("w3 has gone quiet");

        Working(Worker(run, "w3"));
        run.ClaimRefusal("w2", "G1: the docs").ShouldNotBeNull("w3 is stepping again");

        Worker(run, "w3").Phase = TeamWorkerPhase.Done;
        run.ClaimRefusal("w2", "G1: the docs").ShouldBeNull("w3 is done");

        Working(Worker(run, "w3"));
        run.Board.Write("w3", "PATCH_SUMMARY", Merged("G1"), null);
        run.ClaimRefusal("w2", "G1: follow-up").ShouldBeNull("w3 merged it: a follow-up naming G1 is allowed");
    }

    [Fact]
    public void ThingsThatAreNotATakeover_AreNeverRefused()
    {
        var run = CreateRun();
        run.Board.Write("lead", "TASK", "Update the docs", null);
        run.Board.Write("lead", "TASK", "Fix the build", null);
        Working(Worker(run, "w3"));
        run.Board.Write("w3", "CLAIM", "G1: the docs", null);

        run.ClaimRefusal("w2", "w2 verifying the build after G1").ShouldBeNull("G1 is only mentioned");
        run.ClaimRefusal("w2", "G2: fix the build").ShouldBeNull("a different task");
        run.ClaimRefusal("w2", "G99: no such task").ShouldBeNull("not a task on the board");
        run.ClaimRefusal("w3", "G1: still the docs").ShouldBeNull("its own task");

        // A worker whose latest claim moved on to something else no longer holds the old one.
        run.Board.Write("w3", "CLAIM", "G2: the build instead", null);
        run.ClaimRefusal("w2", "G1: the docs").ShouldBeNull("w3 moved on from G1");
    }

    [Fact]
    public async Task TheDispatcher_RefusesAGuardedClaim_WithoutWritingIt_AndWritesAnUnguardedOne()
    {
        var (dispatcher, _, _) = await _repo.CreateWorkerAsync(1, claimGuard: text => text.StartsWith("G1", StringComparison.Ordinal) ? "G1 is blocked." : null);

        var refused = await dispatcher.ExecuteAsync(
            TeamTools.BoardWriteToolName, JsonNode.Parse("""{"type":"CLAIM","text":"G1: the docs"}"""), CancellationToken.None);
        var allowed = await dispatcher.ExecuteAsync(
            TeamTools.BoardWriteToolName, JsonNode.Parse("""{"type":"CLAIM","text":"G2: something else"}"""), CancellationToken.None);
        var notAClaim = await dispatcher.ExecuteAsync(
            TeamTools.BoardWriteToolName, JsonNode.Parse("""{"type":"FACT","text":"G1: is a fact about the docs"}"""), CancellationToken.None);

        refused.ShouldBe((true, "G1 is blocked."));
        allowed.IsError.ShouldBeFalse();
        notAClaim.IsError.ShouldBeFalse("only a claim is guarded");
        _repo.Board.Snapshot().Select(e => e.Text).ShouldBe(["G2: something else", "G1: is a fact about the docs"]);
    }

    [Fact]
    public async Task ATaskWrittenByAWorker_CanSayWhatItWaitsFor_AndAnUnknownIdIsRefused()
    {
        var (dispatcher, _, _) = await _repo.CreateWorkerAsync(1);
        _repo.Board.Write("lead", "TASK", "Foundation", null);

        var ok = await dispatcher.ExecuteAsync(
            TeamTools.BoardWriteToolName, JsonNode.Parse("""{"type":"TASK","text":"Docs","after":["G1"]}"""), CancellationToken.None);
        var bad = await dispatcher.ExecuteAsync(
            TeamTools.BoardWriteToolName, JsonNode.Parse("""{"type":"TASK","text":"More","after":["G9"]}"""), CancellationToken.None);

        ok.IsError.ShouldBeFalse();
        _repo.Board.Snapshot().Single(e => e.Text == "Docs").After.ShouldBe([1]);
        bad.IsError.ShouldBeTrue();
        bad.Text.ShouldContain("G9 is not a TASK");
    }

    // ---- team_wait ----

    [Fact]
    public async Task Waiting_EndsWhenAPeerMerges_OrPostsAFail_OrAddsATask_NotWhenItIsTheWorkerItself()
    {
        var run = CreateRun();

        async Task<string> WakesOn(Action change)
        {
            var wait = run.WaitForChangeAsync("w1", TimeSpan.FromSeconds(30), CancellationToken.None);
            await Task.Delay(100);
            wait.IsCompleted.ShouldBeFalse("nothing has changed yet");
            change();
            var (isError, text) = await wait.WaitAsync(TimeSpan.FromSeconds(10));
            isError.ShouldBeFalse();
            return text;
        }

        (await WakesOn(() => run.Board.Write("w2", "PATCH_SUMMARY", Merged("G1"), null))).ShouldContain("[PATCH_SUMMARY] w2");
        (await WakesOn(() => run.Board.Write("w2", "FAIL", "the cache approach", null))).ShouldContain("[FAIL] w2");
        (await WakesOn(() => run.Board.Write("lead", "TASK", "A follow-up", null))).ShouldContain("[TASK] lead");

        // What woke it counts as seen: waiting again straight away does not hand it the same entry back.
        var again = run.WaitForChangeAsync("w1", TimeSpan.FromMilliseconds(300), CancellationToken.None);
        (await again).Text.ShouldContain("Nothing changed");

        // Its own writes, and entries nobody needs to act on, do not wake it.
        var wait = run.WaitForChangeAsync("w1", TimeSpan.FromMilliseconds(400), CancellationToken.None);
        run.Board.Write("w1", "PATCH_SUMMARY", Merged("G9"), null);
        run.Board.Write("w2", "FACT", "an observation", null);
        run.Board.Write("w2", "CLAIM", "G1: something", null);
        (await wait).Text.ShouldContain("Nothing changed");
    }

    [Fact]
    public async Task Waiting_EndsOnAMessageToTheWorker_AChannelPost_OrARunnerBroadcast_ButNotOnSomeoneElsesDirectMessage()
    {
        var run = CreateRun();

        async Task<string> WakesOn(Action change, string worker = "w1")
        {
            var wait = run.WaitForChangeAsync(worker, TimeSpan.FromSeconds(30), CancellationToken.None);
            await Task.Delay(100);
            change();
            return (await wait.WaitAsync(TimeSpan.FromSeconds(10))).Text;
        }

        (await WakesOn(() => run.Bus.SendDirect("lead", "w1", "w2 owns the parser"))).ShouldContain("lead sent you a message");
        run.Bus.TakeDirect("w1");
        (await WakesOn(() => run.Bus.Post("w2", "Merged abc into the team branch."))).ShouldContain("w2 posted to the channel: Merged abc");
        (await WakesOn(() => run.Bus.Broadcast(TeamMessageBus.RunnerName, TeamPrompts.DeadlineReminder(TimeSpan.FromMinutes(2))))).ShouldContain("runner sent you a message");
        run.Bus.TakeDirect("w1");

        var wait = run.WaitForChangeAsync("w1", TimeSpan.FromMilliseconds(400), CancellationToken.None);
        run.Bus.SendDirect("w2", "w3", "private");
        (await wait).Text.ShouldContain("Nothing changed");
    }

    [Fact]
    public async Task Waiting_DoesNotSleepThroughSomethingThatAlreadyHappened()
    {
        var run = CreateRun();
        run.Board.Write("w2", "PATCH_SUMMARY", Merged("G1"), null);

        var seen = await run.WaitForChangeAsync("w1", TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        seen.Text.ShouldContain("Already changed");
        seen.Text.ShouldContain("[PATCH_SUMMARY] w2");

        run.Bus.SendDirect("w2", "w1", "look at this");
        var message = await run.WaitForChangeAsync("w1", TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        message.Text.ShouldContain("message waiting");
        run.Bus.HasDirect("w1").ShouldBeTrue("it is not taken: it still comes with the result");
    }

    [Fact]
    public async Task Waiting_StopsWhenTheRoundIsCancelled_AndNeverOutlastsTheTimeLeft()
    {
        var run = CreateRun();
        using var cancel = new CancellationTokenSource();
        var wait = run.WaitForChangeAsync("w1", TimeSpan.FromSeconds(30), cancel.Token);
        await Task.Delay(100);
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(wait);

        // No time left: it says so at once instead of waiting.
        var over = CreateRun(budget: TimeSpan.Zero);
        var (isError, text) = await over.WaitForChangeAsync("w1", TimeSpan.FromMinutes(5), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        isError.ShouldBeFalse();
        text.ShouldContain("The round is over");
    }

    [Fact]
    public async Task TheDispatcher_RoutesTeamWait_ToTheRun_AndCountsWaitingAsDoingTheJob()
    {
        int? asked = null;
        var (dispatcher, _, state) = await _repo.CreateWorkerAsync(
            1, waitForChange: (minutes, _) =>
            {
                asked = minutes;
                return Task.FromResult((false, "Woke."));
            });
        dispatcher.Tools.ShouldContain(t => t.Name == TeamTools.WaitToolName);

        var result = await dispatcher.ExecuteAsync(TeamTools.WaitToolName, JsonNode.Parse("""{"minutes":3}"""), CancellationToken.None);
        var byDefault = await dispatcher.ExecuteAsync(TeamTools.WaitToolName, null, CancellationToken.None);

        result.ShouldBe((false, "Woke."));
        byDefault.IsError.ShouldBeFalse();
        asked.ShouldBe(TeamTools.DefaultWaitMinutes, "the second call, with no minutes");
        state.HadVisibleActivityThisCycle.ShouldBeTrue("no idle nudge for a worker that is waiting on purpose");
        state.WroteToBoardThisCycle.ShouldBeTrue("and nothing to publish for it");
    }

    [Fact]
    public async Task WithoutAnAnswerForIt_TeamWaitIsNotOfferedOrRun()
    {
        var (dispatcher, _, _) = await _repo.CreateWorkerAsync(1);

        dispatcher.Tools.ShouldNotContain(t => t.Name == TeamTools.WaitToolName);
        (await dispatcher.ExecuteAsync(TeamTools.WaitToolName, null, CancellationToken.None)).IsError.ShouldBeTrue();
    }

    // ---- Wrapping up ----

    [Fact]
    public void WhenTheLastPlannedTaskIsMerged_TheTeamIsToldOnce_ToWrapUp()
    {
        var run = CreateRun(workers: 2);
        run.Board.Write("lead", "TASK", "First", null);
        run.Board.Write("lead", "TASK", "Second", null);

        run.Board.Write("w1", "PATCH_SUMMARY", Merged("G1"), null);
        run.IsWrappingUp.ShouldBeFalse("one task is still open");
        run.Bus.HasDirect("w2").ShouldBeFalse();

        run.Board.Write("w2", "PATCH_SUMMARY", Merged("G2"), null);
        run.IsWrappingUp.ShouldBeTrue();
        run.Bus.TakeDirect("w1").ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            m => m.From.ShouldBe(TeamMessageBus.RunnerName),
            m => m.Text.ShouldBe(TeamPrompts.WrapUpNotice));
        run.Bus.TakeDirect("w2").ShouldHaveSingleItem();

        // Further summaries - a worker's last check - do not say it again.
        run.Board.Write("w1", "PATCH_SUMMARY", "files=x | idea=a last check | evidence=tests pass", null);
        run.Bus.HasDirect("w1").ShouldBeFalse();
        run.Bus.HasDirect("w2").ShouldBeFalse();
    }

    [Fact]
    public void ATaskAddedAfterTheWrapUp_MeansTheTeamIsNotDoneAnymore_AndFinishingItSaysSoAgain()
    {
        var run = CreateRun(workers: 2);
        run.Board.Write("lead", "TASK", "First", null);
        run.Board.Write("w1", "PATCH_SUMMARY", Merged("G1"), null);
        run.IsWrappingUp.ShouldBeTrue();
        run.Bus.TakeDirect("w2");

        run.Board.Write("w2", "TASK", "A follow-up the last check found", null);
        run.IsWrappingUp.ShouldBeFalse();

        run.Board.Write("w2", "PATCH_SUMMARY", Merged("G3"), null);
        run.IsWrappingUp.ShouldBeTrue();
        run.Bus.TakeDirect("w1").Select(m => m.Text).ShouldContain(TeamPrompts.WrapUpNotice);
    }

    [Fact]
    public void ARunWithNoTasks_NeverTellsTheTeamToWrapUp_ForWorkersChooseTheirOwnWork()
    {
        var run = CreateRun(workers: 2);

        run.Board.Write("w1", "PATCH_SUMMARY", "files=x | idea=something | evidence=build ok", null);

        run.IsWrappingUp.ShouldBeFalse();
        run.Bus.HasDirect("w2").ShouldBeFalse();
    }

    // ---- The lead's plan ----

    private (UltraOrchestrator Lead, TeamBoard Board) CreateLead()
    {
        string[] names = ["w1", "w2"];
        var board = new TeamBoard(TimeProvider.System, TimeSpan.FromMinutes(10));
        var bus = new TeamMessageBus(TimeProvider.System, [.. names, LeadTools.LeadName]);
        var lead = new UltraOrchestrator(
            _client, "lead-model", ChatEffort.High, _repo.ReadOnlyTools, _repo.RepositoryRoot,
            board, bus, names.Select(n => new TeamWorkerState(n)).ToList(), TimeProvider.System, 2);
        return (lead, board);
    }

    [Fact]
    public async Task APlanThatSaysWhatWaitsForWhat_PutsItOnTheBoardAsBlockedTasks()
    {
        // The real shape of the run this came from: a foundation, two pieces that need it, and docs for all.
        _client.Script("lead",
            _client.Call(LeadTools.SubmitPlanToolName, new
            {
                tasks = new[] { "Foundation", "Page one", "Page two", "Docs" },
                after = new[] { Array.Empty<int>(), new[] { 1 }, new[] { 1 }, new[] { 1, 2, 3 } },
            }),
            [new TextDelta("Planned.")]);
        var (lead, board) = CreateLead();

        (await lead.PlanAsync("goal", CancellationToken.None)).ShouldBe(4);

        var tasks = board.Snapshot().Where(e => e.Type == TeamBoardEntryType.Task).ToList();
        tasks.Select(t => t.After).ShouldBe([null, [1], [1], [1, 2, 3]]);
        tasks.Select(t => TeamBoard.StatusOf(t, board.Snapshot(), [])).ShouldBe(
            [TeamTaskStatus.Open, TeamTaskStatus.Blocked, TeamTaskStatus.Blocked, TeamTaskStatus.Blocked]);
    }

    [Fact]
    public async Task APlansBadAfterLists_AreLeftOut_NotHeldAgainstThePlan()
    {
        // A task waiting on itself, on a later task, on one that does not exist, and a ragged list: none of
        // it can be honoured, and none of it is worth throwing away a plan the lead has already paid for.
        _client.Script("lead",
            _client.Call(LeadTools.SubmitPlanToolName, new
            {
                tasks = new[] { "One", "Two", "Three" },
                after = new[] { new[] { 1 }, new[] { 3, 9, 0, -1 }, new[] { 1 } },
            }),
            [new TextDelta("Planned.")]);
        var (lead, board) = CreateLead();

        (await lead.PlanAsync("goal", CancellationToken.None)).ShouldBe(3);

        var tasks = board.Snapshot().Where(e => e.Type == TeamBoardEntryType.Task).ToList();
        tasks[0].After.ShouldBeNull("it cannot wait on itself");
        tasks[1].After.ShouldBeNull("a later task, an unknown one and nonsense");
        tasks[2].After.ShouldBe([1], "the one good entry stays");
    }

    [Fact]
    public async Task AFollowUpTheLeadAdds_CanWaitForATaskAlreadyOnTheBoard_AndAnUnknownIdIsSaidSo()
    {
        _client.Script("lead",
            _client.Call(LeadTools.AddTaskToolName, new { text = "Update the docs", after = new[] { "G1" } }),
            _client.Call(LeadTools.AddTaskToolName, new { text = "Another", after = new[] { "G42" } }),
            [new TextDelta("Adjusted.")]);
        var (lead, board) = CreateLead();
        board.Write("w1", "TASK", "Foundation", null);

        await lead.CheckInAsync("goal", TimeSpan.FromMinutes(5), CancellationToken.None);

        var added = board.Snapshot().Where(e => e.Author == "lead").ShouldHaveSingleItem();
        added.Text.ShouldBe("Update the docs");
        added.After.ShouldBe([1]);
        lead.TasksPlanned.ShouldBe(1, "the one with an unknown id was not added");
    }
}
