using IdePlusPlus.Roslyn;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// What the workspace answers while the IDE is between solutions. Opening another solution restores
/// its packages before the workspace loads anything, and for that whole window - forever, if the new
/// load failed - find_symbol, find_references and get_diagnostics used to answer from the previous
/// solution, so the copilot kept "seeing" the solution the user had just closed.
///
/// <para>Each test loads the shared scratch solution into a workspace of its own, since these tests
/// deliberately leave it in a state the fixture's shared instance must never be in.</para>
/// </summary>
[Collection(nameof(RoslynScratchSolutionCollection))]
public sealed class SolutionSwitchIntegrationTests
{
    private readonly RoslynScratchSolutionFixture _fixture;

    public SolutionSwitchIntegrationTests(RoslynScratchSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    private string SolutionPath => Path.Combine(_fixture.Directory, "Scratch.slnx");

    private string OtherSolutionPath => Path.Combine(_fixture.Directory, "..", $"other-{Guid.NewGuid():N}", "Other.slnx");

    private async Task<RoslynWorkspaceService> LoadScratchSolutionAsync()
    {
        var service = new RoslynWorkspaceService();
        await service.LoadSolutionAsync(SolutionPath);
        service.IsSolutionLoaded.ShouldBeTrue();
        (await service.FindSymbolsAsync("Class1", 10)).ShouldNotBeEmpty();
        return service;
    }

    [Fact]
    public async Task AnnouncingAnotherSolution_StopsAnsweringFromTheOneStillLoaded()
    {
        using var service = await LoadScratchSolutionAsync();

        service.SetExpectedSolution(OtherSolutionPath);

        service.IsSolutionLoaded.ShouldBeFalse();
        (await service.FindSymbolsAsync("Class1", 10)).ShouldBeEmpty();
        (await service.FindReferencesAsync("Class1", 10)).Declarations.ShouldBeEmpty();
        service.IsAnalysed(_fixture.ClassFilePath).ShouldBeFalse();
        (await service.GetDiagnosticsAsync(_fixture.ClassFilePath, "class Broken { int x = ; }")).ShouldBeEmpty();
    }

    /// <summary>The same solution announced again (a reload of what is already open) is still the
    /// loaded one - only a different solution invalidates it.</summary>
    [Fact]
    public async Task AnnouncingTheLoadedSolutionAgain_KeepsAnswering()
    {
        using var service = await LoadScratchSolutionAsync();

        service.SetExpectedSolution(SolutionPath);

        service.IsSolutionLoaded.ShouldBeTrue();
        (await service.FindSymbolsAsync("Class1", 10)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ALoadThatFails_LeavesNothingLoadedRatherThanThePreviousSolution()
    {
        using var service = await LoadScratchSolutionAsync();

        await Should.ThrowAsync<Exception>(() => service.LoadSolutionAsync(OtherSolutionPath));

        service.IsSolutionLoaded.ShouldBeFalse();
        (await service.FindSymbolsAsync("Class1", 10)).ShouldBeEmpty();
        service.IsAnalysed(_fixture.ClassFilePath).ShouldBeFalse();
    }

    /// <summary>A load the IDE has already moved on from (its restore finished after the user opened
    /// something else) must not install itself over the newer choice.</summary>
    [Fact]
    public async Task ALoadOvertakenByAnotherSolution_IsNotInstalled()
    {
        using var service = new RoslynWorkspaceService();

        var load = service.LoadSolutionAsync(SolutionPath);
        service.SetExpectedSolution(OtherSolutionPath);
        await load;

        service.IsSolutionLoaded.ShouldBeFalse();
        (await service.FindSymbolsAsync("Class1", 10)).ShouldBeEmpty();

        // Nor does it count as loaded if the IDE later comes back to it without reloading.
        service.SetExpectedSolution(SolutionPath);
        service.IsSolutionLoaded.ShouldBeFalse();
    }
}
