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

    [Fact]
    public async Task BuildAsync_WhenCancelled_ThrowsAndReleasesTheGuard()
    {
        using var cancellation = new CancellationTokenSource();
        var build = _buildService.BuildAsync(
            _projectPath, _ => cancellation.Cancel(), cancellationToken: cancellation.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => build);

        _buildService.IsRunning.ShouldBeFalse();
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
}
