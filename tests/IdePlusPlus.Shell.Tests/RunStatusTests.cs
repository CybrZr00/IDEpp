using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// What the status card and the result summary say for a run of either kind. Both read the run through
/// <see cref="ITeamRunStatus"/>, so a stand-in is enough to check the words for the end reasons only the
/// pipeline can produce, and that a run with stages shows its stage on the card while a team shows nothing new.
/// </summary>
public sealed class RunStatusTests
{
    private sealed class FakeRun : ITeamRunStatus
    {
        public bool IsPipeline { get; init; }

        public string? RecordsDirectory { get; init; }

        public string RunId => "20260101-000000-abcd";

        public TeamBoard Board { get; } = new(TimeProvider.System, TimeSpan.FromMinutes(10));

        public IReadOnlyList<TeamWorkerState> Workers { get; set; } = [new TeamWorkerState("u1")];

        public int MaxWorkers => Workers.Count;

        public bool IsAutoScaling => false;

        public TimeSpan TimeBudget => TimeSpan.FromMinutes(20);

        public TimeSpan Elapsed => TimeSpan.FromMinutes(7);

        public TimeSpan TimeLeft => TimeSpan.FromMinutes(13);

        public int Round => 1;

        public string? SetupStatus => null;

        public bool IsCheckingBuild => false;

        public bool IsAwaitingContinue => false;

        public string? PauseExplanation => null;

        public bool IsWrappingUp => false;

        public string? StageText { get; set; }

        public long MaxTokens => 0;

        public int PromptTokens => 1_000;

        public int CompletionTokens => 200;

        public int CachedPromptTokens => 0;

        public string? StartedFrom => null;

        public int CarriedBoardEntries => 0;

        public event Action? Changed
        {
            add { }
            remove { }
        }
    }

    private static UltraTeamResult Result(UltraTeamEndReason reason) => new(
        "20260101-000000-abcd", "C:/repo", "snapshot", "ideplusplus/ultra/20260101-000000-abcd/team", TeamBranchKept: false,
        [], reason, [new TeamWorkerState("u1")], [], 1_000, 200, []);

    [Theory]
    [InlineData(UltraTeamEndReason.LoopCapReached, "stopped: the judges still had open findings after the last repair round.")]
    [InlineData(UltraTeamEndReason.NoProgress, "stopped: repairs were no longer reducing the open findings.")]
    public void TheResultSummary_SaysWhyAPipelineRunStopped(UltraTeamEndReason reason, string expected)
    {
        var text = CopilotChatPaneViewModel.DescribeTeamResult(new FakeRun(), Result(reason), 0, []);

        text.ShouldContain(expected);
        text.ShouldNotContain("no worker was still making progress");
    }

