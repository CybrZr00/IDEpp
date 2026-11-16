using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class TeamRoleTests
{
    private static readonly ToolDefinition EditFile =
        new(TeamTools.EditFileToolName, "edit", JsonNode.Parse("""{ "type": "object" }""")!);

    private static IReadOnlyList<string> NamesFor(TeamRole role) =>
        TeamTools.ForWorker(EditFile, shellCommandsEnabled: true, role: role).Select(t => t.Name).ToList();

    [Fact]
    public void TheCatalogueHasTheRosterTheRunnerStarts()
    {
        TeamRoles.All.Select(r => r.NamePrefix).ShouldBe(["b", "mut", "adv", "lint", "gap", "next"]);
        TeamRoles.All.Where(r => r.IsSpecialist).Count().ShouldBe(5);
        TeamRoles.All.ShouldAllBe(r => r.Charter.Length > 120);
        TeamRoles.Conformity.DefaultEffort.ShouldBe(ChatEffort.Low);
        TeamRoles.Adversary.DefaultEffort.ShouldBe(ChatEffort.High);
        TeamRoles.Builder.Trigger.ShouldBe(TeamRoleTrigger.Backlog);
        TeamRoles.Specialists.ShouldAllBe(r => r.Trigger == TeamRoleTrigger.MergeQueue);
    }

    [Fact]
    public void WorkerNamesCarryTheirLane_AndOlderNamesStillParseToNoRole()
    {
        TeamRoles.NameFor(TeamRoleKind.MutationTester, 2).ShouldBe("mut2");

        TeamRoles.ForWorkerName("adv1")!.Kind.ShouldBe(TeamRoleKind.Adversary);
        TeamRoles.ForWorkerName("lint2")!.Kind.ShouldBe(TeamRoleKind.Conformity);
        TeamRoles.ForWorkerName("next1")!.Kind.ShouldBe(TeamRoleKind.FollowUpScout);
        TeamRoles.ForWorkerName("b3")!.Kind.ShouldBe(TeamRoleKind.Builder);

        TeamRoles.ForWorkerName("w3").ShouldBeNull();
        TeamRoles.ForWorkerName("adv").ShouldBeNull();
        TeamRoles.ForWorkerName("").ShouldBeNull();
        TeamRoles.ForWorkerName("z9").ShouldBeNull();
    }

    [Fact]
    public void SpeedIsTheRosterUltraAlwaysRan_BuildersOnly()
    {
        var speed = TeamComposition.Speed(4);

        speed.Total.ShouldBe(4);
        speed.SpecialistTotal.ShouldBe(0);
        speed.Specialists.ShouldBeEmpty();
        speed.Workers.Select(w => w.Name).ShouldBe(["b1", "b2", "b3", "b4"]);
        speed.Validate().ShouldBeNull();
    }

    [Fact]
    public void BalancedAndHardeningSpreadTheSpecialistLanes()
    {
        var balanced = TeamComposition.Balanced(3);
        balanced.CountOf(TeamRoleKind.MutationTester).ShouldBe(1);
        balanced.CountOf(TeamRoleKind.Adversary).ShouldBe(1);
        balanced.CountOf(TeamRoleKind.Conformity).ShouldBe(1);
        balanced.CountOf(TeamRoleKind.GapFinder).ShouldBe(1);
        balanced.CountOf(TeamRoleKind.FollowUpScout).ShouldBe(0);
        balanced.Total.ShouldBe(7);
        balanced.Validate().ShouldBeNull();
        balanced.Workers.Select(w => w.Name)
            .ShouldBe(["b1", "b2", "b3", "mut1", "adv1", "lint1", "gap1"]);

        var hardening = TeamComposition.Hardening(2);
        hardening.CountOf(TeamRoleKind.MutationTester).ShouldBe(2);
        hardening.CountOf(TeamRoleKind.Adversary).ShouldBe(2);
        hardening.Total.ShouldBe(6);
        hardening.Validate().ShouldBeNull();

        TeamComposition.For(TeamCompositionPreset.Balanced, 4).Total.ShouldBe(8);
        TeamComposition.For(TeamCompositionPreset.Speed, 4).Total.ShouldBe(4);
    }

    [Fact]
    public void ValidateRefusesATeamThatCannotDoTheWork()
    {
        TeamComposition.Speed(0).Validate().ShouldNotBeNull();
        TeamComposition.Balanced(1).Total.ShouldBe(5);
        new TeamComposition(2, new Dictionary<TeamRoleKind, int> { [TeamRoleKind.Adversary] = 3 })
            .Validate()!.ShouldContain("At most 2");
        new TeamComposition(TeamComposition.MaxUltraWorkerCount, new Dictionary<TeamRoleKind, int> { [TeamRoleKind.MutationTester] = 1 })
            .Validate()!.ShouldContain($"{TeamComposition.MaxUltraWorkerCount}");
        new TeamComposition(2, new Dictionary<TeamRoleKind, int> { [TeamRoleKind.Builder] = 1 })
            .Validate().ShouldNotBeNull();
    }

    [Fact]
    public void ABuilderIsOfferedEverything_AndNoSpecialistTools()
    {
        var names = NamesFor(TeamRoles.Builder);

        names.ShouldContain(TeamTools.EditFileToolName);
        names.ShouldContain(TeamTools.MergeToolName);
        names.ShouldNotContain(TeamTools.NextReviewToolName);
        names.ShouldNotContain(TeamTools.ReportFindingToolName);
        names.ShouldNotContain(TeamTools.RunMutationTestsToolName);
        names.ShouldNotContain(TeamTools.CheckConformityToolName);
    }

    [Fact]
    public void AnAdversaryWritesTestsButNeverMergesThem()
    {
        var names = NamesFor(TeamRoles.Adversary);

        names.ShouldContain(TeamTools.EditFileToolName);
        names.ShouldContain(TeamTools.CommitToolName);
        names.ShouldContain(TeamTools.NextReviewToolName);
        names.ShouldContain(TeamTools.ReportFindingToolName);
        names.ShouldNotContain(TeamTools.MergeToolName);
        names.ShouldNotContain(TeamTools.RunMutationTestsToolName);
    }

    [Fact]
    public void EachSpecialistLaneGetsOnlyItsOwnTool()
    {
        var mutation = NamesFor(TeamRoles.MutationTester);
        mutation.ShouldContain(TeamTools.RunMutationTestsToolName);
        mutation.ShouldContain(TeamTools.MergeToolName);
        mutation.ShouldNotContain(TeamTools.CheckConformityToolName);

        var conformity = NamesFor(TeamRoles.Conformity);
        conformity.ShouldContain(TeamTools.CheckConformityToolName);
        conformity.ShouldContain(TeamTools.MergeToolName);
        conformity.ShouldNotContain(TeamTools.RunMutationTestsToolName);
    }

    [Fact]
    public void AReadOnlyLaneGetsNoEditFileAndNoMerge()
    {
        foreach (var role in new[] { TeamRoles.GapFinder, TeamRoles.FollowUpScout })
        {
            var names = NamesFor(role);
            names.ShouldNotContain(TeamTools.EditFileToolName);
            names.ShouldNotContain(TeamTools.MergeToolName);
            names.ShouldContain(TeamTools.CommitToolName);
            names.ShouldContain(TeamTools.NextReviewToolName);
            names.ShouldContain(TeamTools.ReportFindingToolName);
        }
    }

    [Fact]
    public void EveryRoleHasADistinctToolList_AndCoordinationCoversTheNewTools()
    {
        foreach (var role in TeamRoles.All)
        {
            var names = NamesFor(role);
            names.Distinct().Count().ShouldBe(names.Count);
        }

        TeamTools.IsCoordinationTool(TeamTools.NextReviewToolName).ShouldBeTrue();
        TeamTools.IsCoordinationTool(TeamTools.ReportFindingToolName).ShouldBeTrue();
        TeamTools.IsCoordinationTool(TeamTools.RunMutationTestsToolName).ShouldBeTrue();
        TeamTools.IsCoordinationTool(TeamTools.CheckConformityToolName).ShouldBeTrue();
    }

    [Fact]
    public void TheFindingToolAsksForTheFieldsAFindingBecomesWorkBy()
    {
        var properties = TeamTools.ReportFinding.ParametersSchema["properties"]!;

        properties["severity"].ShouldNotBeNull();
        properties["kind"].ShouldNotBeNull();
        properties["evidence"].ShouldNotBeNull();
        properties["fix_by"].ShouldNotBeNull();
        TeamTools.RunMutationTests.ParametersSchema["properties"]!["max_mutants"]!["maximum"]!.GetValue<int>()
            .ShouldBe(TeamTools.MaxMutantsPerRun);
    }
}
