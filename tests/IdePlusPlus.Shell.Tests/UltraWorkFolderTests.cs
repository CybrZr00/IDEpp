using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The Ultra work folder is where every worker's checkout and build output go, so a user short of room
/// on the project's drive can point it elsewhere. What can be said about a folder without running
/// anything is checked here; that a run really uses it is in CopilotChatUltraTests.
/// </summary>
public sealed class UltraWorkFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-workfolder-{Guid.NewGuid():N}");
    private readonly string _repository;

    public UltraWorkFolderTests()
    {
        _repository = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AFullPathOutsideTheRepository_IsAccepted_AndTheFolderIsCreated()
    {
        var folder = Path.Combine(_root, "scratch", "ultra");

        CopilotChatPaneViewModel.ValidateUltraWorkFolder(folder, _repository).ShouldBeNull();

        Directory.Exists(folder).ShouldBeTrue("only creating it shows it can be created");
    }

    [Fact]
    public void ATrailingSeparator_IsFine()
    {
        CopilotChatPaneViewModel.ValidateUltraWorkFolder(Path.Combine(_root, "scratch") + Path.DirectorySeparatorChar, _repository)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("scratch")]
    [InlineData(@"..\scratch")]
    [InlineData(@"\scratch")]
    public void APathThatIsNotFullyQualified_IsRefused(string folder)
    {
        CopilotChatPaneViewModel.ValidateUltraWorkFolder(folder, _repository).ShouldNotBeNull().ShouldContain("full path");
    }

    [Fact]
    public void TheRepositoryItself_AndAnyFolderInsideIt_AreRefused()
    {
        // A worktree inside the repository would show up in the user's git status.
        CopilotChatPaneViewModel.ValidateUltraWorkFolder(_repository, _repository).ShouldNotBeNull().ShouldContain("inside the repository");
        CopilotChatPaneViewModel.ValidateUltraWorkFolder(Path.Combine(_repository, "scratch"), _repository)
            .ShouldNotBeNull().ShouldContain("inside the repository");
        Directory.Exists(Path.Combine(_repository, "scratch")).ShouldBeFalse("a refused folder is not created");
    }

    [Fact]
    public void AFolderWhoseNameOnlyStartsLikeTheRepository_IsNotInsideIt()
    {
        CopilotChatPaneViewModel.ValidateUltraWorkFolder(_repository + "-scratch", _repository).ShouldBeNull();
    }

    [Fact]
    public void ADriveThatIsNotThere_IsRefused()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).First(c => !used.Contains(c));

        CopilotChatPaneViewModel.ValidateUltraWorkFolder($@"{free}:\ultra", _repository).ShouldNotBeNull().ShouldContain("not available");
    }

    [Fact]
    public void AFolderThatCannotBeCreated_IsRefused_WithTheReason()
    {
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "in the way");

        CopilotChatPaneViewModel.ValidateUltraWorkFolder(Path.Combine(file, "scratch"), _repository)
            .ShouldNotBeNull().ShouldContain("can't be created");
    }

    [Fact]
    public void TheSetting_IsTrimmed_AndBlankMeansTheDefault()
    {
        var settings = new AppSettingsStore(Path.Combine(_root, "settings.json"));
        CopilotChatPaneViewModel.ReadUltraWorkFolder(settings).ShouldBeNull();

        settings.Set(CopilotChatPaneViewModel.UltraWorkFolderSettingKey, "   ");
        CopilotChatPaneViewModel.ReadUltraWorkFolder(settings).ShouldBeNull();

        settings.Set(CopilotChatPaneViewModel.UltraWorkFolderSettingKey, @"  D:\IdePlusPlusUltra  ");
        CopilotChatPaneViewModel.ReadUltraWorkFolder(settings).ShouldBe(@"D:\IdePlusPlusUltra");
    }
}