    [Theory]
    [InlineData(UltraTeamEndReason.LoopCapReached, "Stopped - the judges still had open findings after the last repair round.")]
    [InlineData(UltraTeamEndReason.NoProgress, "Stopped - repairs were no longer reducing the open findings.")]
    [InlineData(UltraTeamEndReason.AllDone, "Finished - every worker reported done.")]
    public void TheCard_SaysTheSameWhenARunEnds(UltraTeamEndReason reason, string expected)
    {
        var card = new TeamRunViewModel(new FakeRun(), isLocalProvider: false, discardBranches: null);

        card.Finish(Result(reason));

        card.OutcomeText.ShouldBe(expected);
        card.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void ARunWithStages_ShowsItsStageOnTheCard_AndOneWithoutShowsNothingExtra()
    {
        var staged = new TeamRunViewModel(new FakeRun { StageText = "Judge, loop 2 of 3" }, isLocalProvider: false, discardBranches: null);
        var team = new TeamRunViewModel(new FakeRun(), isLocalProvider: false, discardBranches: null);

        staged.ProgressText.ShouldStartWith("Judge, loop 2 of 3  ·  ");
        team.ProgressText.ShouldStartWith("7:00 elapsed");
    }

    private static PipelineReport OpenReport() => new(
        Passed: false,
        EndReason: "LoopCapReached",
        JudgeRounds: 2,
        RepairRounds: 1,
        Threshold: TeamFindingSeverity.Medium,
        Open:
        [
            new ReportFinding(
                "F1.1", "j1", TeamFindingSeverity.High, "correctness", "Value is wrong", ["Lib/Class1.cs"], "Read the file.", "public int Value => 1;",
                "Make Value return 2.", FindingStatus.Escalated, 2, null),
        ],
        Lower: [],
        Fixed: 0,
        Dismissed: 0,
        Unverified: 0,
        NextPrompts: ["In Lib/Class1.cs: Value is wrong. Make Value return 2."],
        Gaps: []);

    // ---- The words for a pipeline run ----

    [Theory]
    [InlineData(UltraTeamEndReason.AllDone, "finished: the gate and every judge passed.")]
    [InlineData(UltraTeamEndReason.WorkersStopped, "stopped: an agent stopped without handing in what the next stage needed.")]
    [InlineData(UltraTeamEndReason.LoopCapReached, "stopped: the judges still had open findings after the last repair round.")]
    public void ThePipelinesResult_SaysPipeline_AndEndsInItsOwnWords(UltraTeamEndReason reason, string expected)
    {
        var text = CopilotChatPaneViewModel.DescribeTeamResult(new FakeRun { IsPipeline = true }, Result(reason), 0, []);

        text.ShouldStartWith($"**Ultra pipeline** - {expected} 1 agent, 7 min");
        text.ShouldContain("\nAgents:\n");
        text.ShouldNotContain("Workers:");
        text.ShouldNotContain("**Ultra team**");
    }

    [Fact]
    public void TheTeamsResult_IsUnchanged_SoARunOfTheTeamReadsAsItAlwaysDid()
    {
        var text = CopilotChatPaneViewModel.DescribeTeamResult(new FakeRun(), Result(UltraTeamEndReason.AllDone), 0, []);

        text.ShouldStartWith("**Ultra team** - finished: every worker reported done. 1 workers, 7 min");
        text.ShouldContain("\nWorkers:\n");
        text.ShouldNotContain("The full report");
    }

    [Fact]
    public void ThePipelinesResult_CarriesTheReport_AndSaysWhereTheFullOneIs()
    {
        var run = new FakeRun { IsPipeline = true, RecordsDirectory = "C:\\runs\\r1\\_phases" };
        var result = Result(UltraTeamEndReason.LoopCapReached) with { Pipeline = OpenReport() };

        var text = CopilotChatPaneViewModel.DescribeTeamResult(run, result, 0, []);

        text.ShouldContain("Stopped at its limit of 1 repair round with 1 finding still open.");
        text.ShouldContain("- [high] Value is wrong (Lib/Class1.cs) - Read the file.");
        text.ShouldContain("1. In Lib/Class1.cs: Value is wrong. Make Value return 2.");
        text.ShouldContain("The full report, with every finding's evidence, is `report.md` in `C:\\runs\\r1\\_phases`.");
    }

    [Fact]
    public void ARunThatLeftNoReport_SaysNothingOfOne()
    {
        var text = CopilotChatPaneViewModel.DescribeTeamResult(new FakeRun { IsPipeline = true }, Result(UltraTeamEndReason.WorkersStopped), 0, []);

        text.ShouldNotContain("report.md");
        text.ShouldNotContain("Suggested next prompts");
    }

    [Fact]
    public void ThePipelinesAgents_AreListedAsHavingHandedInTheirWork_OrAsStopped()
    {
        var done = new TeamWorkerState("b1");
        done.Phase = TeamWorkerPhase.Done;
        var stopped = new TeamWorkerState("b2");
        stopped.Phase = TeamWorkerPhase.Stopped;
        var result = Result(UltraTeamEndReason.WorkersStopped) with { Workers = [done, stopped] };

        var text = CopilotChatPaneViewModel.DescribeTeamResult(new FakeRun { IsPipeline = true, Workers = [done, stopped] }, result, 0, []);

        text.ShouldContain("- b1: handed in its work after 0 steps");
        text.ShouldContain("- b2: stopped after 0 steps");
    }

    [Fact]
    public void ThePipelinesCard_SaysPipeline_AndFinishesInItsOwnWords()
    {
        var pipeline = new TeamRunViewModel(new FakeRun { IsPipeline = true }, isLocalProvider: false, discardBranches: null);
        var team = new TeamRunViewModel(new FakeRun(), isLocalProvider: false, discardBranches: null);

        pipeline.Header.ShouldBe("⚡ Ultra pipeline - 1 agent so far");
        team.Header.ShouldBe("⚡ Ultra team - 1 worker");

        pipeline.Finish(Result(UltraTeamEndReason.AllDone));
        pipeline.OutcomeText.ShouldBe("Finished - the gate and every judge passed.");
        var stopped = new TeamRunViewModel(new FakeRun { IsPipeline = true }, isLocalProvider: false, discardBranches: null);
        stopped.Finish(Result(UltraTeamEndReason.WorkersStopped));
        stopped.OutcomeText.ShouldBe("Stopped - an agent stopped without handing in what the next stage needed.");
    }

    [Fact]
    public void AnAgentWithNothingToSay_IsShownAsDone_NotAsDoneDash()
    {
        var agent = new TeamWorkerState("u1");
        agent.Phase = TeamWorkerPhase.Done;
        var card = new TeamRunViewModel(new FakeRun { IsPipeline = true, Workers = [agent] }, isLocalProvider: false, discardBranches: null);

        card.Workers.Single().Status.ShouldBe("done");
    }

    [Fact]
    public void TheMessageThatLandsAPipelinesWork_SaysSo_AndHowTheRunEnded()
    {
        var run = new FakeRun { IsPipeline = true };
        var result = Result(UltraTeamEndReason.LoopCapReached) with { Pipeline = OpenReport() };

        var message = CopilotChatPaneViewModel.LandingCommitMessage("Make Class1.Value return 2.", run, result);

        message.ShouldStartWith("Merge Ultra pipeline run 20260101-000000-abcd: Make Class1.Value return 2.");
        message.ShouldContain("1 agent, 7 min,");
        message.ShouldContain("Stopped at its limit of 1 repair round with 1 finding still open.");
        CopilotChatPaneViewModel.LandingCommitMessage("Make Class1.Value return 2.", new FakeRun(), Result(UltraTeamEndReason.AllDone))
            .ShouldStartWith("Merge Ultra run 20260101-000000-abcd: Make Class1.Value return 2.");
    }

    [Fact]
    public void ThePipelineRun_IsAPipeline_AndKeepsItsRecordsInItsPhasesFolder_WhereTheTeamKeepsNone()
    {
        ((ITeamRunStatus)NewTeamRun()).IsPipeline.ShouldBeFalse();
        ((ITeamRunStatus)NewTeamRun()).RecordsDirectory.ShouldBeNull();

        var root = Path.Combine(Path.GetTempPath(), $"ideplusplus-status-{Guid.NewGuid():N}");
        var pipeline = new UltraPipelineRun(
            new ScriptedTeamChatClient(), "chat-model",
            new UltraTeamOptions(1, TimeSpan.FromMinutes(1), ShellCommandsEnabled: false, "Debug", IdePlusPlus.Copilot.Core.ChatEffort.Ultra),
            readOnlyTools: null!, gitService: null!, teamGit: new IdePlusPlus.SourceControl.TeamGitWorkspace(), TimeProvider.System, root, runCommand: null);

        ((ITeamRunStatus)pipeline).IsPipeline.ShouldBeTrue();
        ((ITeamRunStatus)pipeline).RecordsDirectory.ShouldBe(Path.Combine(root, pipeline.RunId, "_phases"));
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheTeamRun_HasNoStages()
    {
        // The team is the run the card was written for: its progress line gains nothing.
        ((ITeamRunStatus)NewTeamRun()).StageText.ShouldBeNull();
    }

    private static UltraTeamRun NewTeamRun()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ideplusplus-status-{Guid.NewGuid():N}");
        try
        {
            return new UltraTeamRun(
                new ScriptedTeamChatClient(), "chat-model",
                new UltraTeamOptions(1, TimeSpan.FromMinutes(1), ShellCommandsEnabled: false, "Debug", IdePlusPlus.Copilot.Core.ChatEffort.Ultra),
                readOnlyTools: null!, gitService: null!, teamGit: new IdePlusPlus.SourceControl.TeamGitWorkspace(), TimeProvider.System, root, runCommand: null);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
