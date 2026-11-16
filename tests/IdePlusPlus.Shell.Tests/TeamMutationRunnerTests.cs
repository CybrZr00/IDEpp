using IdePlusPlus.Roslyn.Mutation;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.TestRunner;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The runner's own bookkeeping, with the mutant executor injected: every test here is about the
/// file being written and restored, which mutants are offered, and what the report says - not about
/// MSBuild, which the injected executor replaces.
/// </summary>
public class TeamMutationRunnerTests : IDisposable
{
    private const string Original = "class C { bool M(int a, int b) => a < b; }";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "IdePlusPlus.MutationRunnerTests", Guid.NewGuid().ToString("N"));

    public TeamMutationRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still open from a failed assertion - not worth failing the suite over.
        }
    }

    private string WriteSource(string name, string text)
    {
        var path = Path.Combine(_root, "src", name);
        File.WriteAllText(path, text);
        return path;
    }

    private TeamMutationRunner Runner(
        Func<CancellationToken, Task<MutantOutcome>> executor, IReadOnlyList<string>? testProjects = null) =>
        new(
            new MutationRunnerContext(_root, _root, testProjects ?? [Path.Combine(_root, "C.Tests.csproj")]),
            useStryker: false,
            mutantExecutor: executor);

    [Fact]
    public async Task RunAsync_WritesEachMutantAndThenRestoresTheOriginal()
    {
        var path = WriteSource("C.cs", Original);
        var seen = new List<string>();
        var runner = Runner(_ =>
        {
            seen.Add(File.ReadAllText(path));
            return Task.FromResult(MutantOutcome.Killed);
        });

        var (isError, text) = await runner.RunAsync("src/C.cs", maxMutants: 2, changedLines: [new LineRange(1, 1)]);

        isError.ShouldBeFalse();
        seen.Count.ShouldBe(2);
        seen.ShouldAllBe(content => content != Original);
        File.ReadAllText(path).ShouldBe(Original);
        text.ShouldContain("2 killed");
        text.ShouldContain("restored");
    }

    [Fact]
    public async Task RunAsync_CountsSurvivorsAndReportsTheScore()
    {
        WriteSource("C.cs", Original);
        var runner = Runner(_ => Task.FromResult(MutantOutcome.Survived));

        var (isError, text) = await runner.RunAsync("src/C.cs", maxMutants: 2, changedLines: [new LineRange(1, 1)]);

        isError.ShouldBeFalse();
        text.ShouldContain("0 killed, 2 survived");
        text.ShouldContain("Mutation score 0/2 (0%)");
        text.ShouldContain("Survived mutants are gaps");
    }

    [Fact]
    public async Task RunAsync_OnlyOffersMutantsInsideTheChangedLines()
    {
        var source = """
            class C
            {
                bool A(int a, int b) => a < b;
                bool B(int a, int b) => a > b;
            }
            """;
        WriteSource("C.cs", source);
        var seen = new List<string>();
        var runner = Runner(_ =>
        {
            seen.Add(File.ReadAllText(Path.Combine(_root, "src", "C.cs")));
            return Task.FromResult(MutantOutcome.Survived);
        });

        // Line 4 is the 'a > b' method: only its two mutants (flip and boundary) are in scope.
        await runner.RunAsync("src/C.cs", maxMutants: 12, changedLines: [new LineRange(4, 4)]);

        seen.Count.ShouldBe(2);
        seen.ShouldAllBe(content => content != source, "each mutant must differ from the original");
        seen.ShouldAllBe(
            content => content.Contains("bool A(int a, int b) => a < b;"),
            "the method outside the changed lines must never be touched");
    }

    [Fact]
    public async Task RunAsync_WhenTheMutantExecutorThrows_RecordsABuildFailureAndKeepsGoing()
    {
        WriteSource("C.cs", Original);
        var calls = 0;
        var runner = Runner(_ =>
        {
            calls++;
            return calls == 1
                ? throw new InvalidOperationException("the build service was busy")
                : Task.FromResult(MutantOutcome.Killed);
        });

        var (isError, text) = await runner.RunAsync("src/C.cs", maxMutants: 2, changedLines: [new LineRange(1, 1)]);

        isError.ShouldBeFalse();
        text.ShouldContain("build-fail");
        text.ShouldContain("the build service was busy");
        text.ShouldContain("1 killed");
        File.ReadAllText(Path.Combine(_root, "src", "C.cs")).ShouldBe(Original);
    }

    [Fact]
    public async Task RunAsync_OnAFileWithNoMutableCode_SaysSo()
    {
        WriteSource("C.cs", "class C { }");
        var runner = Runner(_ => Task.FromResult(MutantOutcome.Killed));

        var (isError, text) = await runner.RunAsync("src/C.cs");

        isError.ShouldBeFalse();
        text.ShouldContain("no mutable constructs");
    }

    [Fact]
    public async Task RunAsync_OnAMissingFile_IsAnError()
    {
        var runner = Runner(_ => Task.FromResult(MutantOutcome.Killed));

        var (isError, text) = await runner.RunAsync("src/Missing.cs");

        isError.ShouldBeTrue();
        text.ShouldContain("No such file");
    }

    [Fact]
    public async Task RunAsync_OutsideTheSolution_IsAnError()
    {
        var runner = Runner(_ => Task.FromResult(MutantOutcome.Killed));

        var (isError, text) = await runner.RunAsync("../outside.cs");

        isError.ShouldBeTrue();
        text.ShouldContain("No such file");
    }

    [Fact]
    public async Task RunAsync_WithNoTestProjects_IsAnError()
    {
        WriteSource("C.cs", Original);
        var runner = Runner(_ => Task.FromResult(MutantOutcome.Killed), testProjects: []);

        var (isError, text) = await runner.RunAsync("src/C.cs");

        isError.ShouldBeTrue();
        text.ShouldContain("No test projects");
    }

    // ---- Classify: what a test run really says about a mutant ----

    private static TestResult Result(TestOutcome outcome) =>
        new("Ns.C.M", outcome, TimeSpan.Zero, outcome == TestOutcome.Failed ? "boom" : null, null);

    [Fact]
    public void Classify_AFailedTest_KillsTheMutant()
    {
        TeamMutationRunner.Classify([Result(TestOutcome.Passed), Result(TestOutcome.Failed)], timedOut: false)
            .ShouldBe(MutantOutcome.Killed);
    }

    [Fact]
    public void Classify_AllTestsPassed_LeavesTheMutantAlive()
    {
        TeamMutationRunner.Classify(
            [Result(TestOutcome.Passed), Result(TestOutcome.Skipped)], timedOut: false)
            .ShouldBe(MutantOutcome.Survived);
    }

    [Fact]
    public void Classify_AnEmptyResultList_IsNoTestResults_NotSurvived()
    {
        // What TestRunService produces when the filter matched nothing, the host crashed, or the run
        // was killed on its timeout: [] - which used to be counted as a survivor.
        TeamMutationRunner.Classify([], timedOut: false).ShouldBe(MutantOutcome.NoTestResults);
    }

    [Fact]
    public void Classify_AFinishedTimeout_IsTimedOut()
    {
        // The linked token fired: dotnet test was killed and its .trx never written, so whatever the
        // results hold says nothing about the mutant.
        TeamMutationRunner.Classify([], timedOut: true).ShouldBe(MutantOutcome.TimedOut);
        TeamMutationRunner.Classify([Result(TestOutcome.Passed)], timedOut: true).ShouldBe(MutantOutcome.TimedOut);
    }

    [Fact]
    public async Task RunAsync_ReportsNoResultsSeparatelyAndLeavesThemOutOfTheScore()
    {
        WriteSource("C.cs", Original);
        var outcomes = new Queue<MutantOutcome>([MutantOutcome.Killed, MutantOutcome.NoTestResults]);
        var runner = Runner(_ => Task.FromResult(outcomes.Dequeue()));

        var (isError, text) = await runner.RunAsync("src/C.cs", maxMutants: 2, changedLines: [new LineRange(1, 1)]);

        isError.ShouldBeFalse();
        text.ShouldContain("1 ran no tests");
        text.ShouldContain("Mutation score 1/1 (100%)");
        text.ShouldContain("no-results");
        text.ShouldContain("neither killed nor survived");
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_StopsAndRestoresTheFile()
    {
        WriteSource("C.cs", Original);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var runner = Runner(_ =>
        {
            calls++;
            cancellation.Cancel();
            return Task.FromResult(MutantOutcome.Killed);
        });

        var (isError, text) = await runner.RunAsync(
            "src/C.cs", maxMutants: 5, changedLines: [new LineRange(1, 1)], cancellationToken: cancellation.Token);

        isError.ShouldBeFalse();
        calls.ShouldBe(1);
        text.ShouldContain("1 killed");
        File.ReadAllText(Path.Combine(_root, "src", "C.cs")).ShouldBe(Original);
    }
}
