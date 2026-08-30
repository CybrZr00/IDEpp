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

    /// <summary>
    /// The Test Explorer button used to be the only caller, so the window between the IsRunning
    /// check and _process being assigned never mattered. It does once the copilot's run_tests tool
    /// can start a run too - two `dotnet test` processes over the same bin/obj is the same race
    /// BuildService had.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhileAnotherRunIsInFlight_Throws()
    {
        var runTask = _runService.RunAsync(
            _project.CsprojPath,
            filterFullyQualifiedNames: ["ScratchRunTests.SampleTests.SlowTest"],
            onOutputLine: _ => { });

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => _runService.RunAsync(
                _project.CsprojPath,
                filterFullyQualifiedNames: ["ScratchRunTests.SampleTests.PassingTest"],
                onOutputLine: _ => { }));
        }
        finally
        {
            _runService.Cancel();
            await runTask;
        }
    }

    /// <summary>
    /// WaitForExitAsync only stops awaiting, so a cancelled token used to leave `dotnet test`
    /// running - callers had to know to call Cancel() themselves, which a timeout path is exactly
    /// the sort of place to forget. Same wall-clock proof as the Cancel() test: SlowTest sleeps for
    /// eight seconds, so finishing well inside that means the process really was killed.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTheTokenIsCancelled_KillsTheProcessRatherThanLeavingItRunning()
    {
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();

        var runTask = _runService.RunAsync(
            _project.CsprojPath,
            filterFullyQualifiedNames: ["ScratchRunTests.SampleTests.SlowTest"],
            onOutputLine: _ => { },
            ct: cancellation.Token);

        await Task.Delay(TimeSpan.FromSeconds(2));
        _runService.IsRunning.ShouldBeTrue();

        await cancellation.CancelAsync();

        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBe(runTask, "cancelling the token should have terminated the process.");

        stopwatch.Stop();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(7));
        _runService.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_WithAnAlreadyCancelledToken_DoesNotStartAnything()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _runService.RunAsync(
            _project.CsprojPath, filterFullyQualifiedNames: null, onOutputLine: _ => { }, ct: cancellation.Token));

        _runService.IsRunning.ShouldBeFalse();
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

    /// <summary>
    /// Cancel()'s tree kill walks parent PIDs, so a grandchild whose own parent has already exited
    /// is orphaned and outlives it - measured under a real `npm run watch`, and reachable here
    /// through any test that leaves a process behind. This spawns exactly that shape from inside a
    /// test and waits for the launcher to exit before cancelling, so it pins the orphan case rather
    /// than an ordinary child of `dotnet test`.
    /// </summary>
    [Fact]
    public async Task Cancel_KillsAGrandchildATestOrphaned()
    {
        using var orphan = new OrphanedSleeper();
        using var project = new ScratchXunitProject("OrphanRunFixture", "OrphanTests.cs", OrphanSourceCode(orphan.LauncherScriptPath));
        project.Build();

        var runTask = _runService.RunAsync(project.CsprojPath, filterFullyQualifiedNames: null, onOutputLine: _ => { });

        var sleeperPid = orphan.WaitForSleeperPid();
        var launcherPid = orphan.WaitForLauncherPid();
        OrphanedSleeper.WaitUntilGone(launcherPid)
            .ShouldBeTrue("the launcher has to exit for the sleeper to be orphaned at all");
        OrphanedSleeper.IsAlive(sleeperPid).ShouldBeTrue("the orphan should still be running before the cancel");

        _runService.Cancel();
        await runTask;

        OrphanedSleeper.WaitUntilGone(sleeperPid)
            .ShouldBeTrue("Cancel() should have taken the orphaned grandchild with it");
        _runService.IsRunning.ShouldBeFalse();
    }

    /// <summary>A test that starts a launcher, waits for it to exit - leaving its child orphaned -
    /// and then stalls for long enough that the run has to be cancelled rather than finishing.</summary>
    private static string OrphanSourceCode(string launcherScriptPath) => $$"""
        using System;
        using System.Diagnostics;
        using System.Threading;
        using Xunit;

        namespace ScratchOrphanTests;

        public class OrphanTests
        {
            [Fact]
            public void SpawnsAnOrphanThenStalls()
            {
                var startInfo = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-File");
                startInfo.ArgumentList.Add(@"{{launcherScriptPath}}");

                using var launcher = Process.Start(startInfo)!;
                launcher.WaitForExit();

                Thread.Sleep(TimeSpan.FromSeconds(60));
            }
        }
        """;
}
