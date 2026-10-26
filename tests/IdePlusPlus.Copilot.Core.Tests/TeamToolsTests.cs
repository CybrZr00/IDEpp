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
}
