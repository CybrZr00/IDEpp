using System.Security;
using IdePlusPlus.Build;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Build.Tests;

/// <summary>
/// Runs a real `dotnet build` against a scratch project, for the same reason GitServiceTests shells
/// out to real git: the behaviour worth pinning here is process lifetime, and a mocked process has
/// none.
/// </summary>
public sealed class BuildServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"ideplusplus-build-{Guid.NewGuid()}");

    private readonly string _projectPath;
    private readonly BuildService _buildService = new();

    public BuildServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _projectPath = Path.Combine(_directory, "Scratch.csproj");

        // Standalone: no Directory.Build.props above a temp directory, so nothing from the repo
        // leaks into what this builds.
        File.WriteAllText(_projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_directory, "Class1.cs"), "public class Class1 { }");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked build holding obj/ would land here - not worth failing an unrelated test over.
        }
    }

    [Fact]
    public void IsRunning_BeforeAnythingStarts_IsFalse()
    {
        _buildService.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task BuildAsync_OnAValidProject_SucceedsAndClearsIsRunning()
    {
        var exitCode = await _buildService.BuildAsync(_projectPath, _ => { });

        exitCode.ShouldBe(0);
        _buildService.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task BuildAsync_ReportsCompilerErrorsOnTheOutputCallback()
    {
        File.WriteAllText(Path.Combine(_directory, "Class1.cs"), "public class Class1 { this is not C# }");
        var lines = new List<string>();

        var exitCode = await _buildService.BuildAsync(_projectPath, lines.Add);

        exitCode.ShouldNotBe(0);
        lines.Any(l => BuildDiagnosticParser.TryParse(l) is { Severity: BuildDiagnosticSeverity.Error })
            .ShouldBeTrue("expected at least one parseable error diagnostic");
    }

    /// <summary>
    /// Two dotnet builds over the same obj/bin is not a race worth having, and it became reachable
    /// the moment something other than the toolbar button could start one.
    /// </summary>
    [Fact]
    public async Task BuildAsync_WhileAnotherBuildIsRunning_Throws()
    {
        var first = _buildService.BuildAsync(_projectPath, _ => { });

        await Should.ThrowAsync<InvalidOperationException>(
            () => _buildService.BuildAsync(_projectPath, _ => { }));

        await first;
    }

    /// <summary>
    /// A build that finished but never returned: dotnet had exited, and a process it started (an
    /// MSBuild worker node) still held the redirected output pipes. Waiting for those to close waited
    /// forever - it hung the copilot's build_solution on a solution with a compile error. cmd's
    /// `start /b` reproduces it without depending on when MSBuild happens to start a node: cmd exits
    /// at once, and the ping it started inherits the pipes and keeps them for half a minute.
    /// </summary>
    [Fact]
    public async Task AProcessThatExits_WhileSomethingItStartedHoldsItsOutput_StillReturnsPromptly()
    {
        var lines = new List<string>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var exitCode = await BuildService.RunProcessAsync(
            // No quotes inside: ArgumentList would escape them as \", and cmd's parsing of that is not
            // something the test should depend on.
            ["/c", "start /b ping -n 30 127.0.0.1 >NUL & echo parent done & exit /b 3"],
            _directory,
            line => { lock (lines) { lines.Add(line); } },
            CancellationToken.None,
            fileName: "cmd.exe");

        stopwatch.Stop();
        exitCode.ShouldBe(3);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "it waited for the child's pipe instead of for the process");
        lock (lines)
        {
            lines.ShouldContain(line => line.Contains("parent done"), "output written before the exit must still be delivered");
        }
    }

    [Fact]
    public async Task BuildAsync_WhenCancelled_ThrowsAndReleasesTheGuard()
    {
        using var cancellation = new CancellationTokenSource();
        var build = _buildService.BuildAsync(
            _projectPath, _ => cancellation.Cancel(), cancellationToken: cancellation.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => build);

        _buildService.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task RunDotnetAsync_PassesItsArgumentsThroughVerbatim()
    {
        var lines = new List<string>();

        var exitCode = await _buildService.RunDotnetAsync(["build", _projectPath, "--verbosity", "quiet"], _directory, lines.Add);

        exitCode.ShouldBe(0);
        _buildService.IsRunning.ShouldBeFalse();
    }

    /// <summary>
    /// The whole reason RunDotnetAsync lives here rather than in a PublishService of its own: a
    /// `dotnet publish` writes the same obj/bin a concurrent build would, so the two have to exclude
    /// each other through one guard rather than two that need to agree.
    /// </summary>
    [Fact]
    public async Task RunDotnetAsync_WhileABuildIsRunning_Throws()
    {
        var build = _buildService.BuildAsync(_projectPath, _ => { });

        await Should.ThrowAsync<InvalidOperationException>(
            () => _buildService.RunDotnetAsync(["publish", _projectPath], _directory, _ => { }));

        await build;
    }

    [Fact]
    public async Task BuildAsync_WhileRunDotnetAsyncIsRunning_Throws()
    {
        var publish = _buildService.RunDotnetAsync(["publish", _projectPath], _directory, _ => { });

        await Should.ThrowAsync<InvalidOperationException>(() => _buildService.BuildAsync(_projectPath, _ => { }));

        await publish;
    }

    /// <summary>
    /// The reason cancellation now kills the process tree: `using var process` disposes the handle
    /// without touching the process, so a cancelled build used to keep running with MSBuild worker
    /// nodes still holding obj/bin. A build that starts cleanly straight afterwards is the
    /// observable form of "the old one really is gone".
    /// </summary>
    [Fact]
    public async Task BuildAsync_AfterACancelledBuild_CanBuildAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelled = _buildService.BuildAsync(
            _projectPath, _ => cancellation.Cancel(), cancellationToken: cancellation.Token);
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);

        var exitCode = await _buildService.BuildAsync(_projectPath, _ => { });

        exitCode.ShouldBe(0);
    }

    /// <summary>
    /// Cancelling used to kill only what Kill(entireProcessTree: true) could see, and its walk is
    /// over parent PIDs - so a grandchild whose own parent has already exited is orphaned and
    /// survives. A build target that spawns exactly that and then stalls is the reachable version:
    /// `dotnet build` running an Exec that leaves something behind is the same shape as `npm run
    /// watch`, which is where this was first measured.
    /// </summary>
    [Fact]
    public async Task BuildAsync_WhenCancelled_KillsAGrandchildTheBuildOrphaned()
    {
        using var orphan = new OrphanedSleeper();

        // Its own directory rather than a second project under _directory: the inner project's
        // obj/ would be picked up by the outer project's default source glob and break the other
        // tests in this class.
        var stallDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-build-stall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stallDirectory);

        try
        {
            var stallProjectPath = Path.Combine(stallDirectory, "Stall.csproj");
            File.WriteAllText(stallProjectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Target Name="SpawnAnOrphanThenStall" BeforeTargets="Build">
                    <Exec Command="{SecurityElement.Escape(orphan.LauncherCommandLine)}" />
                    <Exec Command="powershell.exe -NoProfile -NonInteractive -Command &quot;Start-Sleep -Seconds 60&quot;" />
                  </Target>
                </Project>
                """);

            using var cancellation = new CancellationTokenSource();
            var build = _buildService.BuildAsync(stallProjectPath, _ => { }, cancellationToken: cancellation.Token);

            var sleeperPid = orphan.WaitForSleeperPid();
            var launcherPid = orphan.WaitForLauncherPid();
            OrphanedSleeper.WaitUntilGone(launcherPid)
                .ShouldBeTrue("the launcher has to exit for the sleeper to be orphaned at all");
            OrphanedSleeper.IsAlive(sleeperPid).ShouldBeTrue("the orphan should still be running before the cancel");

            await cancellation.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => build);

            OrphanedSleeper.WaitUntilGone(sleeperPid)
                .ShouldBeTrue("cancelling the build should have taken the orphaned grandchild with it");
            _buildService.IsRunning.ShouldBeFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(stallDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort - a killed process may still be holding obj/.
            }
        }
    }
}
