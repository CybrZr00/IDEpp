using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

// Change requests between owners. b1 owns the contract (IGreeter.cs) and Class1.cs, b2 owns Greeter.cs; in these
// tests b2 needs something from b1's Class1.cs, and sometimes the other way round.
//
// Two owners that run at the same time cannot be scripted to act in a fixed order, so where order matters a script
// takes a "gate": a run_command whose command names what it waits for - "until b1 has finished" - which the test's
// own command runner holds until the run's state says it is so. That is the only synchronisation these tests use.
public sealed partial class UltraPipelineRunTests
{
    private const string ReturnTwo = "Make Class1.Value return 2.";

    private ChatStreamEvent[] RequestChange(string file, string change = ReturnTwo, string rationale = "A test in Greeter's area expects it.") =>
        _client.Call(PipelineChangeTools.RequestChangeToolName, new { file, change, rationale });

    private ChatStreamEvent[] AwaitChange(string id, int minutes = 1) =>
        _client.Call(PipelineChangeTools.AwaitChangeToolName, new { id, minutes });

    private ChatStreamEvent[] Respond(string id, string decision, string reasoning = "") =>
        _client.Call(PipelineChangeTools.RespondChangeToolName, new { id, decision, reasoning });

    private ChatStreamEvent[] ChangeDone(string id, string summary = "Done.") =>
        _client.Call(PipelineChangeTools.ChangeDoneToolName, new { id, summary });

    private ChatStreamEvent[] Appeal(string id, string argument = "The test needs the value, and nothing else can give it.") =>
        _client.Call(PipelineChangeTools.AppealChangeToolName, new { id, argument });

    private ChatStreamEvent[] Claim(string path, string reason = "The greeter needs a place for its formats.") =>
        _client.Call(PipelineChangeTools.ClaimNewFileToolName, new { path, reason });

    private ChatStreamEvent[] Gate(string command) => _client.Call(ShellTools.RunCommandToolName, new { command, reason = "wait" });

    private ChatStreamEvent[] Arbitrate(string id, string ruling, string instruction) =>
        _client.Call(PipelineLeadTools.ArbitrateToolName, new { request = id, ruling, instruction });

    /// <summary>A run whose owners may run commands, and whose commands are the gates a script waits at.</summary>
    private UltraPipelineRun CreateGatedRun(Func<UltraTeamOptions, UltraTeamOptions>? configure = null)
    {
        UltraPipelineRun run = null!;
        run = CreateRun(
            o => (configure?.Invoke(o) ?? o) with { ShellCommandsEnabled = true },
            (command, reason, directory, ct) => PassGateAsync(run, command, ct));
        return run;
    }

