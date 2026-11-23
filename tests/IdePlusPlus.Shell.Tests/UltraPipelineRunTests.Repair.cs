using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

// Triage and repair: what the judges and the gate found is handed to the owners of the files it is about - by rule, or by the
// lead where a rule cannot say - and the owners repair it in the checkouts they built it in.
public sealed partial class UltraPipelineRunTests
{
    private const string Class1Returning4 = "public class Class1\r\n{\r\n    public int Value => 4;\r\n}\r\n";

    private ChatStreamEvent[] LeadTriage(params object[] decisions) =>
        _client.Call(PipelineLeadTools.SubmitTriageToolName, new { decisions });

    /// <summary>A finding that carries the judge's own fix and who is to make it.</summary>
    private static object FindingFor(
        string severity, string title, string fix, string fixBy = "owner", string quote = ValueLine, params string[] files) => new
    {
        title,
        severity,
        category = "correctness",
        files = files.Length == 0 ? new[] { "Lib/Class1.cs" } : files,
        evidence = "Read the file.",
        quote,
        fix,
        fixBy,
    };

    private async Task<TriageRecord> TriageOfAsync(UltraPipelineRun run, int round) => await ReadAsync<TriageRecord>(run, $"loop-{round}/triage.json");

    private async Task<RepairRecord> RepairOfAsync(UltraPipelineRun run, int round) => await ReadAsync<RepairRecord>(run, $"loop-{round}/repairs.json");

    /// <summary>The first message of the repair conversation b1 (or whoever) was handed, which is how a test sees what it was told.</summary>
    private string RepairOpeningOf(string owner, int nth = 0) => _client.Requests
        .Where(r => r.Worker == owner && r.Messages[0].Content.Contains("You built these files earlier in this run"))
        .Where(r => r.Messages.Count == 2) // the opening, before any tool result
        .ElementAt(nth).Messages[1].Content;

    // ---- By rule ----

