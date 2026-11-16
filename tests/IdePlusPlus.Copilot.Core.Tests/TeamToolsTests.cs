using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class TeamToolsTests
{
    private static readonly ToolDefinition EditFile =
        new(TeamTools.EditFileToolName, "edit", JsonNode.Parse("""{ "type": "object" }""")!);

    [Fact]
    public void ForWorker_OffersRequestHelpOnlyInAutoMode()
    {
        TeamTools.ForWorker(EditFile, shellCommandsEnabled: false)
            .ShouldNotContain(t => t.Name == TeamTools.RequestHelpToolName);
        TeamTools.ForWorker(EditFile, shellCommandsEnabled: false, canRequestHelp: true)
            .ShouldContain(t => t.Name == TeamTools.RequestHelpToolName);
    }

    [Fact]
    public void ForWorker_OffersWaitOnlyWhenTheRunCanAnswerIt()
    {
        TeamTools.ForWorker(EditFile, shellCommandsEnabled: false)
            .ShouldNotContain(t => t.Name == TeamTools.WaitToolName);
        TeamTools.ForWorker(EditFile, shellCommandsEnabled: false, canWait: true)
            .ShouldContain(t => t.Name == TeamTools.WaitToolName);
        TeamTools.IsCoordinationTool(TeamTools.WaitToolName).ShouldBeTrue();
    }

    [Fact]
    public void WorkerSystemPrompt_ExplainsWaiting_OnlyWhereTheToolIsOffered_AndAlwaysTheWrapUp()
    {
        var withWait = TeamPrompts.WorkerSystemPrompt("w1", ["w1", "w2"], TimeSpan.FromMinutes(10), false, canWait: true);
        var without = TeamPrompts.WorkerSystemPrompt("w1", ["w1", "w2"], TimeSpan.FromMinutes(10), false);

        withWait.ShouldContain("Call team_wait");
        withWait.ShouldContain($"at most {TeamTools.MaxWaitMinutes}");
        without.ShouldNotContain("team_wait");
        foreach (var prompt in new[] { withWait, without })
        {
            prompt.ShouldContain("every planned TASK is merged");
            prompt.ShouldContain("[blocked]");
        }
    }

    [Fact]
    public void ThePlanAndTaskTools_OfferAfter_AndTheLeadIsToldToUseItSparingly()
    {
        LeadTools.SubmitPlan.ParametersSchema["properties"]!["after"].ShouldNotBeNull();
        LeadTools.AddTask.ParametersSchema["properties"]!["after"].ShouldNotBeNull();
        TeamTools.BoardWrite.ParametersSchema["properties"]!["after"].ShouldNotBeNull();
        TeamPrompts.LeadPlanningPrompt(["w1"]).ShouldContain("'after'");
    }

    [Fact]
    public void WorkerSystemPrompt_ExplainsAskingForHelp_OnlyWhileTheTeamCanStillGrow()
    {
        TeamPrompts.WorkerSystemPrompt("w1", ["w1", "w2"], TimeSpan.FromMinutes(10), false, maxTeamSize: 8)
            .ShouldContain("The team can grow, up to 8 workers");
        TeamPrompts.WorkerSystemPrompt("w1", ["w1", "w2"], TimeSpan.FromMinutes(10), false)
            .ShouldNotContain("team_request_help");
        TeamPrompts.WorkerSystemPrompt("w1", ["w1", "w2"], TimeSpan.FromMinutes(10), false, maxTeamSize: 2)
            .ShouldNotContain("team_request_help");
    }

    [Fact]
    public void ForWorker_OffersRunCommandOnlyWhenShellCommandsAreOn()
    {
        TeamTools.ForWorker(EditFile, shellCommandsEnabled: false)
            .ShouldNotContain(t => t.Name == ShellTools.RunCommandToolName);
        TeamTools.ForWorker(EditFile, shellCommandsEnabled: true)
            .ShouldContain(t => t.Name == ShellTools.RunCommandToolName);
    }

    /// <summary>A worker's edits go to its worktree, never the user's editor, and it cannot fan out
    /// sub-agents of its own or reach MCP/plugin tools - see TeamTools' type doc.</summary>
    [Fact]
    public void ForWorker_LeavesOutTheUserFacingAndUnprovenTools()
    {
        var names = TeamTools.ForWorker(EditFile, shellCommandsEnabled: true).Select(t => t.Name).ToList();

        names.ShouldNotContain("propose_edit");
        names.ShouldNotContain(ProposePlanTool.ToolName);
        names.ShouldNotContain(SubAgentTools.InvestigateParallelToolName);
        names.ShouldNotContain(PublishTools.PublishToolName);
        names.ShouldNotContain(n => n.Contains("__"));
    }

    [Fact]
    public void ForWorker_HasNoDuplicateNames()
    {
        var names = TeamTools.ForWorker(EditFile, shellCommandsEnabled: true).Select(t => t.Name).ToList();

        names.Distinct().Count().ShouldBe(names.Count);
    }

    [Fact]
    public void WorkerSystemPrompt_NamesTheWorkerAndItsPeers()
    {
        var prompt = TeamPrompts.WorkerSystemPrompt(
            "w2", ["w1", "w2", "w3"], TimeSpan.FromMinutes(10), shellCommandsEnabled: false);

        prompt.ShouldStartWith("You are w2, one of 3 equal workers");
        prompt.ShouldContain("your peers: w1, w3");
        prompt.ShouldNotContain("run_command");
    }

    /// <summary>Speed is the default preset, and a roster with no specialist lanes has nobody to do
    /// the review work the lane text promises - so that team gets the prompt Ultra shipped with.</summary>
    [Fact]
    public void WorkerSystemPrompt_LeavesOutTheLanes_WhenTheTeamHasNoSpecialists()
    {
        var legacy = TeamPrompts.WorkerSystemPrompt("w1", ["w1", "w2"], TimeSpan.FromMinutes(10), false);
        var speed = TeamPrompts.WorkerSystemPrompt(
            "b1", ["b1", "b2"], TimeSpan.FromMinutes(10), false, role: TeamRoles.Builder);

        foreach (var prompt in new[] { legacy, speed })
        {
            prompt.ShouldNotContain("specialist");
            prompt.ShouldNotContain("Your lane");
            prompt.ShouldNotContain("FINDING");
            prompt.ShouldNotContain("team_next_review");
            prompt.ShouldContain("every planned TASK is merged");
        }

        legacy.ShouldStartWith("You are w1, one of 2 equal workers");
        speed.ShouldStartWith("You are b1, one of 2 equal workers");
    }

    [Fact]
    public void WorkerSystemPrompt_TellsABuilderThatSpecialistsReviewItsMerges()
    {
        var prompt = TeamPrompts.WorkerSystemPrompt(
            "b1", ["b1", "mut1", "adv1"], TimeSpan.FromMinutes(10), false, role: TeamRoles.Builder);

        prompt.ShouldStartWith("You are b1, a builder");
        prompt.ShouldContain("specialist lanes");
        prompt.ShouldContain("specialist lanes are waiting on it");
        prompt.ShouldContain("Do not do the specialists' jobs yourself");
        prompt.ShouldNotContain("team_next_review");
    }

    [Fact]
    public void WorkerSystemPrompt_GivesEachSpecialistItsOwnLane()
    {
        var adversary = TeamPrompts.WorkerSystemPrompt(
            "adv1", ["b1", "adv1"], TimeSpan.FromMinutes(10), false, role: TeamRoles.Adversary);
        var conformity = TeamPrompts.WorkerSystemPrompt(
            "lint1", ["b1", "lint1"], TimeSpan.FromMinutes(10), false, role: TeamRoles.Conformity);

        adversary.ShouldStartWith("You are adv1, the adversary");
        adversary.ShouldContain("team_next_review");
        adversary.ShouldContain("tests only");
        adversary.ShouldContain("evidence=branch:<sha>");
        adversary.ShouldNotContain("[blocked]");

        conformity.ShouldStartWith("You are lint1, the conformity");
        conformity.ShouldContain("check_conformity");
        conformity.ShouldContain("files the review item you are on changed");
    }

    [Fact]
    public void SplitNudge_AsksTheOldestClaimToBeSplitIntoTask()
    {
        var nudge = TeamPrompts.SplitNudge("parser rewrite", 2);

        nudge.ShouldContain("parser rewrite");
        nudge.ShouldContain("2 builders are idle");
        nudge.ShouldContain("TASKs");
    }
}
