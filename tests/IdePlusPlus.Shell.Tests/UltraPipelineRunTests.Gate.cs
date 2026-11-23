using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

// The check before the run, and the gate stage inside it. The other tests in this class run with a stand-in gate that passes
// at once; these are about the gate.
public sealed partial class UltraPipelineRunTests
{
    private bool _realGate;

    private static GateResult FailingGate(string commit) => new(
        commit, false, "Build FAILED (Debug), exit code 1. 1 error(s), 0 warning(s).",
        [new GateDiagnostic("Lib/Class1.cs", 3, "CS1002", "; expected")],
        TestsRan: false, 0, 0, 0, [], "skipped: the build failed", GateConformity.NotRun("skipped: the build failed"));

    // ---- Before the run ----

    [Fact]
    public async Task ASolutionThatDoesNotBuild_IsRefusedBeforeAnyModelIsCalled_WithTheBuildsOwnReport()
    {
        File.WriteAllText(
            Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs"), "public class Class1\r\n{\r\n    public int Value => 1\r\n}\r\n");
        _realGate = true;
        var run = CreateRun(o => o with { PreflightBuild = true });

        var error = await Should.ThrowAsync<UltraTeamPreflightException>(() => RunAsync(run));

        error.Report.ShouldContain("Build FAILED");
        error.Report.ShouldContain("CS1002");
        error.NotMirrored.ShouldBeEmpty();
        _client.Requests.ShouldBeEmpty();
        Branches(run).ShouldBeEmpty();
        WorktreeCount().ShouldBe(1);
        Directory.Exists(Path.Combine(run.RunDirectory, "_gate")).ShouldBeFalse();
    }

    [Fact]
    public async Task ASolutionThatBuilds_ThroughThePreflightAndTheRealGate_IsAPass()
    {
        _realGate = true;
        _client.Script("lead", NoSurveyors());
        var run = CreateRun(o => o with { PreflightBuild = true });
        var wasChecking = false;
        var saidSo = false;
        run.Changed += () =>
        {
            wasChecking |= run.IsCheckingBuild;
            saidSo |= run.SetupStatus?.StartsWith("Checking that the solution builds", StringComparison.Ordinal) == true;
        };

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        wasChecking.ShouldBeTrue();
        saidSo.ShouldBeTrue();
        run.IsCheckingBuild.ShouldBeFalse();

        var gate = await ReadAsync<GateRecord>(run, "loop-1/gate.json");
        gate.Result.BuildOk.ShouldBeTrue();
        gate.Result.Ok.ShouldBeTrue();
        gate.Result.TestNote.ShouldBe("this solution has no test projects");
        gate.Findings.ShouldBeEmpty();
        var stage = (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.Single(s => s.Stage == "Gate");
        (stage.Stage, stage.Round).ShouldBe(("Gate", 1));
        stage.Note.ShouldBe("builds; tests not run (this solution has no test projects); conformity not run");
        Directory.Exists(Path.Combine(run.RunDirectory, "_gate")).ShouldBeFalse();
        WorktreeCount().ShouldBe(1);
    }

    // ---- The gate stage ----

    [Fact]
    public async Task AGateThatFails_EndsTheRunWithoutAPass_AndRecordsWhatItFound()
    {
        GateOverride = (request, _) => Task.FromResult(FailingGate(request.Commit));
        _client.Script("lead", NoSurveyors());
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.LoopCapReached);
        var gate = await ReadAsync<GateRecord>(run, "loop-1/gate.json");
        gate.Result.Ok.ShouldBeFalse();
        var finding = gate.Findings.ShouldHaveSingleItem();
        (finding.Title, finding.Severity, finding.Category, finding.FixBy).ShouldBe(
            ("CS1002: ; expected", TeamFindingSeverity.High, "build", FindingFixBy.Owner));
        finding.Files.ShouldBe(["Lib/Class1.cs"]);

        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.EndReason.ShouldBe("LoopCapReached");
        summary.Gaps.ShouldHaveSingleItem().ShouldBe("The gate failed in round 1: does not build (1 error(s)).");
        summary.Stages.Last().Note.ShouldBe("does not build (1 error(s))");
    }

    [Fact]
    public async Task TheGate_IsAskedAboutTheTeamBranchAsTheOwnersLeftIt_AndWhichCSharpFilesTheRunChanged()
    {
        GateRequest? asked = null;
        GateOverride = (request, _) =>
        {
            asked = request;
            return Task.FromResult(PassingGate(request.Commit));
        };
        ScriptTheContract();
        _client
            .Script("b1", [EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), .. MergeWhicheverWayTheRaceGoes(), Report("Class1.Value is 2.")])
            .Script("b2", [Claim("Notes.md", "The greeter's notes belong with the greeter."), EditFile("Lib/Greeter.cs", GreeterSource), EditFile("Notes.md", "Greeter notes.\r\n"), Commit("Add Greeter"), .. MergeWhicheverWayTheRaceGoes(), Report("Greeter implements IGreeter.")]);
        var run = CreateRun();
        var testProject = Path.Combine(_repo.RepositoryRoot, "Tests", "Tests.csproj");

        var result = await run.RunAsync(
            "Make Class1.Value return 2.", _repo.RepositoryRoot, Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"), [testProject], CancellationToken.None);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        asked.ShouldNotBeNull();
        asked.Commit.ShouldBe(TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", TeamBranch(run)).Trim());
        asked.TestProjectPaths.ShouldBe([testProject]);

        // Every C# file the run changed, as the design spells paths - and not the notes file, which is not C#.
        asked.ChangedFiles.Order().ShouldBe(["Lib/Class1.cs", "Lib/Greeter.cs", "Lib/IGreeter.cs"]);
    }
}
