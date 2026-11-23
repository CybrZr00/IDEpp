using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>
/// Triage's rules: what the runner places by itself, what it defers to the user, and what it leaves to the orchestrator.
/// b1 owns Lib/A.cs and Lib/B.cs, b2 owns Lib/C.cs; nobody owns Lib/New.cs.
/// </summary>
public sealed class RepairPlannerTests
{
    private static readonly OwnershipMap Owners = OwnershipMap.FromEntries(
    [
        KeyValuePair.Create("Lib/A.cs", "b1"),
        KeyValuePair.Create("Lib/B.cs", "b1"),
        KeyValuePair.Create("Lib/C.cs", "b2"),
    ]);

    private static LedgerFinding Finding(
        string id,
        string[] files,
        FindingStatus status = FindingStatus.Open,
        FindingFixBy fixBy = FindingFixBy.Owner,
        string? fix = "Guard the null case in Parse.",
        string title = "Parse does not guard null",
        int seen = 1) =>
        new(id, "fp", "j1", new JudgeFinding(title, TeamFindingSeverity.Medium, "correctness", files, "evidence", "quote", fix, fixBy),
            FirstSeenLoop: 1, LastSeenLoop: seen, SeenCount: seen, status);

    // ---- What the rules place ----

    [Fact]
    public void AnOpenFindingAboutOneOwnersFile_IsAssignedToThatOwner_WithTheJudgesOwnFixAsTheInstruction()
    {
        var placed = RepairPlanner.Place([Finding("F1.1", ["Lib/A.cs"])], Owners);

        placed.Hard.ShouldBeEmpty();
        var decision = placed.Decided.ShouldHaveSingleItem();
        (decision.FindingId, decision.Action, decision.Owner, decision.Instruction, decision.Reason).ShouldBe(
            ("F1.1", TriageAction.Assign, "b1", "Guard the null case in Parse.", RepairPlanner.AssignReason));
    }

    [Fact]
    public void AFindingNamingSeveralFilesOfOneOwner_AndAFileNobodyOwns_IsStillThatOwnersToRepair()
    {
        var placed = RepairPlanner.Place([Finding("F1.1", ["Lib/A.cs", "Lib/B.cs", "Lib/New.cs"])], Owners);

        placed.Decided.ShouldHaveSingleItem().Owner.ShouldBe("b1");
        placed.Hard.ShouldBeEmpty();
    }

    [Fact]
    public void AFindingWithNoUsableFix_IsAssignedWithItsTitle()
    {
        var placed = RepairPlanner.Place(
            [Finding("F1.1", ["Lib/A.cs"], fix: null), Finding("F1.2", ["Lib/B.cs"], fix: "Fix it.")], Owners);

        placed.Decided.Select(d => d.Instruction).ShouldBe(["Fix this: Parse does not guard null", "Fix this: Parse does not guard null"]);
    }

    [Fact]
    public void AnInstruction_IsCutToWhatTheTriageValidatorAllows()
    {
        var long600 = new string('x', TriageValidator.MaxInstructionLength + 300);

        var placed = RepairPlanner.Place([Finding("F1.1", ["Lib/A.cs"], fix: long600)], Owners);

        placed.Decided.Single().Instruction!.Length.ShouldBeLessThanOrEqualTo(TriageValidator.MaxInstructionLength);
    }

    // ---- What it defers to the user ----

    [Fact]
    public void AFindingTheJudgeSaysIsForTheUser_IsDeferred_WithARequestToRunLater()
    {
        var placed = RepairPlanner.Place(
            [Finding("F1.1", ["Lib/A.cs", "Lib/C.cs"], fixBy: FindingFixBy.User, fix: "Cache the parsed result.", title: "Parsing is slow")], Owners);

        placed.Hard.ShouldBeEmpty();
        var decision = placed.Decided.ShouldHaveSingleItem();
        (decision.Action, decision.Reason, decision.Owner).ShouldBe((TriageAction.Defer, RepairPlanner.DeferReason, null));
        decision.NextPrompt.ShouldBe("In Lib/A.cs, Lib/C.cs: Parsing is slow. Cache the parsed result.");
    }

    [Fact]
    public void ADeferredFindingWithNoFix_AsksForTheTitle_AndNoFilesMeansNoPrefix()
    {
        var placed = RepairPlanner.Place([Finding("F1.1", [], fixBy: FindingFixBy.User, fix: null, title: "Parsing is slow")], Owners);

        placed.Decided.Single().NextPrompt.ShouldBe("Parsing is slow");
    }

    // ---- What it leaves to the orchestrator ----

