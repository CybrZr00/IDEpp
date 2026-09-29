using IdePlusPlus.Publish;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Publish.Tests;

public sealed class PublishSelectionStoreTests : IDisposable
{
    private readonly string _solutionDirectory;

    public PublishSelectionStoreTests()
    {
        _solutionDirectory = Path.Combine(Path.GetTempPath(), "ideplusplus-publish-sel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_solutionDirectory);
    }

    [Fact]
    public void Load_WithNoStoredFile_ReturnsEmpty() =>
        PublishSelectionStore.Load(_solutionDirectory).ShouldBeEmpty();

    [Fact]
    public void Save_ThenLoad_RoundTripsTheSelection()
    {
        var projectPath = Path.Combine(_solutionDirectory, "src", "App", "App.csproj");
        var key = PublishSelectionStore.KeyFor(_solutionDirectory, projectPath);

        PublishSelectionStore.Save(_solutionDirectory, new Dictionary<string, string> { [key] = "win-x64" });

        PublishSelectionStore.Load(_solutionDirectory)[key].ShouldBe("win-x64");
    }

    [Fact]
    public void KeyFor_AProjectUnderTheSolution_IsARelativeForwardSlashPath() =>
        PublishSelectionStore.KeyFor(_solutionDirectory, Path.Combine(_solutionDirectory, "src", "App", "App.csproj"))
            .ShouldBe("src/App/App.csproj");

    [Fact]
    public void Load_AMalformedFile_DegradesToEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_solutionDirectory, ".ideplusplus"));
        File.WriteAllText(Path.Combine(_solutionDirectory, ".ideplusplus", "publish.json"), "{ not json");

        PublishSelectionStore.Load(_solutionDirectory).ShouldBeEmpty();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_solutionDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
