using System.Diagnostics;
using Shouldly;
using Xunit;

namespace IdePlusPlus.NuGet.Tests;

/// <summary>
/// Exercises NuGetService against a real, scratch classlib project rather than mocking dotnet.exe -
/// mirrors GitServiceTests's philosophy of testing the actual tool's behavior. Note: AddPackageAsync
/// and RestoreAsync tests hit the real nuget.org feed (resolving/downloading a real, small package),
/// the same way the rest of this repo's own build already implicitly depends on network access for
/// NuGet restore.
/// </summary>
public sealed class NuGetServiceTests : IDisposable
{
    private const string WellKnownPackageId = "Newtonsoft.Json";
    private const string WellKnownPackageVersion = "13.0.1";

    private readonly string _projectDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-nuget-{Guid.NewGuid():N}");
    private readonly string _csprojPath;
    private readonly NuGetService _nuGetService = new();

    public NuGetServiceTests()
    {
        Directory.CreateDirectory(_projectDirectory);
        RunDotNet(_projectDirectory, "new", "classlib", "-n", "ScratchProject", "-o", ".");
        _csprojPath = Path.Combine(_projectDirectory, "ScratchProject.csproj");
        File.Exists(_csprojPath).ShouldBeTrue("dotnet new classlib should have produced a .csproj to test against.");
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_projectDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_projectDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a lingering file handle (e.g. from MSBuild) shouldn't fail the test run.
        }
    }

    [Fact]
    public async Task ListPackagesAsync_OnAFreshProjectWithNoPackageReferences_ReturnsEmptyList()
    {
        var packages = await _nuGetService.ListPackagesAsync(_csprojPath);

        packages.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListPackagesAsync_ForACsprojPathThatDoesNotExist_ReturnsEmptyListRatherThanThrowing()
    {
        var missingCsproj = Path.Combine(_projectDirectory, "DoesNotExist.csproj");

        var packages = await _nuGetService.ListPackagesAsync(missingCsproj);

        packages.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddPackageAsync_ThenListPackagesAsync_ReturnsTheInstalledPackageAsATopLevelReference()
    {
        var (success, output) = await _nuGetService.AddPackageAsync(_csprojPath, WellKnownPackageId, version: null, onOutputLine: _ => { });

        success.ShouldBeTrue(output);

        var packages = await _nuGetService.ListPackagesAsync(_csprojPath);

        var package = packages.ShouldHaveSingleItem();
        package.Id.ShouldBe(WellKnownPackageId);
        package.ResolvedVersion.ShouldNotBeNullOrWhiteSpace();
        package.IsTransitive.ShouldBeFalse();
    }

    [Fact]
    public async Task AddPackageAsync_WithAnExplicitVersion_ResolvesToExactlyThatVersion()
    {
        var (success, output) = await _nuGetService.AddPackageAsync(_csprojPath, WellKnownPackageId, WellKnownPackageVersion, onOutputLine: _ => { });

        success.ShouldBeTrue(output);

        var packages = await _nuGetService.ListPackagesAsync(_csprojPath);

        var package = packages.ShouldHaveSingleItem();
        package.RequestedVersion.ShouldBe(WellKnownPackageVersion);
        package.ResolvedVersion.ShouldBe(WellKnownPackageVersion);
    }

    [Fact]
    public async Task AddPackageAsync_StreamsOutputLinesAsTheyArriveRatherThanOnlyAtTheEnd()
    {
        var lines = new List<string>();

        var (success, output) = await _nuGetService.AddPackageAsync(_csprojPath, WellKnownPackageId, version: null, onOutputLine: lines.Add);

        success.ShouldBeTrue(output);
        lines.ShouldNotBeEmpty();
        // The combined buffered Output should be exactly the streamed lines joined back together.
        output.ShouldBe(string.Join("\n", lines));
    }

    [Fact]
    public async Task AddPackageAsync_WithANonexistentPackageId_FailsWithoutThrowing()
    {
        var (success, output) = await _nuGetService.AddPackageAsync(
            _csprojPath, "ThisPackageDefinitelyDoesNotExist.Ideplusplus.Test", version: null, onOutputLine: _ => { });

        success.ShouldBeFalse();
        output.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RemovePackageAsync_AfterAdding_RemovesItFromTheInstalledList()
    {
        await _nuGetService.AddPackageAsync(_csprojPath, WellKnownPackageId, version: null, onOutputLine: _ => { });
        (await _nuGetService.ListPackagesAsync(_csprojPath)).ShouldHaveSingleItem();

        var (success, output) = await _nuGetService.RemovePackageAsync(_csprojPath, WellKnownPackageId);

        success.ShouldBeTrue(output);
        var packages = await _nuGetService.ListPackagesAsync(_csprojPath);
        packages.ShouldBeEmpty();
    }

    [Fact]
    public async Task RestoreAsync_OnAValidProject_Succeeds()
    {
        var lines = new List<string>();

        var (success, output) = await _nuGetService.RestoreAsync(_csprojPath, onOutputLine: lines.Add);

        success.ShouldBeTrue(output);
    }

    [Fact]
    public async Task RestoreAsync_AfterAddingAPackage_StillSucceeds()
    {
        await _nuGetService.AddPackageAsync(_csprojPath, WellKnownPackageId, version: null, onOutputLine: _ => { });

        var (success, output) = await _nuGetService.RestoreAsync(_csprojPath, onOutputLine: _ => { });

        success.ShouldBeTrue(output);
    }

    private static void RunDotNet(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}.\n{stdOut}\n{stdErr}");
        }
    }
}
