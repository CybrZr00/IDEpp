using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace IdePlusPlus.ProjectSystem.Tests;

/// <summary>
/// Unloading a project: the list that is kept per solution, the tree built without evaluating the
/// unloaded project, and the solution filter every solution-wide build goes through instead.
/// </summary>
public sealed class UnloadedProjectsTests : IDisposable
{
    /// <summary>A project file no MSBuild can evaluate - the stand-in for a MAUI Android head on a
    /// machine without the workload.</summary>
    private const string UnbuildableProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Missing.Workload.targets\" /></Project>";

    private readonly TempSolutionFixture _solution = new("App", "Mobile");

    public void Dispose()
    {
        var filterDirectory = SolutionBuildTarget.FilterDirectoryFor(_solution.SolutionPath);
        if (Directory.Exists(filterDirectory))
        {
            Directory.Delete(filterDirectory, recursive: true);
        }

        _solution.Dispose();
    }

    // ---- The store ----

    [Fact]
    public void TheStore_RoundTripsTheListAndTheToggle()
    {
        UnloadedProjectsStore.Save(_solution.Directory, [@"Mobile\Mobile.csproj"], showUnloaded: false);

        var state = UnloadedProjectsStore.Load(_solution.Directory);

        state.Projects.ShouldBe([@"Mobile\Mobile.csproj"]);
        state.ShowUnloaded.ShouldBeFalse();
    }

    [Fact]
    public void TheStore_WithNoFile_HasNothingUnloaded_AndShowsUnloadedProjects()
    {
        UnloadedProjectsStore.Load(_solution.Directory).ShouldBe(UnloadedProjectsState.Empty);
    }

    [Fact]
    public void TheStore_WithACorruptFile_OpensWithNothingUnloaded_InsteadOfThrowing()
    {
        var path = UnloadedProjectsStore.FilePath(_solution.Directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"unloadedProjects\": [ \"Mobile");

        UnloadedProjectsStore.Load(_solution.Directory).ShouldBe(UnloadedProjectsState.Empty);
    }

    [Fact]
    public void TheStore_DropsPathsThatAreNotInsideTheSolution()
    {
        UnloadedProjectsStore.Save(
            _solution.Directory, [@"Mobile\Mobile.csproj", @"..\Elsewhere\Other.csproj", @"C:\Abs\Abs.csproj"], showUnloaded: true);

        UnloadedProjectsStore.Load(_solution.Directory).Projects.ShouldBe([@"Mobile\Mobile.csproj"]);
    }

    [Fact]
    public void Paths_MatchWhateverTheirSeparatorsAndCase()
    {
        var set = UnloadedProjectsStore.CreateSet(["mobile/MOBILE.csproj"]);

        set.Contains(@"Mobile\Mobile.csproj").ShouldBeTrue();
    }

    // ---- The loader ----

    [Fact]
    public void AnUnloadedProject_IsAPlaceholder_AndIsNeverEvaluated()
    {
        _solution.WriteProjectFile("Mobile", UnbuildableProject);

        var root = SolutionLoader.Load(_solution.SolutionPath, UnloadedProjectsStore.CreateSet([@"Mobile\Mobile.csproj"]));

        var mobile = root.Children.Single(n => n.Name == "Mobile");
        mobile.IsUnloaded.ShouldBeTrue();
        mobile.LoadError.ShouldBeNull("an unloaded project is not evaluated, so its broken import never comes up");
        mobile.Children.ShouldBeEmpty();
        mobile.DisplayName.ShouldBe("Mobile (unloaded)");
        root.Children.Single(n => n.Name == "App").Children.ShouldNotBeEmpty();
    }

    [Fact]
    public void AProjectThatFailsToLoad_IsShownAsFailed_AndTheRestOfTheSolutionStillOpens()
    {
        _solution.WriteProjectFile("Mobile", UnbuildableProject);

        var root = SolutionLoader.Load(_solution.SolutionPath);

        var mobile = root.Children.Single(n => n.Name == "Mobile");
        mobile.LoadError.ShouldNotBeNull();
        mobile.DisplayName.ShouldBe("Mobile (load failed)");
        root.Children.Single(n => n.Name == "App").LoadError.ShouldBeNull();
    }

    // ---- The build target ----

    [Fact]
    public void WithNothingUnloaded_TheBuildTarget_IsTheSolutionItself()
    {
        SolutionBuildTarget.Resolve(_solution.SolutionPath, []).ShouldBe(_solution.SolutionPath);
        File.Exists(SolutionBuildTarget.FilterPathFor(_solution.SolutionPath)).ShouldBeFalse();
    }

    [Fact]
    public void WithAProjectUnloaded_TheBuildTarget_IsAFilterOfTheOthers_KeptOutsideTheRepository()
    {
        var target = SolutionBuildTarget.Resolve(_solution.SolutionPath, [@"Mobile\Mobile.csproj"]);

        target.ShouldEndWith(".slnf");
        target.ShouldNotStartWith(_solution.Directory, Case.Insensitive);
        using var filter = JsonDocument.Parse(File.ReadAllText(target));
        var solution = filter.RootElement.GetProperty("solution");
        solution.GetProperty("path").GetString().ShouldBe(Path.GetFullPath(_solution.SolutionPath));
        solution.GetProperty("projects").EnumerateArray().Select(p => p.GetString()).ShouldBe([@"App\App.csproj"]);
    }

    [Fact]
    public void WithEveryProjectUnloaded_TheBuildTarget_FallsBackToTheSolution()
    {
        SolutionBuildTarget.Resolve(_solution.SolutionPath, [@"App\App.csproj", @"Mobile\Mobile.csproj"])
            .ShouldBe(_solution.SolutionPath);
    }

    /// <summary>
    /// The reason the feature exists, end to end with the real SDK: the solution does not build while
    /// it holds a project this machine cannot build, and its filter - which is what Build, the copilot's
    /// build_solution and every Ultra worktree now build - does.
    /// </summary>
    [Fact]
    public async Task TheFilter_BuildsWithDotnet_WhereTheWholeSolutionDoesNot()
    {
        _solution.WriteProjectFile("Mobile", UnbuildableProject);

        var (wholeExit, _) = await DotnetBuildAsync(_solution.SolutionPath);
        var filter = SolutionBuildTarget.Resolve(_solution.SolutionPath, [@"Mobile\Mobile.csproj"]);
        var (filterExit, filterOutput) = await DotnetBuildAsync(filter);

        wholeExit.ShouldNotBe(0);
        filterExit.ShouldBe(0, filterOutput);
    }

    private static async Task<(int ExitCode, string Output)> DotnetBuildAsync(string target)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(target)!,
        };
        // No worker node, compiler server or build server may outlive the build: each inherits the
        // redirected output handles, and the reads below would then wait for an end of stream that
        // never comes.
        foreach (var argument in new[] { "build", target, "-nologo", "-v:q", "-nodeReuse:false", "-p:UseSharedCompilation=false", "--disable-build-servers" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        // The in-process MSBuild registration's variables would point the child at this process's SDK
        // (see MSBuildLocatorEnvironment); a terminal's `dotnet build` resolves its own.
        foreach (var name in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
        {
            startInfo.Environment.Remove(name);
        }

        var output = new System.Text.StringBuilder();
        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet build {target} did not finish in 4 minutes:\n{output}");
        }

        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}