    [Fact]
    public void AFindingThatSpansTwoOwners_OneNobodyOwns_OneAboutTheDesign_AndOneThatCameBack_AreLeftToTheLead()
    {
        LedgerFinding[] blocking =
        [
            Finding("F1.1", ["Lib/A.cs", "Lib/C.cs"]),
            Finding("F1.2", ["Lib/New.cs"]),
            Finding("F1.3", ["Lib/A.cs"], fixBy: FindingFixBy.Design),
            Finding("F1.4", ["Lib/A.cs"], status: FindingStatus.Escalated, seen: 2),
            Finding("F1.5", ["Lib/A.cs"], status: FindingStatus.Disputed),
            Finding("F1.6", []),
        ];

        var placed = RepairPlanner.Place(blocking, Owners);

        placed.Decided.ShouldBeEmpty();
        placed.Hard.Select(f => f.Id).ShouldBe(["F1.1", "F1.2", "F1.3", "F1.4", "F1.5", "F1.6"]);
    }

    [Fact]
    public void ARepairedFindingThatCameBack_IsNeverHandedBackWithoutTheLead_EvenForOneOwnersFile()
    {
        var placed = RepairPlanner.Place([Finding("F1.1", ["Lib/A.cs"], status: FindingStatus.Escalated, seen: 2)], Owners);

        placed.Decided.ShouldBeEmpty();
        placed.Hard.ShouldHaveSingleItem().Id.ShouldBe("F1.1");
    }

    [Fact]
    public void AMixOfAll_IsSplitInOrder()
    {
        LedgerFinding[] blocking =
        [
            Finding("F1.1", ["Lib/A.cs"]),
            Finding("F1.2", ["Lib/A.cs", "Lib/C.cs"]),
            Finding("F1.3", ["Lib/C.cs"], fixBy: FindingFixBy.User),
        ];

        var placed = RepairPlanner.Place(blocking, Owners);

        placed.Decided.Select(d => (d.FindingId, d.Action)).ShouldBe([("F1.1", TriageAction.Assign), ("F1.3", TriageAction.Defer)]);
        placed.Hard.Select(f => f.Id).ShouldBe(["F1.2"]);
    }

    // ---- Putting the two halves together ----

    [Fact]
    public void TheRulesAndTheLeadsDecisions_AreCombinedInTheOrderTheFindingsWereGiven()
    {
        LedgerFinding[] order = [Finding("F1.1", ["Lib/A.cs"]), Finding("F1.2", ["Lib/A.cs", "Lib/C.cs"]), Finding("F1.3", ["Lib/C.cs"])];
        var byRule = new[]
        {
            new TriageDecision("F1.3", TriageAction.Assign, "r", "b2", "Do it."),
            new TriageDecision("F1.1", TriageAction.Assign, "r", "b1", "Do it."),
        };
        var byLead = new[]
        {
            new TriageDecision("F1.2", TriageAction.Assign, "r", "b2", "Do that."),
            new TriageDecision("F1.2", TriageAction.Assign, "r", "b1", "Do that too."),
        };

        var combined = RepairPlanner.Combine(order, byRule, byLead);

        combined.Select(d => (d.FindingId, d.Owner)).ShouldBe([("F1.1", "b1"), ("F1.2", "b2"), ("F1.2", "b1"), ("F1.3", "b2")]);
    }

    [Fact]
    public void EachOwnersTasks_AreTheFindingsAssignedToIt_WithTheInstructionItWasGiven()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1",
        [
            new ValidatedFinding(Finding("x", ["Lib/A.cs"], title: "First problem").Finding, FindingStatus.Open),
            new ValidatedFinding(Finding("x", ["Lib/C.cs"], title: "Second problem").Finding with { Category = "security" }, FindingStatus.Open),
        ]);
        var plan = new TriagePlan(
        [
            new TriageDecision("F1.1", TriageAction.Assign, "r", "b1", "Change A as the lead says."),
            new TriageDecision("F1.2", TriageAction.Assign, "r", "b2", "Change C."),
            new TriageDecision("F1.2", TriageAction.Assign, "r", "b1", "Also change A for C's sake."),
            new TriageDecision("F9.9", TriageAction.Assign, "r", "b1", "A finding the ledger has never heard of."),
            new TriageDecision("F1.1", TriageAction.Dismiss, "r"),
        ]);

        var tasks = RepairPlanner.TasksByOwner(plan, ledger);

        tasks.Keys.Order().ShouldBe(["b1", "b2"]);
        tasks["b1"].Select(t => (t.Finding.Id, t.Instruction)).ShouldBe(
            [("F1.1", "Change A as the lead says."), ("F1.2", "Also change A for C's sake.")]);
        tasks["b2"].Select(t => (t.Finding.Id, t.Instruction)).ShouldBe([("F1.2", "Change C.")]);
    }

    [Fact]
    public void ATaskWithNoInstruction_FallsBackToTheJudgesFix()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [new ValidatedFinding(Finding("x", ["Lib/A.cs"]).Finding, FindingStatus.Open)]);
        var plan = new TriagePlan([new TriageDecision("F1.1", TriageAction.Assign, "r", "b1", null)]);

        RepairPlanner.TasksByOwner(plan, ledger)["b1"].Single().Instruction.ShouldBe("Guard the null case in Parse.");
    }
}
