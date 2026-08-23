using System.Diagnostics;
using Shouldly;
using Xunit;

namespace IdePlusPlus.TestRunner.Tests;

/// <summary>
/// Exercises TestRunService against a real scratch xunit project via the actual `dotnet test` CLI,
/// including a real, wall-clock-bounded assertion that Cancel() actually kills the in-flight
/// process tree rather than merely giving up on awaiting it.
/// </summary>
public sealed class TestRunServiceTests : IDisposable
{
    private const string SourceCode = """
        using System;
        using System.Threading;
        using Xunit;

        namespace ScratchRunTests;

        public class SampleTests
        {
            [Fact]
            public void PassingTest()
            {
                Assert.True(true);
            }

            [Fact]
            public void FailingTest()
            {
                Assert.Fail("boom");
            }

            [Fact]
            public void SlowTest()
            {
                Thread.Sleep(TimeSpan.FromSeconds(8));
            }
        }
        """;

    private readonly ScratchXunitProject _project = new("RunServiceFixture", "SampleTests.cs", SourceCode);
    private readonly TestRunService _runService = new();

    public TestRunServiceTests()
    {
        // Pre-build so the timing-sensitive Cancel test below only pays for `dotnet test`'s
        // incremental up-to-date check, not a from-scratch restore+compile.
        _project.Build();
    }

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task RunAsync_OnAProjectWithAPassingAndAFailingTest_ReturnsBothOutcomes()
    {
        // Filtered to exclude the slow test so this fact isn't held up by it.
        var results = await _runService.RunAsync(
            _project.CsprojPath,
            filterFullyQualifiedNames: ["ScratchRunTests.SampleTests.PassingTest", "ScratchRunTests.SampleTests.FailingTest"],
            onOutputLine: _ => { });

        results.Count.ShouldBe(2);
        results.ShouldContain(r => r.FullyQualifiedName == "ScratchRunTests.SampleTests.PassingTest" && r.Outcome == TestOutcome.Passed);
        results.ShouldContain(r => r.FullyQualifiedName == "ScratchRunTests.SampleTests.FailingTest" && r.Outcome == TestOutcome.Failed);
    }

    [Fact]
    public async Task RunAsync_WithAFilterForOneSpecificTest_RunsAndReturnsOnlyThatTest()
    {
        var results = await _runService.RunAsync(
            _project.CsprojPath,
            filterFullyQualifiedNames: ["ScratchRunTests.SampleTests.PassingTest"],
            onOutputLine: _ => { });

        var result = results.ShouldHaveSingleItem();
        result.FullyQualifiedName.ShouldBe("ScratchRunTests.SampleTests.PassingTest");
        result.Outcome.ShouldBe(TestOutcome.Passed);
    }

    [Fact]
    public async Task Cancel_DuringAnInFlightRun_TerminatesTheProcessWellBeforeTheSlowTestWouldFinishNaturally()
    {
        var stopwatch = Stopwatch.StartNew();

        var runTask = _runService.RunAsync(
            _project.CsprojPath,
            filterFullyQualifiedNames: ["ScratchRunTests.SampleTests.SlowTest"],
            onOutputLine: _ => { });

        // Give the process a moment to actually start (dotnet host + testhost startup) before
        // cancelling, so this proves Cancel() interrupts a run that is genuinely in flight rather
        // than one that hadn't started yet.
        await Task.Delay(TimeSpan.FromSeconds(2));
        _runService.IsRunning.ShouldBeTrue();

        _runService.Cancel();

        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBe(runTask, "Cancel() should have terminated the process well within 5 seconds of being called.");

        stopwatch.Stop();
        // SlowTest sleeps for 8 seconds - a real cancellation finishes in a small fraction of
        // that. This is the wall-clock proof that the process was actually killed rather than
        // left running to finish naturally.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(7));

        _runService.IsRunning.ShouldBeFalse();
    }
}
