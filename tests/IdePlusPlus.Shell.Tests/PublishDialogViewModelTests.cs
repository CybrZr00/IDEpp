using IdePlusPlus.Build;
using IdePlusPlus.Publish;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// PublishDialogViewModel is deliberately free of WPF types so it can be covered here - this test
/// project only exercises view-model logic and never constructs an Application. Saving is exercised
/// against a real scratch project directory, since writing .pubxml files is most of what it does.
/// </summary>
public sealed class PublishDialogViewModelTests : IDisposable
{
    private readonly string _projectDirectory;
    private readonly string _projectPath;

    public PublishDialogViewModelTests()
    {
        _projectDirectory = Path.Combine(Path.GetTempPath(), "ideplusplus-publish-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectDirectory);
        _projectPath = Path.Combine(_projectDirectory, "Sample.csproj");
        File.WriteAllText(_projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    private PublishDialogViewModel CreateViewModel(params PublishProfile[] profiles) =>
        new(_projectPath, "net10.0", profiles, new PublishService(new BuildService()), _ => { });

    [Fact]
    public void NewProfile_AddsASelectedFolderProfileSeededWithTheProjectFramework()
    {
        var viewModel = CreateViewModel();

        viewModel.NewProfileCommand.Execute(null);

        viewModel.Profiles.Count.ShouldBe(1);
        viewModel.SelectedProfile.ShouldBe(viewModel.Profiles[0]);
        viewModel.SelectedProfile!.Kind.ShouldBe(PublishKind.Folder);
        viewModel.SelectedProfile.TargetFramework.ShouldBe("net10.0");
    }

    /// <summary>The constructor assigns backing fields directly, so a new profile has to be marked
    /// dirty explicitly or it would never be written.</summary>
    [Fact]
    public void NewProfile_IsUnsavedUntilSaved()
    {
        var viewModel = CreateViewModel();

        viewModel.NewProfileCommand.Execute(null);

        viewModel.HasUnsavedChanges.ShouldBeTrue();
    }

    [Fact]
    public void NewProfile_Twice_GeneratesDistinctNames()
    {
        var viewModel = CreateViewModel();

        viewModel.NewProfileCommand.Execute(null);
        viewModel.NewProfileCommand.Execute(null);

        viewModel.Profiles.Select(p => p.Name).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void DuplicateProfile_CopiesTheSettingsButNotTheFilePath()
    {
        var source = PublishProfile.CreateFolderDefault("win-x64", "net10.0") with
        {
            FilePath = Path.Combine(_projectDirectory, "Properties", "PublishProfiles", "win-x64.pubxml"),
            RuntimeIdentifier = "win-x64",
            PublishSingleFile = true,
        };
        var viewModel = CreateViewModel(source);

        viewModel.DuplicateProfileCommand.Execute(null);

        var copy = viewModel.SelectedProfile!;
        copy.Name.ShouldNotBe("win-x64");
        copy.RuntimeIdentifier.ShouldBe("win-x64");
        copy.PublishSingleFile.ShouldBeTrue();
        copy.FilePath.ShouldBeNull();
    }

    [Fact]
    public void DeleteProfile_RemovesItAndSelectsANeighbour()
    {
        var viewModel = CreateViewModel(
            PublishProfile.CreateFolderDefault("one"), PublishProfile.CreateFolderDefault("two"));

        viewModel.DeleteProfileCommand.Execute(null);

        viewModel.Profiles.Select(p => p.Name).ShouldBe(["two"]);
        viewModel.SelectedProfile!.Name.ShouldBe("two");
    }

    [Fact]
    public void EditingAField_MarksTheProfileUnsaved()
    {
        var viewModel = CreateViewModel(PublishProfile.CreateFolderDefault("win-x64"));

        viewModel.HasUnsavedChanges.ShouldBeFalse();
        viewModel.SelectedProfile!.RuntimeIdentifier = "linux-x64";

        viewModel.HasUnsavedChanges.ShouldBeTrue();
    }

    [Fact]
    public void SaveAll_WritesNewProfilesAndClearsTheUnsavedFlag()
    {
        var viewModel = CreateViewModel();
        viewModel.NewProfileCommand.Execute(null);
        viewModel.SelectedProfile!.Name = "staging";
        viewModel.SelectedProfile.RuntimeIdentifier = "linux-x64";

        viewModel.SaveAll().ShouldBeNull();

        viewModel.HasUnsavedChanges.ShouldBeFalse();
        var saved = PublishProfileStore.Load(_projectPath);
        saved.Count.ShouldBe(1);
        saved[0].Name.ShouldBe("staging");
        saved[0].RuntimeIdentifier.ShouldBe("linux-x64");
    }

    [Fact]
    public void SaveAll_AfterDeletingASavedProfile_RemovesItFromDisk()
    {
        PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault("win-x64"));
        var viewModel = CreateViewModel([.. PublishProfileStore.Load(_projectPath)]);

        viewModel.DeleteProfileCommand.Execute(null);
        viewModel.SaveAll().ShouldBeNull();

        PublishProfileStore.Load(_projectPath).ShouldBeEmpty();
    }

    [Fact]
    public void SaveAll_WithAnUnusableName_ReportsItAndWritesNothing()
    {
        var viewModel = CreateViewModel();
        viewModel.NewProfileCommand.Execute(null);
        viewModel.SelectedProfile!.Name = "bad/name";

        viewModel.SaveAll().ShouldNotBeNull();

        PublishProfileStore.Load(_projectPath).ShouldBeEmpty();
    }

    [Fact]
    public void SaveAll_WithTwoProfilesOfTheSameName_ReportsIt()
    {
        var viewModel = CreateViewModel();
        viewModel.NewProfileCommand.Execute(null);
        viewModel.SelectedProfile!.Name = "same";
        viewModel.NewProfileCommand.Execute(null);
        viewModel.SelectedProfile!.Name = "same";

        viewModel.SaveAll().ShouldNotBeNull();
    }

    [Fact]
    public void SaveAll_WithNoChanges_LeavesTheFileUntouched()
    {
        var path = PublishProfileStore.Save(_projectPath, PublishProfile.CreateFolderDefault("win-x64"));
        var before = File.GetLastWriteTimeUtc(path);
        var viewModel = CreateViewModel([.. PublishProfileStore.Load(_projectPath)]);

        viewModel.SaveAll().ShouldBeNull();

        File.GetLastWriteTimeUtc(path).ShouldBe(before);
    }

    [Fact]
    public void Kind_SwitchedToContainer_SwapsWhichSettingsGroupIsShown()
    {
        var viewModel = CreateViewModel(PublishProfile.CreateFolderDefault("win-x64"));
        var profile = viewModel.SelectedProfile!;

        profile.Kind = PublishKind.Container;

        profile.IsContainerTarget.ShouldBeTrue();
        profile.IsFolderTarget.ShouldBeFalse();
    }

    [Fact]
    public void DeploymentMode_RoundTripsTheTriStateSelfContainedFlag()
    {
        var viewModel = CreateViewModel(PublishProfile.CreateFolderDefault("win-x64") with { SelfContained = null });
        var profile = viewModel.SelectedProfile!;

        profile.DeploymentMode.ShouldBe(DeploymentModeDisplay.ProjectDefault);
        profile.ToModel().SelfContained.ShouldBeNull();

        profile.DeploymentMode = DeploymentModeDisplay.SelfContained;
        profile.ToModel().SelfContained.ShouldBe(true);
    }

    /// <summary>An MSDeploy profile is listed so the user can see it exists, but must never be edited
    /// or published from here - writing it would discard the settings IDE++ does not model.</summary>
    [Fact]
    public void AnUnsupportedProfile_IsReadOnlyAndCannotBePublished()
    {
        var unsupported = PublishProfileReader.Parse(
            "<Project><PropertyGroup><WebPublishMethod>MSDeploy</WebPublishMethod></PropertyGroup></Project>",
            "azure")!;
        var viewModel = CreateViewModel(unsupported);

        viewModel.SelectedProfile!.IsEditable.ShouldBeFalse();
        viewModel.SelectedProfile.UnsupportedMessage.ShouldNotBeEmpty();
        viewModel.PublishCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void SaveAll_NeverWritesAnUnsupportedProfileEvenIfItLooksDirty()
    {
        var unsupported = PublishProfileReader.Parse(
            "<Project><PropertyGroup><WebPublishMethod>MSDeploy</WebPublishMethod></PropertyGroup></Project>",
            "azure")!;
        var viewModel = CreateViewModel(unsupported);
        viewModel.SelectedProfile!.RuntimeIdentifier = "win-x64";

        viewModel.SaveAll().ShouldBeNull();

        PublishProfileStore.Load(_projectPath).ShouldBeEmpty();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a leftover temp folder is not worth failing a passing test over.
        }
    }
}
