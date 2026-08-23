using IdePlusPlus.ProjectSystem;
using Shouldly;
using Xunit;

namespace IdePlusPlus.ProjectSystem.Tests;

public sealed class StartupProjectsStoreTests : IDisposable
{
    private readonly string _solutionDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-startupprojects-{Guid.NewGuid()}");

    public StartupProjectsStoreTests() => Directory.CreateDirectory(_solutionDirectory);

    public void Dispose() => Directory.Delete(_solutionDirectory, recursive: true);

    [Fact]
    public void SaveThenLoad_RoundTripsTheProjectList()
    {
        var projectPaths = new[] { @"C:\solutions\MySolution\Api\Api.csproj", @"C:\solutions\MySolution\Worker\Worker.csproj" };

        StartupProjectsStore.Save(_solutionDirectory, projectPaths);
        var loaded = StartupProjectsStore.Load(_solutionDirectory);

        loaded.ShouldBe(projectPaths);
    }

    [Fact]
    public void Load_NoFileAtAllExists_ReturnsEmpty()
    {
        StartupProjectsStore.Load(_solutionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void Load_OnlyOldSingleProjectFileExists_ReturnsSingleElementListFromIt()
    {
        StartupProjectStore.Save(_solutionDirectory, @"C:\solutions\MySolution\Api\Api.csproj");

        var loaded = StartupProjectsStore.Load(_solutionDirectory);

        loaded.ShouldBe([@"C:\solutions\MySolution\Api\Api.csproj"]);
    }

    [Fact]
    public void Load_OnlyOldSingleProjectFileExists_DoesNotDeleteTheOldFile()
    {
        StartupProjectStore.Save(_solutionDirectory, @"C:\solutions\MySolution\Api\Api.csproj");

        StartupProjectsStore.Load(_solutionDirectory);

        StartupProjectStore.Load(_solutionDirectory).ShouldBe(@"C:\solutions\MySolution\Api\Api.csproj");
    }
}
