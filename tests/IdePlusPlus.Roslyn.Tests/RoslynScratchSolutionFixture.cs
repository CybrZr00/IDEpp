using System.Diagnostics;
using IdePlusPlus.Roslyn;
using Microsoft.Build.Locator;
using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// Builds one real, on-disk, restored scratch solution (via the actual <c>dotnet new</c>/
/// <c>dotnet sln</c> CLI, not a hand-authored file) and loads it through a real
/// <see cref="RoslynWorkspaceService"/> - once per test class, since generating and restoring a
/// project is the expensive part and both real tests in this class only need to read/overlay text
/// on top of the same already-loaded solution, not reload it.
///
/// This is deliberately NOT a mock or an in-memory AdhocWorkspace: the single biggest risk on this
/// feature is Microsoft.CodeAnalysis.* vs MSBuildLocator-resolved-MSBuild version skew, which only
/// ever surfaces via a real MSBuildWorkspace.OpenSolutionAsync call against a real solution on disk.
/// </summary>
public sealed class RoslynScratchSolutionFixture : IAsyncLifetime
{
    private static bool _msBuildRegistered;

    public string Directory { get; }
    public string ClassFilePath { get; }
    public RoslynWorkspaceService WorkspaceService { get; private set; } = null!;

    public RoslynScratchSolutionFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), $"ideplusplus-roslyn-scratch-{Guid.NewGuid():N}");
        ClassFilePath = Path.Combine(Directory, "Class1.cs");
    }

    public async Task InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(Directory);

        // Real CLI calls, not a hand-authored .sln/.csproj: this exercises the exact same
        // "restore, then build a Roslyn view of what's on disk" pipeline a real user's project
        // goes through, including the implicit FrameworkReference resolution that a hand-rolled
        // project file could accidentally sidestep and hide a real bug. On this SDK, `dotnet new
        // sln` produces a `.slnx` (not classic `.sln`) - which conveniently exercises the very same
        // .slnx-via-Microsoft.VisualStudio.SolutionPersistence path the real IdePlusPlus.slnx uses.
        await RunDotnetAsync(Directory, "new classlib -n ScratchLib -o . --force");
        await RunDotnetAsync(Directory, "new sln -n Scratch -o . --force");
        await RunDotnetAsync(Directory, "sln Scratch.slnx add ScratchLib.csproj");
        await RunDotnetAsync(Directory, "restore Scratch.slnx");

        // Guard exactly like SolutionLoader.EnsureMSBuildRegistered: MSBuildLocator.RegisterDefaults
        // must run before any Microsoft.Build.* type is JIT'd, and (empirically confirmed by this
        // test actually passing) throws if called a second time in the same process, so a static
        // bool guard is required even though this test project's process only ever loads one
        // solution.
        if (!_msBuildRegistered)
        {
            MSBuildLocator.RegisterDefaults();
            _msBuildRegistered = true;
        }

        WorkspaceService = new RoslynWorkspaceService();
        var solutionPath = Path.Combine(Directory, "Scratch.slnx");
        await WorkspaceService.LoadSolutionAsync(solutionPath);
    }

    public Task DisposeAsync()
    {
        WorkspaceService?.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - shouldn't fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above.
        }

        return Task.CompletedTask;
    }

    private static async Task RunDotnetAsync(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start 'dotnet {arguments}'.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet {arguments}' exited with code {process.ExitCode} in '{workingDirectory}'.\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}");
        }
    }
}
