using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;
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
