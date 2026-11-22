using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class PipelineRolesTests
{
    private static readonly ToolDefinition EditFile =
        new(TeamTools.EditFileToolName, "edit", JsonNode.Parse("""{ "type": "object" }""")!);

    private static IReadOnlyList<string> ToolNamesFor(TeamRole role) =>
        TeamTools.ForWorker(EditFile, shellCommandsEnabled: false, role: role).Select(t => t.Name).ToList();

    // --- The roles themselves ---

    [Fact]
    public void EveryPipelineRoleIsOfThePipelineKind_WithItsOwnPipelineKind()
    {
        PipelineRoles.Understander.PipelineKind.ShouldBe(PipelineRoleKind.Understander);
        PipelineRoles.Designer.PipelineKind.ShouldBe(PipelineRoleKind.Designer);
        PipelineRoles.Owner.PipelineKind.ShouldBe(PipelineRoleKind.Owner);
        PipelineRoles.StandardJudges.ShouldAllBe(j => j.Role.PipelineKind == PipelineRoleKind.Judge);

        new[] { PipelineRoles.Understander, PipelineRoles.Designer, PipelineRoles.Owner }
            .Concat(PipelineRoles.StandardJudges.Select(j => j.Role))
            .ShouldAllBe(role => role.Kind == TeamRoleKind.Pipeline && role.Trigger == TeamRoleTrigger.Stage);
    }

    [Fact]
    public void ThePipelineRolesHaveTheNamesAndScopesThePlanGives()
    {
        PipelineRoles.UnderstanderAgent(3).Name.ShouldBe("u3");
        PipelineRoles.DesignerAgent.Name.ShouldBe("d1");
        PipelineRoles.OwnerAgent(2).Name.ShouldBe("b2");
        PipelineRoles.StandardJudges.Select(j => j.Name).ShouldBe(["j1", "j2", "j3", "j4"]);

        PipelineRoles.Understander.EditScope.ShouldBe(TeamRoleEditScope.None);
        PipelineRoles.Designer.EditScope.ShouldBe(TeamRoleEditScope.Any);
        PipelineRoles.Owner.EditScope.ShouldBe(TeamRoleEditScope.OwnedFilesOnly);
        PipelineRoles.StandardJudges.Select(j => j.Role.EditScope).ShouldBe(
            [TeamRoleEditScope.None, TeamRoleEditScope.None, TeamRoleEditScope.None, TeamRoleEditScope.TestsOnly]);
    }

    [Fact]
    public void OnlyTheDesignerAndTheOwnersMergeAnything()
    {
        PipelineRoles.Understander.CanMerge.ShouldBeFalse();
        PipelineRoles.Designer.CanMerge.ShouldBeTrue();
        PipelineRoles.Owner.CanMerge.ShouldBeTrue();

        // j4 can edit (tests), but its failing test is evidence - it is never merged.
        PipelineRoles.StandardJudges[3].Role.CanEdit.ShouldBeTrue();
        PipelineRoles.StandardJudges.ShouldAllBe(j => !j.Role.CanMerge);
    }

    [Fact]
    public void PipelineRolesAreNotReviewLaneSpecialists()
    {
        PipelineRoles.Understander.IsSpecialist.ShouldBeFalse();
        PipelineRoles.Designer.IsSpecialist.ShouldBeFalse();
        PipelineRoles.Owner.IsSpecialist.ShouldBeFalse();
        PipelineRoles.StandardJudges.ShouldAllBe(j => !j.Role.IsSpecialist);
    }

    [Fact]
    public void TheStandardRolesHaveRealChartersAndTheEffortTheyNeed()
    {
        PipelineRoles.Understander.Charter.Length.ShouldBeGreaterThan(200);
        PipelineRoles.Designer.Charter.Length.ShouldBeGreaterThan(200);
        PipelineRoles.Owner.Charter.Length.ShouldBeGreaterThan(200);
        PipelineRoles.StandardJudges.ShouldAllBe(j => j.Role.Charter.Length > 200);
        PipelineRoles.StandardJudges.Select(j => j.Role.Charter).Distinct().Count().ShouldBe(4);

        PipelineRoles.Designer.DefaultEffort.ShouldBe(ChatEffort.High);
        PipelineRoles.StandardJudges.ShouldAllBe(j => j.Role.DefaultEffort == ChatEffort.High);
        PipelineRoles.Understander.DefaultEffort.ShouldBe(ChatEffort.Medium);
        PipelineRoles.Owner.DefaultEffort.ShouldBe(ChatEffort.Medium);
    }

    [Fact]
    public void TheOwnerCharterSaysWhatAnOwnerMayAndMayNotDo()
    {
        // The wording is the prompt; the enforcement is the scope. Both have to name the same tools.
        PipelineRoles.Owner.Charter.ShouldContain("request_change");
        PipelineRoles.Owner.Charter.ShouldContain("await_change");
    }

    [Fact]
    public void AgentNumbersStartAtOne()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PipelineRoles.UnderstanderAgent(0));
        Should.Throw<ArgumentOutOfRangeException>(() => PipelineRoles.OwnerAgent(-1));
    }

    // --- Custom judges ---

    [Theory]
    [InlineData(TeamRoleEditScope.None)]
    [InlineData(TeamRoleEditScope.TestsOnly)]
    public void ACustomJudgeMayReadOrWriteTests(TeamRoleEditScope scope)
    {
        var judge = PipelineRoles.CustomJudge(5, "Accessibility judge", "Check keyboard navigation and contrast.", scope);

        judge.Name.ShouldBe("j5");
        judge.Role.DisplayName.ShouldBe("Accessibility judge");
        judge.Role.EditScope.ShouldBe(scope);
        judge.Role.CanMerge.ShouldBeFalse();
        judge.Role.PipelineKind.ShouldBe(PipelineRoleKind.Judge);
        judge.Role.DefaultEffort.ShouldBe(ChatEffort.High);
    }

    [Theory]
    [InlineData(TeamRoleEditScope.Any)]
    [InlineData(TeamRoleEditScope.ChangedFilesStyleOnly)]
    [InlineData(TeamRoleEditScope.OwnedFilesOnly)]
    public void ACustomJudgeNeverGetsAnEditScopeThatReachesProductionCode(TeamRoleEditScope scope) =>
        Should.Throw<ArgumentOutOfRangeException>(
            () => PipelineRoles.CustomJudge(5, "Judge", "Check things.", scope));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-3)]
    public void ACustomJudgeCannotTakeAStandardJudgesNumber(int number) =>
        Should.Throw<ArgumentOutOfRangeException>(
            () => PipelineRoles.CustomJudge(number, "Judge", "Check things.", TeamRoleEditScope.None));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ACustomJudgeNeedsATitle(string title) =>
        Should.Throw<ArgumentException>(() => PipelineRoles.CustomJudge(5, title, "Check things.", TeamRoleEditScope.None));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ACustomJudgeNeedsACharter(string charter) =>
        Should.Throw<ArgumentException>(() => PipelineRoles.CustomJudge(5, "Judge", charter, TeamRoleEditScope.None));

    [Fact]
    public void ACustomJudgeTitleAndCharterAreCapped_AtTheLimitAndNotBeyondIt()
    {
        PipelineRoles.CustomJudge(
            5, new string('t', PipelineRoles.MaxJudgeTitleLength), new string('c', PipelineRoles.MaxCustomCharterLength), TeamRoleEditScope.None)
            .Name.ShouldBe("j5");

        Should.Throw<ArgumentException>(() => PipelineRoles.CustomJudge(
            5, new string('t', PipelineRoles.MaxJudgeTitleLength + 1), "Check things.", TeamRoleEditScope.None));
        Should.Throw<ArgumentException>(() => PipelineRoles.CustomJudge(
            5, "Judge", new string('c', PipelineRoles.MaxCustomCharterLength + 1), TeamRoleEditScope.None));
    }

    [Fact]
    public void ACustomJudgesTitleAndCharterAreTrimmed()
    {
        var judge = PipelineRoles.CustomJudge(6, "  Localisation judge  ", "  Check every string is translatable.  ", TeamRoleEditScope.None);

        judge.Role.DisplayName.ShouldBe("Localisation judge");
        judge.Role.Charter.ShouldBe("Check every string is translatable.");
    }

    // --- Tool profiles ---

    [Fact]
    public void ADesignerAndAnOwnerGetTheEditAndMergeToolsButNoBoardWriteOrDone()
    {
        foreach (var role in new[] { PipelineRoles.Designer, PipelineRoles.Owner })
        {
            var names = ToolNamesFor(role);

            names.ShouldContain(TeamTools.EditFileToolName);
            names.ShouldContain(TeamTools.CommitToolName);
            names.ShouldContain(TeamTools.SyncToolName);
            names.ShouldContain(TeamTools.MergeToolName);
            names.ShouldContain(TeamTools.BoardReadToolName);
            names.ShouldContain(TeamTools.DirectMessageToolName);
            names.ShouldNotContain(TeamTools.BoardWriteToolName);
            names.ShouldNotContain(TeamTools.DoneToolName);
        }
    }

    [Fact]
    public void NoPipelineRoleIsOfferedTheReviewQueueOrTheSpecialistTools()
    {
        var roles = new[] { PipelineRoles.Understander, PipelineRoles.Designer, PipelineRoles.Owner }
            .Concat(PipelineRoles.StandardJudges.Select(j => j.Role));

        foreach (var role in roles)
        {
            var names = ToolNamesFor(role);
            names.ShouldNotContain(TeamTools.NextReviewToolName);
            names.ShouldNotContain(TeamTools.ReportFindingToolName);
            names.ShouldNotContain(TeamTools.RunMutationTestsToolName);
            names.ShouldNotContain(TeamTools.CheckConformityToolName);
            names.ShouldNotContain(TeamTools.BoardWriteToolName);
            names.ShouldNotContain(TeamTools.DoneToolName);
        }
    }

    [Fact]
    public void AJudgeThatWritesTestsCanCommitButNotMerge_AndAReadOnlyJudgeCanDoNeither()
    {
        var runtimeJudge = ToolNamesFor(PipelineRoles.StandardJudges[3].Role);
        runtimeJudge.ShouldContain(TeamTools.EditFileToolName);
        runtimeJudge.ShouldContain(TeamTools.CommitToolName);
        runtimeJudge.ShouldNotContain(TeamTools.MergeToolName);

        var readOnlyJudge = ToolNamesFor(PipelineRoles.StandardJudges[0].Role);
        readOnlyJudge.ShouldNotContain(TeamTools.EditFileToolName);
        readOnlyJudge.ShouldNotContain(TeamTools.MergeToolName);
    }

    [Fact]
    public void AnUnderstanderIsOfferedNoEditOrMerge()
    {
        var names = ToolNamesFor(PipelineRoles.Understander);

        names.ShouldNotContain(TeamTools.EditFileToolName);
        names.ShouldNotContain(TeamTools.MergeToolName);
        names.ShouldContain(WorkspaceTools.ReadFile.Name);
    }

    // --- The original team is untouched ---

    [Fact]
    public void TheOriginalCatalogueDoesNotContainAnyPipelineRole()
    {
        TeamRoles.All.Select(r => r.NamePrefix).ShouldBe(["b", "mut", "adv", "lint", "gap", "next"]);
        TeamRoles.All.ShouldAllBe(r => r.Kind != TeamRoleKind.Pipeline && r.PipelineKind == null);
        TeamRoles.Specialists.Count.ShouldBe(5);
        TeamRoles.ForWorkerName("u1").ShouldBeNull();
        TeamRoles.ForWorkerName("d1").ShouldBeNull();
        TeamRoles.ForWorkerName("j2").ShouldBeNull();
    }

    [Fact]
    public void TheAdversaryStillNeverMerges_ThroughTheNewAllowsMergeFlag()
    {
        TeamRoles.Adversary.AllowsMerge.ShouldBeFalse();
        TeamRoles.Adversary.CanEdit.ShouldBeTrue();
        TeamRoles.Adversary.CanMerge.ShouldBeFalse();

        TeamRoles.All.Where(r => r.Kind != TeamRoleKind.Adversary).ShouldAllBe(r => r.AllowsMerge);
        TeamRoles.Builder.CanMerge.ShouldBeTrue();
        TeamRoles.MutationTester.CanMerge.ShouldBeTrue();
        TeamRoles.Conformity.CanMerge.ShouldBeTrue();
        TeamRoles.GapFinder.CanMerge.ShouldBeFalse(); // No edits, so nothing to merge.
        TeamRoles.FollowUpScout.CanMerge.ShouldBeFalse();
    }

    [Fact]
    public void TheOriginalToolProfilesAreUnchanged()
    {
        // The builder's coordination set (and so every default caller's) still has board_write and
        // team_done, and the specialists still get their review tools.
        var builder = ToolNamesFor(TeamRoles.Builder);
        builder.ShouldContain(TeamTools.BoardWriteToolName);
        builder.ShouldContain(TeamTools.DoneToolName);
        builder.ShouldContain(TeamTools.MergeToolName);
        builder.ShouldNotContain(TeamTools.NextReviewToolName);

        var adversary = ToolNamesFor(TeamRoles.Adversary);
        adversary.ShouldContain(TeamTools.NextReviewToolName);
        adversary.ShouldContain(TeamTools.ReportFindingToolName);
        adversary.ShouldNotContain(TeamTools.MergeToolName);
    }
}
