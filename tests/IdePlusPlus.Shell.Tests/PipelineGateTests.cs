using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The deterministic gate against a real repository, a real <c>dotnet build</c> and a real <c>dotnet test</c>: a solution that
/// builds passes, one that does not fails naming the file and the compiler's error, a failing test fails the gate by name, and
/// the conformity check - whose formatter rewrites files - can change nothing but its own throwaway checkout.
/// </summary>
public sealed class PipelineGateTests : IDisposable
{
    private readonly TeamRepositoryFixture _repo = new();
    private readonly List<RunWorkspace> _opened = [];

    public void Dispose()
    {
        foreach (var workspace in _opened)
        {
            workspace.CloseAsync().GetAwaiter().GetResult();
        }

        _repo.Dispose();
    }

    private string Class1Path => Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs");

    private async Task<(RunWorkspace Workspace, PipelineGate Gate)> OpenAsync(
        Func<IReadOnlyList<string>, string, CancellationToken, Task<DotnetRunResult>>? runDotnet = null)
    {
        var workspace = new RunWorkspace(
            _repo.TeamGit, Path.Combine(_repo.Root, "ultra"), "g1", _repo.RepositoryRoot, Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"), _ => long.MaxValue / 2);
        _opened.Add(workspace);
        await workspace.OpenAsync(null, 0, CancellationToken.None);
        return (workspace, new PipelineGate(workspace, new SemaphoreSlim(2), "Debug", runDotnet));
    }

    /// <summary>A test project beside Lib, in the solution, with one test that passes and - if asked - one that fails.</summary>
    private string AddTestProject(bool includeFailingTest)
    {
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
            """<Solution><Project Path="Lib/Lib.csproj" /><Project Path="Tests/Tests.csproj" /></Solution>""");
        var directory = Directory.CreateDirectory(Path.Combine(_repo.RepositoryRoot, "Tests")).FullName;
        File.WriteAllText(Path.Combine(directory, "Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="4.0.0" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "ClassTests.cs"),
            "using Xunit;\r\npublic class ClassTests\r\n{\r\n    [Fact]\r\n    public void Passes() => Assert.Equal(1, new Class1().Value);\r\n"
            + (includeFailingTest ? "\r\n    [Fact]\r\n    public void Fails() => Assert.Equal(2, new Class1().Value);\r\n" : "")
            + "}\r\n");
        return Path.Combine(directory, "Tests.csproj");
    }

    private string Git(string directory, params string[] arguments) => TeamRepositoryFixture.Git(directory, arguments);

    // ---- The build ----

    [Fact]
    public async Task ASolutionThatBuilds_PassesTheGate_WithNothingElseToCheck()
    {
        var (workspace, gate) = await OpenAsync();

        var result = await gate.RunAsync(new GateRequest(workspace.Start, [], []), CancellationToken.None);

        result.Ok.ShouldBeTrue();
        result.BuildOk.ShouldBeTrue();
        result.BuildSummary.ShouldStartWith("Build succeeded (Debug).");
        result.BuildErrors.ShouldBeEmpty();
        result.Commit.ShouldBe(workspace.Start);
        result.TestsRan.ShouldBeFalse();
        result.TestNote.ShouldBe("this solution has no test projects");
        result.Conformity.Ran.ShouldBeFalse();
        result.Conformity.Note.ShouldBe("no C# files were changed");
        GateFindings.From(result).ShouldBeEmpty();
    }

    [Fact]
    public async Task ASolutionThatDoesNotBuild_FailsTheGate_NamingTheFileAndTheError_AndSkipsTheRest()
    {
        File.WriteAllText(Class1Path, "public class Class1\r\n{\r\n    public int Value => 1\r\n}\r\n"); // no semicolon
        var (workspace, gate) = await OpenAsync();

        var result = await gate.RunAsync(new GateRequest(workspace.Start, [], ["Lib/Class1.cs"]), CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.BuildOk.ShouldBeFalse();
        result.BuildSummary.ShouldStartWith("Build FAILED (Debug)");
        var error = result.BuildErrors.ShouldHaveSingleItem();
        (error.File, error.Code).ShouldBe(("Lib/Class1.cs", "CS1002"));
        error.Line.ShouldBe(3);
        result.TestsRan.ShouldBeFalse();
        result.TestNote.ShouldBe("skipped: the build failed");
        result.Conformity.Ran.ShouldBeFalse(); // there is nothing to check the style of in a solution that does not build

        var finding = GateFindings.From(result).ShouldHaveSingleItem();
        finding.Finding.Files.ShouldBe(["Lib/Class1.cs"]);
        finding.Finding.Severity.ShouldBe(TeamFindingSeverity.High);
    }

    [Fact]
    public async Task TheGateChecksTheCommitItIsAsked_NotTheOneItLastChecked()
    {
        var (workspace, gate) = await OpenAsync();
        var d1 = await workspace.AddWorktreeAsync("d1", workspace.Start, CancellationToken.None);
        File.WriteAllText(Path.Combine(d1.Path, "Lib", "Extra.cs"), "public class Extra { }\r\n");
        await _repo.TeamGit.CommitAllAsync(d1.Path, "extra", d1.Author);
        var later = await _repo.TeamGit.GetHeadAsync(d1.Path);

        var first = await gate.RunAsync(new GateRequest(later, [], []), CancellationToken.None);
        File.Exists(Path.Combine(gate.Worktree!.Path, "Lib", "Extra.cs")).ShouldBeTrue();
        var second = await gate.RunAsync(new GateRequest(workspace.Start, [], []), CancellationToken.None);

        (first.Commit, first.Ok).ShouldBe((later, true));
        (second.Commit, second.Ok).ShouldBe((workspace.Start, true));
        File.Exists(Path.Combine(gate.Worktree.Path, "Lib", "Extra.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task OnlyOneBuildOfTheCheckoutRunsAtATime_ButEachGetsItsOwnAnswer()
    {
        var (workspace, gate) = await OpenAsync();

        var results = await Task.WhenAll(
            gate.RunAsync(new GateRequest(workspace.Start, [], []), CancellationToken.None),
            gate.RunAsync(new GateRequest(workspace.Start, [], []), CancellationToken.None));

        results.ShouldAllBe(r => r.Ok);
    }

    [Fact]
    public async Task ThePreflightBuild_ReportsAFailureWithTheBuildsOwnReport()
    {
        File.WriteAllText(Class1Path, "public class Class1\r\n{\r\n    public int Value => 1\r\n}\r\n");
        var (workspace, gate) = await OpenAsync();

        var build = await gate.BuildAsync(workspace.Start, CancellationToken.None);

        build.Ok.ShouldBeFalse();
        build.Report.ShouldContain("CS1002");
        build.Errors.ShouldHaveSingleItem().File.ShouldBe("Lib/Class1.cs");
    }

    // ---- The tests ----

    [Fact]
    public async Task AFailingTest_FailsTheGate_NamingTheTest_AndAPassingOneIsCounted()
    {
        var testProject = AddTestProject(includeFailingTest: true);
        var (workspace, gate) = await OpenAsync();

        var result = await gate.RunAsync(new GateRequest(workspace.Start, [testProject], []), CancellationToken.None);

        result.BuildOk.ShouldBeTrue(result.BuildSummary);
        result.TestsRan.ShouldBeTrue(result.TestNote);
        (result.TestsPassed, result.TestsFailed).ShouldBe((1, 1));
        result.Ok.ShouldBeFalse();
        var failure = result.FailedTests.ShouldHaveSingleItem();
        failure.Name.ShouldContain("Fails");
        failure.Message.ShouldNotBeNull().ShouldContain("Values differ");

        var finding = GateFindings.From(result).ShouldHaveSingleItem();
        finding.Finding.Title.ShouldContain("Test failed:");
        finding.Finding.Severity.ShouldBe(TeamFindingSeverity.High);
    }

    [Fact]
    public async Task PassingTests_PassTheGate()
    {
        var testProject = AddTestProject(includeFailingTest: false);
        var (workspace, gate) = await OpenAsync();

        var result = await gate.RunAsync(new GateRequest(workspace.Start, [testProject], []), CancellationToken.None);

        result.TestsRan.ShouldBeTrue(result.TestNote);
        (result.TestsPassed, result.TestsFailed).ShouldBe((1, 0));
        result.Ok.ShouldBeTrue();
        GateFindings.From(result).ShouldBeEmpty();
    }

    // ---- Conformity ----

    [Fact]
    public async Task AFormatterThatRewritesFiles_ChangesNothingButTheGatesOwnCheckout_AndThatIsPutBack()
    {
        var (workspace, gate) = await OpenAsync((arguments, directory, ct) =>
        {
            if (arguments[2] == "style")
            {
                // `dotnet format style` fixes what it can in place - here, it appends to the first file it was given.
                var file = arguments.Skip(4).First();
                File.AppendAllText(file, "// formatted\r\n");
                return Task.FromResult(new DotnetRunResult(2, "Lib/Class1.cs(3,5): warning IDE0055: Fix formatting"));
            }

            return Task.FromResult(new DotnetRunResult(0, ""));
        });
        var teamHeadBefore = await workspace.TeamHeadAsync(CancellationToken.None);

        var result = await gate.RunAsync(new GateRequest(workspace.Start, [], ["Lib/Class1.cs"]), CancellationToken.None);

        result.Conformity.Ran.ShouldBeTrue();
        result.Conformity.Clean.ShouldBeFalse();
        result.Conformity.Style.ShouldHaveSingleItem().ShouldContain("IDE0055");
        result.Ok.ShouldBeTrue(); // advisory: it never holds up a pass
        GateFindings.From(result).ShouldHaveSingleItem().Finding.Severity.ShouldBe(TeamFindingSeverity.Low);

        // The rewrite is not on the team branch, not in the user's file, and not left in the gate's checkout either.
        (await workspace.TeamHeadAsync(CancellationToken.None)).ShouldBe(teamHeadBefore);
        File.ReadAllText(Class1Path).ShouldNotContain("// formatted");
        File.ReadAllText(Path.Combine(gate.Worktree!.Path, "Lib", "Class1.cs")).ShouldNotContain("// formatted");
        Git(gate.Worktree.Path, "status", "--porcelain").ShouldBeEmpty();
        Git(_repo.RepositoryRoot, "show", $"{workspace.TeamBranch}:Lib/Class1.cs").ShouldNotContain("// formatted");
    }
}