    [Fact]
    public async Task AnOwner_IsHandedTheFindingsAboutItsFiles_RepairsThem_AndTheNextRoundPasses()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("b1",
                EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), MergeToTheTeam(), Report("Class1 returns 2."),
                SyncWithTheTeam(), EditFile("Lib/Class1.cs", Class1Returning3), Commit("Class1 returns 3"), MergeToTheTeam(),
                Report("Class1 returns 3, as the judge asked."))
            .Script("j1",
                Verdict("fail", "Wrong.", [FindingFor("medium", "Value is wrong", "Make Value return 3.", quote: "public int Value => 2;")]),
                Verdict("pass", "Fixed."));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 3");
        _client.Requests.Count(r => r.Worker == "lead").ShouldBe(1); // the roster; nothing here needed the lead

        var opening = RepairOpeningOf("b1");
        opening.ShouldContain("This is repair round 1.");
        opening.ShouldContain("<<<DATA findings");
        opening.ShouldContain("F1.1 [medium] Value is wrong");
        opening.ShouldContain("found by: j1; files: Lib/Class1.cs");
        opening.ShouldContain("quote: public int Value => 2;");
        opening.ShouldContain("instruction: Make Value return 3.");
        opening.ShouldEndWith("Repair them in your files, merge, then call build_submit.");

        var triage = await TriageOfAsync(run, 1);
        (triage.AskedTheLead, triage.LeadDecided).ShouldBe((false, true));
        var decision = triage.Decisions.ShouldHaveSingleItem();
        (decision.FindingId, decision.Action, decision.Owner, decision.Instruction, decision.Reason).ShouldBe(
            ("F1.1", TriageAction.Assign, "b1", "Make Value return 3.", RepairPlanner.AssignReason));

        var repairs = await RepairOfAsync(run, 1);
        repairs.Changed.ShouldBeTrue();
        var owner = repairs.Owners.ShouldHaveSingleItem();
        (owner.Owner, owner.Submitted, owner.Summary, owner.Merges).ShouldBe(("b1", true, "Class1 returns 3, as the judge asked.", 1));
        owner.Findings.ShouldBe(["F1.1"]);

        var round2 = await FindingsAsync(run, 2);
        round2.Passed.ShouldBeTrue();
        round2.Findings.ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Fixed);
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.TakeLast(5).Select(s => (s.Stage, s.Round)).ShouldBe(
            [("Gate", 1), ("Judge", 1), ("Repair", 1), ("Gate", 2), ("Judge", 2)]);
        Branches(run).ShouldBe([TeamBranch(run)]);
    }

    [Fact]
    public async Task AFindingThatIsOutsideTheGoal_GoesToTheUser_AndTheRunPassesWithoutRepairingIt()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "Out of scope.", [FindingFor("medium", "Value should come from settings", "Read it from the settings file.", fixBy: "user")]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var decision = (await TriageOfAsync(run, 1)).Decisions.ShouldHaveSingleItem();
        (decision.Action, decision.Reason).ShouldBe((TriageAction.Defer, RepairPlanner.DeferReason));
        decision.NextPrompt.ShouldBe("In Lib/Class1.cs: Value should come from settings. Read it from the settings file.");
        File.Exists(Path.Combine(run.PhasesDirectory, "loop-1", "repairs.json")).ShouldBeFalse(); // nobody was asked to repair anything
        _client.Requests.Count(r => r.Worker == "b1").ShouldBe(1);                             // only the build
        _client.Requests.Count(r => r.Worker == "j1").ShouldBe(1);                             // and no second round

        var round1 = await FindingsAsync(run, 1);
        round1.Passed.ShouldBeTrue();
        var finding = round1.Findings.ShouldHaveSingleItem();
        (finding.Status, finding.Note).ShouldBe((FindingStatus.Dismissed, $"for the user: {RepairPlanner.DeferReason}"));
    }

    [Fact]
    public async Task AnOwnerWhoMergesNothing_EndsTheLoop_AsNoProgress_AndSaysWhatItCouldNotFix()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("b1",
                Report("Nothing to change."),
                Report("I could not fix it.", "F1.1: it needs a redesign of the value, which is not mine to do."))
            .Script("j1", Verdict("fail", "Wrong.", [FindingFor("medium", "Value is wrong", "Make Value return 3.")]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.NoProgress);
        var repairs = await RepairOfAsync(run, 1);
        repairs.Changed.ShouldBeFalse();
        var owner = repairs.Owners.ShouldHaveSingleItem();
        (owner.Submitted, owner.Merges).ShouldBe((true, 0));
        owner.Unmet.ShouldBe(["F1.1: it needs a redesign of the value, which is not mine to do."]);
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldHaveSingleItem()
            .ShouldBe("The loop stopped after round 1: the repair changed nothing (no owner merged anything), so judging the same code again would find the same things.");
        _client.Requests.Count(r => r.Worker == "j1").ShouldBe(1);
    }

    // ---- By the lead ----

    [Fact]
    public async Task AFindingThatSpansTwoOwnersFiles_IsDecidedByTheLead_AndOnlyTheOwnerItNamesRepairsIt()
    {
        _client
            .Script("lead", NoSurveyors(), LeadTriage(new
            {
                finding = "F1.1", action = "assign", owner = "b2", instruction = "Make Greeter greet the value Class1 holds.", reason = "Greeter is where it belongs.",
            }))
            .Script("d1", WriteStub(), Commit(), BuildDesign())
            .Script("b1", [EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), .. MergeWhicheverWayTheRaceGoes(), Report("Class1 returns 2.")])
            .Script("b2",
                [SyncWithTheTeam(), EditFile("Lib/Greeter.cs", GreeterSource), Commit("Add Greeter"), .. MergeWhicheverWayTheRaceGoes(), Report("Greeter implements IGreeter.")])
            .Script("b2",
                SyncWithTheTeam(), EditFile("Lib/Greeter.cs", GreeterSource + "// repaired\r\n"), Commit("Greeter repaired"), MergeToTheTeam(), Report("Greeter repaired."))
            .Script("j1", Verdict("fail", "They disagree.", [FindingFor(
                "medium", "Class1 and Greeter disagree about the value", "Make them agree.", quote: "public int Value => 2;", files: ["Lib/Class1.cs", "Lib/Greeter.cs"])]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.Count(r => r.Worker == "lead").ShouldBe(2); // the roster, and the triage
        var triage = await TriageOfAsync(run, 1);
        (triage.AskedTheLead, triage.LeadDecided).ShouldBe((true, true));
        var decision = triage.Decisions.ShouldHaveSingleItem();
        (decision.Owner, decision.Instruction).ShouldBe(("b2", "Make Greeter greet the value Class1 holds."));

        RepairOpeningOf("b2").ShouldContain("instruction: Make Greeter greet the value Class1 holds.");
        _client.Requests.ShouldNotContain(r => r.Worker == "b1" && r.Messages[0].Content.Contains("You built these files earlier in this run"));
        (await RepairOfAsync(run, 1)).Owners.Select(o => o.Owner).ShouldBe(["b2"]);
        ShowOnTeamBranch(run, "Lib/Greeter.cs").ShouldContain("// repaired");
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.Select(s => s.Stage).ShouldContain("Triage");
    }

    [Fact]
    public async Task AFindingTheLeadDismisses_StopsBlocking_AndIsNotRepaired()
    {
        _client
            .Script("lead", NoSurveyors(), LeadTriage(new
            {
                finding = "F1.1", action = "dismiss", reason = "The design chose this on purpose, and the goal does not ask to change it.",
            }))
            .Script("j1", Verdict("fail", "A design matter.", [FindingFor("medium", "The value should be a parameter", "Make it a parameter.", fixBy: "design")]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.Count(r => r.Worker == "b1").ShouldBe(1);
        var finding = (await FindingsAsync(run, 1)).Findings.ShouldHaveSingleItem();
        (finding.Status, finding.Note).ShouldBe(
            (FindingStatus.Dismissed, "dismissed: The design chose this on purpose, and the goal does not ask to change it."));
        (await TriageOfAsync(run, 1)).AskedTheLead.ShouldBeTrue();
    }

    [Fact]
    public async Task ALeadThatCannotDecide_LeavesTheHardFindingOpen_AndTheLoopEndsWithItReported()
    {
        _client
            .Script("lead", NoSurveyors()) // and nothing for the triage: it never submits
            .Script("j1", Verdict("fail", "A design matter.", [FindingFor("medium", "The value should be a parameter", "Make it a parameter.", fixBy: "design")]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.NoProgress);
        var triage = await TriageOfAsync(run, 1);
        (triage.AskedTheLead, triage.LeadDecided).ShouldBe((true, false));
        (await FindingsAsync(run, 1)).Findings.ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Open);
        var gaps = (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps;
        gaps.ShouldContain(g => g.StartsWith("No triage was decided", StringComparison.Ordinal));
        gaps.ShouldContain(g => g.Contains("the repair changed nothing (no finding could be handed to an owner)"));
    }

    [Fact]
    public async Task AFindingThatCameBackAfterARepair_IsEscalated_AndPutToTheLeadWhoGivesItNewWords()
    {
        _client
            .Script("lead", NoSurveyors(), LeadTriage(new
            {
                finding = "F1.1", action = "assign", owner = "b1", instruction = "Do not patch the literal: make Value come from a constant.", reason = "The first repair only patched it.",
            }))
            .Script("b1",
                EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), MergeToTheTeam(), Report("Class1 returns 2."),
                SyncWithTheTeam(), EditFile("Lib/Class1.cs", Class1Returning3), Commit("Class1 returns 3"), MergeToTheTeam(), Report("Returns 3."),
                SyncWithTheTeam(), EditFile("Lib/Class1.cs", Class1Returning4), Commit("Class1 returns 4"), MergeToTheTeam(), Report("Returns 4."))
            .Script("j1",
                Verdict("fail", "Wrong.", [FindingFor("medium", "Value is wrong", "Make Value return 3.", quote: "public int Value => 2;")]),
                Verdict("fail", "Still wrong.", [FindingFor("medium", "Value is wrong", "Make Value return 3.", quote: "public int Value => 3;")]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone); // the third round's judges say nothing
        (await TriageOfAsync(run, 1)).AskedTheLead.ShouldBeFalse();
        var second = await TriageOfAsync(run, 2);
        (second.AskedTheLead, second.LeadDecided).ShouldBe((true, true));
        var round2 = await FindingsAsync(run, 2);
        round2.Findings.ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Escalated);

        var opening = RepairOpeningOf("b1", nth: 1);
        opening.ShouldContain("This is repair round 2.");
        opening.ShouldContain("seen in 2 rounds: it was repaired before and came back.");
        opening.ShouldContain("instruction: Do not patch the literal: make Value come from a constant.");
        _client.Requests.Where(r => r.Worker == "lead").Last().Messages[1].Content.ShouldContain("Value is wrong"); // what the lead was shown
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 4");
    }

    // ---- Change requests in a repair ----

    [Fact]
    public async Task AnOwnerWhoUsedItsCallbacksInTheBuild_HasThemAgainInTheRepair()
    {
        ScriptTheContract();
        _client
            .Script("b1",
                Report("Nothing to change."),
                // Called back twice in the build - as often as a stage allows:
                Respond("CR1", "accept", "Fair enough."), EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"),
                MergeToTheTeam(), ChangeDone("CR1", "Class1 returns 2."), Report("Done for CR1."),
                Respond("CR2", "accept", "Fair enough."), EditFile("Lib/Class1.cs", Class1Returning3), Commit("Class1 returns 3"),
                MergeToTheTeam(), ChangeDone("CR2", "Class1 returns 3."), Report("Done for CR2."),
                // And once more in the repair, where it has nothing of its own to fix:
                Respond("CR3", "accept", "Fair enough."), SyncWithTheTeam(), EditFile("Lib/Class1.cs", Class1Returning4), Commit("Class1 returns 4"),
                MergeToTheTeam(), ChangeDone("CR3", "Class1 returns 4."), Report("Done for CR3."))
            .Script("b2",
                Gate("until b1 has finished"),
                RequestChange("Lib/Class1.cs", "Return 2."), AwaitChange("CR1"), AwaitChange("CR1"),
                SyncWithTheTeam(),
                RequestChange("Lib/Class1.cs", "Return 3."), AwaitChange("CR2"), AwaitChange("CR2"),
                SyncWithTheTeam(), EditFile("Lib/Greeter.cs", GreeterSource), Commit("Add Greeter"), MergeToTheTeam(),
                Report("Greeter implements IGreeter."),
                // The repair:
                SyncWithTheTeam(),
                RequestChange("Lib/Class1.cs", "Return 4."), AwaitChange("CR3"), AwaitChange("CR3"),
                SyncWithTheTeam(), EditFile("Lib/Greeter.cs", GreeterSource + "// repaired\r\n"), Commit("Greeter repaired"), MergeToTheTeam(),
                Report("Greeter repaired."))
            .Script("j1", Verdict("fail", "Greeter is rough.", [FindingFor(
                "medium", "Greeter does not trim the name", "Trim the name before greeting.", quote: "public string Greet(string name)", files: "Lib/Greeter.cs")]));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        run.ChangeRequests.Select(r => (r.Id, r.From, r.To, r.State)).ShouldBe(
            [("CR1", "b2", "b1", ChangeRequestState.Done), ("CR2", "b2", "b1", ChangeRequestState.Done), ("CR3", "b2", "b1", ChangeRequestState.Done)]);
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 4");
        ShowOnTeamBranch(run, "Lib/Greeter.cs").ShouldContain("// repaired");

        // Each stage's record holds that stage's requests, and no other.
        (await ReadAsync<ChangeRequestsRecord>(run, "build/change-requests.json")).Requests.Select(r => r.Id).ShouldBe(["CR1", "CR2"]);
        (await ReadAsync<ChangeRequestsRecord>(run, "loop-1/change-requests.json")).Requests.Select(r => r.Id).ShouldBe(["CR3"]);
        (await RepairOfAsync(run, 1)).Owners.Select(o => o.Owner).ShouldBe(["b2"]);
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.Single(s => s.Stage == "Repair").Note
            .ShouldBe("1 merge(s) from 1 owner(s), 1 change request(s)");
    }
}
