using IdePlusPlus.Publish;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Publish.Tests;

public sealed class PublishProfileStoreTests : IDisposable
{
    private readonly string _projectDirectory;
    private readonly string _projectPath;

    public PublishProfileStoreTests()
    {
        _projectDirectory = Path.Combine(Path.GetTempPath(), "ideplusplus-publish-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectDirectory);
        _projectPath = Path.Combine(_projectDirectory, "Sample.csproj");
        File.WriteAllText(_projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    [Fact]
    public void Load_AProjectWithNoProfilesFolder_ReturnsEmpty() =>
        PublishProfileStore.Load(_projectPath).ShouldBeEmpty();

    [Fact]
    public void Save_ANewProfile_CreatesThePropertiesPublishProfilesFolder()
    {
        var path = PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault("win-x64"));

        path.ShouldBe(Path.Combine(_projectDirectory, "Properties", "PublishProfiles", "win-x64.pubxml"));
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void Load_AfterSaving_FindsTheProfileWithItsFilePathSet()
    {
        PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault("win-x64", "net10.0"));

        var profiles = PublishProfileStore.Load(_projectPath);

        profiles.Count.ShouldBe(1);
        profiles[0].Name.ShouldBe("win-x64");
        profiles[0].TargetFramework.ShouldBe("net10.0");
        profiles[0].FilePath.ShouldNotBeNull();
    }

    [Fact]
    public void Load_SeveralProfiles_ReturnsThemNameSorted()
    {
        PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault("zebra"));
        PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault("alpha"));

        PublishProfileStore.Load(_projectPath).Select(p => p.Name).ShouldBe(["alpha", "zebra"]);
    }

    [Fact]
    public void Load_AMalformedProfileAlongsideAGoodOne_SkipsOnlyTheMalformedOne()
    {
        PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault("good"));
        File.WriteAllText(
            Path.Combine(PublishProfileStore.ProfilesDirectory(_projectPath)!, "broken.pubxml"), "<Project>");

        PublishProfileStore.Load(_projectPath).Select(p => p.Name).ShouldBe(["good"]);
    }

    [Fact]
    public void Save_ARenamedProfile_MovesTheFileAndKeepsItsContent()
    {
        var originalPath = PublishProfileStore.Save(
            _projectPath, PublishProfile.CreateFolderDefault("old") with { RuntimeIdentifier = "win-x64" });
        var loaded = PublishProfileStore.Load(_projectPath).Single();

        PublishProfileStore.Save(_projectPath, loaded with { Name = "new" });

        File.Exists(originalPath).ShouldBeFalse();
        var profiles = PublishProfileStore.Load(_projectPath);
        profiles.Count.ShouldBe(1);
        profiles[0].Name.ShouldBe("new");
        profiles[0].RuntimeIdentifier.ShouldBe("win-x64");
    }

    [Fact]
    public void Delete_AnExistingProfile_RemovesIt()
    {
        PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault("win-x64"));

        PublishProfileStore.Delete(_projectPath, "win-x64");

        PublishProfileStore.Load(_projectPath).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\..\evil")]
    [InlineData("sub/profile")]
    [InlineData("bad:name")]
    [InlineData(" leading")]
    public void ValidateName_ANameThatIsNotAUsableFileName_IsRejected(string name) =>
        PublishProfileStore.ValidateName(name).ShouldNotBeNull();

    [Theory]
    [InlineData("win-x64")]
    [InlineData("FolderProfile")]
    [InlineData("staging.linux")]
    public void ValidateName_AnOrdinaryName_IsAccepted(string name) =>
        PublishProfileStore.ValidateName(name).ShouldBeNull();

    /// <summary>A profile name becomes a path segment; letting one escape the profiles folder would
    /// let a save overwrite an arbitrary file.</summary>
    [Fact]
    public void Save_ANameThatWouldEscapeTheProfilesFolder_Throws() =>
        Should.Throw<ArgumentException>(
            () => PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault(@"..\..\escaped")));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a leftover temp folder isn't worth failing a passing test over.
        }
    }
}