    private static async Task<(bool IsError, string Text)> PassGateAsync(UltraPipelineRun run, string command, CancellationToken cancellationToken)
    {
        TeamWorkerState Agent(string name) => run.Workers.Single(w => w.Name == name);
        Func<bool> reached = command switch
        {
            "until b1 has finished" => () => Agent("b1").Phase is TeamWorkerPhase.Done or TeamWorkerPhase.Stopped,
            "until b1 is idle" => () => Agent("b1").Phase != TeamWorkerPhase.Working,
            "until b2 has finished" => () => Agent("b2").Phase is TeamWorkerPhase.Done or TeamWorkerPhase.Stopped,
            "until b1 is waiting" => () => Agent("b1").BlockedOn is not null,
            "until b1 is not waiting" => () => Agent("b1").BlockedOn is null,
            "until a request exists" => () => run.ChangeRequests.Count > 0,
            "until a request times out" => () => run.ChangeRequests.Any(r => r.State == ChangeRequestState.TimedOut),
            _ => throw new InvalidOperationException($"There is no gate called '{command}'."),
        };

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!reached())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The gate '{command}' was never reached.");
            }

            await Task.Delay(10, cancellationToken);
        }

        return (false, "ok");
    }

    /// <summary>What an agent had been told by the tool results of its <paramref name="n"/>th request (counting from 0).</summary>
    private string LastToolResultOf(string agent, int n) =>
        _client.Requests.Where(r => r.Worker == agent).ElementAt(n).Messages.Last().Content;

    // ---- An owner that has finished is called back ----

    [Fact]
    public async Task ARequestToAnOwnerWhoHasFinished_CallsItBack_ToAnswerAndMakeTheChange()
    {
        ScriptTheContract();
        _client
            .Script("b1",
                Report("Nothing to change."),
                // Called back:
                Respond("CR1", "accept", "Fair enough."), EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"),
                MergeToTheTeam(), ChangeDone("CR1", "Class1.Value returns 2."), Report("Class1 returns 2, as b2 asked."))
            .Script("b2",
                Gate("until b1 has finished"),
                RequestChange("Lib/Class1.cs"),
                Report("Too early."), // refused: its own request is still open
                AwaitChange("CR1"), AwaitChange("CR1"),
                SyncWithTheTeam(), EditFile("Lib/Greeter.cs", GreeterSource), Commit("Add Greeter"), MergeToTheTeam(),
                Report("Greeter implements IGreeter."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var request = run.ChangeRequests.ShouldHaveSingleItem();
        (request.Id, request.From, request.To, request.File, request.State).ShouldBe(("CR1", "b2", "b1", "Lib/Class1.cs", ChangeRequestState.Done));
        request.Reasoning.ShouldBe("Fair enough.");
        request.DoneSummary.ShouldBe("Class1.Value returns 2.");
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 2");
        ShowOnTeamBranch(run, "Lib/Greeter.cs").ShouldContain("class Greeter");

        // b1 had finished, so it was called back in a new conversation that knew the request.
        var calledBack = _client.Requests.Where(r => r.Worker == "b1").ElementAt(1).Messages;
        calledBack[1].Content.ShouldContain("You had finished building them");
        calledBack[1].Content.ShouldContain("<<<DATA request CR1");
        calledBack[1].Content.ShouldContain("b2 asks b1 to change Lib/Class1.cs.");

        // b2 could not report while its request was open, and was told what it came to.
        LastToolResultOf("b2", 3).ShouldContain("Your request CR1 to b1 is still open");
        _client.Requests.Where(r => r.Worker == "b2").Last().Messages.Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Content).ShouldContain(c => c.Contains("made the change for CR1"));

        result.Workers.Where(w => w.Name.StartsWith('b')).ShouldAllBe(w => w.Phase == TeamWorkerPhase.Done && w.BlockedOn == null);
        result.Workers.Single(w => w.Name == "b1").Merges.ShouldBe(1);
        Branches(run).ShouldBe([TeamBranch(run)]);

        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.Gaps.ShouldBeEmpty();
        summary.Stages.Single(s => s.Stage == "Build").Note.ShouldBe("2 merge(s) from 2 owner(s), 1 change request(s)");
        var record = await ReadAsync<ChangeRequestsRecord>(run, "build/change-requests.json");
        record.Requests.ShouldHaveSingleItem().State.ShouldBe(ChangeRequestState.Done);
        (await ReadAsync<BuildRecord>(run, "build/b1.json")).Summary.ShouldBe("Class1 returns 2, as b2 asked.");
    }

    // ---- An owner that is still working is told, and answers first ----

    [Fact]
    public async Task ARequestToAnOwnerWhoIsWorking_ReachesItAsANotice_AndItAnswersBeforeItDoesAnythingElse()
    {
        ScriptTheContract();
        _client
            .Script("b1",
                Gate("until a request exists"),
                Report("Done?"),                                  // refused: a request waits for its answer
                EditFile("Lib/Class1.cs", Class1Returning2),      // refused by the guard
                Respond("CR1", "accept"),
                EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), MergeToTheTeam(),
                ChangeDone("CR1"), Report("Class1 returns 2."))
            .Script("b2",
                RequestChange("Lib/Class1.cs"), AwaitChange("CR1"), AwaitChange("CR1"),
                SyncWithTheTeam(), EditFile("Lib/Greeter.cs", GreeterSource), Commit("Add Greeter"), MergeToTheTeam(),
                Report("Greeter implements IGreeter."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        run.ChangeRequests.ShouldHaveSingleItem().State.ShouldBe(ChangeRequestState.Done);

        // It was told, once, at the end of the tool call it was in...
        var notice = LastToolResultOf("b1", 1);
        notice.ShouldContain("CR1 is waiting for your answer.");
        notice.ShouldContain("<<<DATA request CR1");
        // ...could not report, and could not edit, until it had answered...
        LastToolResultOf("b1", 2).ShouldContain("CR1 from b2 is waiting for your answer: respond_change first.");
        LastToolResultOf("b1", 3).ShouldContain("Refused: edit_file. CR1 from b2 is waiting for your answer - answer it first.");
        // ...and was never called back, because it never stopped.
        _client.Requests.Where(r => r.Worker == "b1").SelectMany(r => r.Messages)
            .ShouldNotContain(m => m.Content.Contains("You had finished building them"));
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 2");
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldBeEmpty();
    }

    // ---- Rejections and appeals ----

    [Fact]
    public async Task ARejectionAppealedToTheLead_WhoRulesForTheRequester_ObligesTheOwner_WhoIsCalledBackAgain()
    {
        _client
            .Script("lead", NoSurveyors(), Arbitrate("CR1", "requester", "b1: make Class1.Value return 2 and change nothing else."))
            .Script("d1", WriteStub(), Commit(), BuildDesign())
            .Script("b1",
                Report("Nothing to change."),
                // Called back to answer:
                Respond("CR1", "reject", "Class1.Value is part of the public contract and stays 1."), Report("Rejected CR1."),
                // Called back to obey the ruling:
                EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), MergeToTheTeam(),
                ChangeDone("CR1", "Returns 2, as the lead ruled."), Report("Class1 returns 2 on the lead's ruling."))
            .Script("b2",
                Gate("until b1 has finished"),
                RequestChange("Lib/Class1.cs"),
                AwaitChange("CR1"),               // the rejection
                Gate("until b1 is idle"),
                Appeal("CR1"),
                AwaitChange("CR1"), AwaitChange("CR1"),
                SyncWithTheTeam(), EditFile("Lib/Greeter.cs", GreeterSource), Commit("Add Greeter"), MergeToTheTeam(),
                Report("Greeter implements IGreeter."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var request = run.ChangeRequests.ShouldHaveSingleItem();
        (request.State, request.Binding, request.AppealUsed).ShouldBe((ChangeRequestState.Done, true, true));
        request.Reasoning.ShouldBe("Class1.Value is part of the public contract and stays 1.");
        request.Arbitration.ShouldBe("b1: make Class1.Value return 2 and change nothing else.");
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 2");

        // The lead was asked twice - for the roster and for the appeal - and saw both sides of it.
        var asked = _client.Requests.Where(r => r.Worker == "lead").ToList();
        asked.Count.ShouldBe(2);
        asked[1].Messages[1].Content.ShouldContain("b1 refused: Class1.Value is part of the public contract");
        asked[1].Messages[1].Content.ShouldContain("b2 appealed: The test needs the value");

        // b1 was called back twice: once for the request, once for the ruling.
        _client.Requests.Where(r => r.Worker == "b1").Count(r => r.Messages.Count == 2 && r.Messages[1].Content.Contains("You had finished building them")).ShouldBe(2);
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARejectionAppealedToTheLead_WhoRulesForTheOwner_IsFinal_AndTheRequesterCarriesOn()
    {
        _client
            .Script("lead", NoSurveyors(), Arbitrate("CR1", "owner", "b2: use Class1.Value as it is."))
            .Script("d1", WriteStub(), Commit(), BuildDesign())
            .Script("b1",
                Report("Nothing to change."),
                Respond("CR1", "reject", "Class1.Value is part of the public contract and stays 1."), Report("Rejected CR1."))
            .Script("b2",
                Gate("until b1 has finished"),
                RequestChange("Lib/Class1.cs"), AwaitChange("CR1"),
                Gate("until b1 is idle"),
                Appeal("CR1"),
                EditFile("Lib/Greeter.cs", GreeterSource), Commit("Add Greeter"), MergeToTheTeam(),
                Report("Greeter implements IGreeter, without the change."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var request = run.ChangeRequests.ShouldHaveSingleItem();
        (request.State, request.Binding, request.AppealUsed).ShouldBe((ChangeRequestState.Rejected, false, true));
        LastToolResultOf("b2", 5).ShouldContain("The lead ruled for b1: the refusal of CR1 stands.");
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 1");
        _client.Requests.Where(r => r.Worker == "b1").Count(r => r.Messages.Count == 2 && r.Messages[1].Content.Contains("You had finished building them")).ShouldBe(1);
    }

    [Fact]
    public async Task AnAppealTheLeadDoesNotRuleOn_LeavesTheOwnersRefusalStanding_AndIsAGap()
    {
        _client
            .Script("lead", NoSurveyors(), Text("Both have a point."), Text("Still both."), Text("Yes."))
            .Script("d1", WriteStub(), Commit(), BuildDesign())
            .Script("b1",
                Report("Nothing to change."),
                Respond("CR1", "reject", "Class1.Value is part of the public contract and stays 1."), Report("Rejected CR1."))
            .Script("b2",
                Gate("until b1 has finished"),
                RequestChange("Lib/Class1.cs"), AwaitChange("CR1"),
                Gate("until b1 is idle"),
                Appeal("CR1"),
                Report("Carrying on without it."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var request = run.ChangeRequests.ShouldHaveSingleItem();
        (request.State, request.Binding).ShouldBe((ChangeRequestState.Rejected, false));
        LastToolResultOf("b2", 5).ShouldContain("the refusal of CR1 stands");
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldContain(g => g.Contains("No ruling was given on CR1"));
    }

    [Fact]
    public async Task TheStageWaitsForAnOwnerItCalledBack_EvenAfterEveryoneElseHasFinished()
    {
        ScriptTheContract();
        _client
            .Script("b1",
                Report("Nothing to change."),
                // Called back - and held at a gate until b2 has finished, so it is the last agent still working.
                Respond("CR1", "accept"), ChangeDone("CR1", "Nothing was needed."),
                Gate("until b2 has finished"),
                Report("The final report, after b2 had finished."))
            .Script("b2",
                Gate("until b1 has finished"),
                RequestChange("Lib/Class1.cs"), AwaitChange("CR1"), AwaitChange("CR1"),
                Report("Done."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Workers.Where(w => w.Name.StartsWith('b')).ShouldAllBe(w => w.Phase == TeamWorkerPhase.Done);
        (await ReadAsync<BuildRecord>(run, "build/b1.json")).Summary.ShouldBe("The final report, after b2 had finished.");
        var stage = (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.Single(s => s.Stage == "Build");
        stage.Agents.Count(a => a.Name == "b1").ShouldBe(2); // its first conversation and its callback
        Directory.Exists(Path.Combine(run.RunDirectory, "b1")).ShouldBeFalse();
    }

    // ---- Deadlock, time and giving up ----

    [Fact]
    public async Task TwoOwnersWaitingOnEachOther_AreStopped_AndTheSecondIsToldToAnswerFirst()
    {
        ScriptTheContract();
        _client
            .Script("b1",
                RequestChange("Lib/Greeter.cs", "Greet with a hello.", "Class1 would like it."),
                AwaitChange("CR1"), AwaitChange("CR1"),
                SyncWithTheTeam(),
                Respond("CR2", "reject", "Class1.Value stays 1; the test can use it as it is."),
                Report("Waited on b2, and answered it."))
            .Script("b2",
                Gate("until b1 is waiting"),
                RequestChange("Lib/Class1.cs"),
                AwaitChange("CR2"),                       // refused: b1 is already waiting on b2
                Respond("CR1", "accept"),
                EditFile("Lib/Greeter.cs", GreeterSource), Commit("Add Greeter"), MergeToTheTeam(),
                ChangeDone("CR1"),
                Gate("until b1 is not waiting"),
                AwaitChange("CR2"),                       // now b1 can answer it
                Report("Greeter implements IGreeter."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.Where(r => r.Worker == "b2").ElementAt(3).Messages.Last().Content
            .ShouldContain("Waiting on b1 would deadlock");
        run.ChangeRequests.Select(r => (r.Id, r.From, r.To, r.State)).ShouldBe(
            [("CR1", "b1", "b2", ChangeRequestState.Done), ("CR2", "b2", "b1", ChangeRequestState.Rejected)]);
        result.Workers.Where(w => w.Name.StartsWith('b')).ShouldAllBe(w => w.Phase == TeamWorkerPhase.Done);
    }

    [Fact]
    public async Task ARequestNobodyAnswersInTime_TimesOut_AndTheRequesterIsToldSo()
    {
        ScriptTheContract();
        _client
            .Script("b1", Gate("until a request times out"), Report("Never got to it."))
            .Script("b2", RequestChange("Lib/Class1.cs"), AwaitChange("CR1", minutes: 5), Report("Carrying on without it."));
        var run = CreateGatedRun(o => o with
        {
            PipelineChangeRequestTimeout = TimeSpan.FromMilliseconds(400),
            PipelineChangeWaitUnit = TimeSpan.FromMilliseconds(100),
        });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var request = run.ChangeRequests.ShouldHaveSingleItem();
        request.State.ShouldBe(ChangeRequestState.TimedOut);
        request.Reasoning.ShouldNotBeNull().ShouldContain("did not answer");
        LastToolResultOf("b2", 2).ShouldContain("CR1 timed out");
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOwnerCalledBackAsOftenAsItMayBe_IsNotCalledAgain_AndTheRequestIsCancelled()
    {
        ScriptTheContract();
        _client
            .Script("b1",
                Report("Nothing to change."),
                Respond("CR1", "accept"), ChangeDone("CR1", "Nothing was needed."), Report("Handled CR1."),
                Respond("CR2", "accept"), ChangeDone("CR2", "Nothing was needed."), Report("Handled CR2."))
            .Script("b2",
                Gate("until b1 has finished"),
                RequestChange("Lib/Class1.cs", "First thing."), AwaitChange("CR1"), AwaitChange("CR1"),
                Gate("until b1 is idle"),
                RequestChange("Lib/Class1.cs", "Second thing."), AwaitChange("CR2"), AwaitChange("CR2"),
                Gate("until b1 is idle"),
                RequestChange("Lib/Class1.cs", "Third thing."), AwaitChange("CR3"),
                Report("Three requests were enough."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        run.ChangeRequests.Select(r => (r.Id, r.State)).ShouldBe(
            [("CR1", ChangeRequestState.Done), ("CR2", ChangeRequestState.Done), ("CR3", ChangeRequestState.Cancelled)]);
        run.ChangeRequests[2].Reasoning.ShouldNotBeNull().ShouldContain("called back 2 times");
        _client.Requests.Where(r => r.Worker == "b1").Count(r => r.Messages.Count == 2 && r.Messages[1].Content.Contains("You had finished building them")).ShouldBe(2);
        _client.Requests.Where(r => r.Worker == "b2").Last().Messages.Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Content).ShouldContain(c => c.Contains("CR3 was cancelled"));
    }

    [Fact]
    public async Task AnOwnerThatStopsWithoutFinishing_TakesItsOpenRequestsDownWithIt()
    {
        ScriptTheContract();
        _client
            .Script("b1", RequestChange("Lib/Greeter.cs", "Greet with a hello.", "Class1 would like it."), Text("Waiting."), Text("Still."), Text("Yes."))
            .Script("b2", Gate("until a request exists"), Gate("until b1 has finished"), Report("Nothing needed from b1."));
        var run = CreateGatedRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Workers.Single(w => w.Name == "b1").Phase.ShouldBe(TeamWorkerPhase.Stopped);
        var request = run.ChangeRequests.ShouldHaveSingleItem();
        request.State.ShouldBe(ChangeRequestState.Cancelled);
        request.Reasoning.ShouldNotBeNull().ShouldContain("b1 stopped without finishing");
    }

    // ---- The tools' own checks ----

    [Fact]
    public async Task ARequestForAFileThatIsNobodysOrItsOwn_IsRefused_BeforeItIsOpened()
    {
        ScriptTheContract();
        _client.Script("b2",
            RequestChange("Lib/Nope.cs"),
            RequestChange("Lib/Greeter.cs"),
            RequestChange("../outside.cs"),
            RequestChange("Lib/Class1.cs", change: "", rationale: "Because."),
            Report("Nothing needed."));
        var run = CreateRun();

        await RunAsync(run);

        var told = _client.Requests.Where(r => r.Worker == "b2").Last().Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content).ToList();
        told[0].ShouldContain("Lib/Nope.cs is not owned by anyone");
        told[1].ShouldContain("Lib/Greeter.cs is your own file");
        told[2].ShouldContain("is not a plain path relative to the solution folder");
        told[3].ShouldContain("Say what should change");
        run.ChangeRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOwnerClaimsANewFile_WhichThenBelongsToIt_ButNothingThatIsOwnedBuildDefiningOrThere()
    {
        ScriptTheContract();
        _client.Script("b2",
            EditFile("Lib/Extra.cs", "public class Extra { }\r\n"),                // not yet its file
            Claim("Lib/Extra.cs"),
            Claim("Lib/Class1.cs"),                                               // b1's
            Claim("Lib/New.csproj"),                                              // defines the build
            Claim(".gitignore"),                                                  // exists, and is nobody's
            Claim("Lib/Other.cs", reason: "x"),                                   // no reason given
            EditFile("Lib/Extra.cs", "public class Extra { }\r\n"),
            EditFile("Lib/Greeter.cs", GreeterSource),
            Commit("Add Greeter and Extra"), MergeToTheTeam(),
            Report("Greeter implements IGreeter; Extra holds the formats."));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var told = _client.Requests.Where(r => r.Worker == "b2").Last().Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content).ToList();
        told[0].ShouldContain("'Lib/Extra.cs' is not owned by anyone");
        told[1].ShouldContain("Claimed Lib/Extra.cs");
        told[2].ShouldContain("Lib/Class1.cs is owned by b1");
        told[3].ShouldContain("defines how the solution builds");
        told[4].ShouldContain(".gitignore already exists");
        told[5].ShouldContain("Say why your work needs this file");

        // The claim is the rule from then on, and the record says so.
        ShowOnTeamBranch(run, "Lib/Extra.cs").ShouldContain("class Extra");
        var ownership = await ReadAsync<OwnershipRecord>(run, "ownership.json");
        ownership.Files["Lib/Extra.cs"].ShouldBe("b2");
        ownership.Files.ShouldNotContainKey("Lib/Other.cs");
        result.Changes.Select(c => c.RelativePath).Order().ShouldBe(["Lib/Extra.cs", "Lib/Greeter.cs", "Lib/IGreeter.cs"]);
    }
}
