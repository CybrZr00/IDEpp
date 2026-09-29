using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class CreatedFileScannerTests
{
    [Fact]
    public void ReportsAFileThatAppeared()
    {
        using var directory = new TempDirectory();
        var before = CreatedFileScanner.Snapshot(directory.Path);
        var created = directory.WriteFile("Widget.cs");

        CreatedFileScanner.NewFilesSince(directory.Path, before).ShouldBe([created]);
    }

    [Fact]
    public void IgnoresFilesThatWereAlreadyThere()
    {
        using var directory = new TempDirectory();
        directory.WriteFile("Existing.cs");
        var before = CreatedFileScanner.Snapshot(directory.Path);

        CreatedFileScanner.NewFilesSince(directory.Path, before).ShouldBeEmpty();
    }

    [Fact]
    public void FindsFilesInSubdirectories()
    {
        using var directory = new TempDirectory();
        var before = CreatedFileScanner.Snapshot(directory.Path);
        var created = directory.WriteFile("Models/Widget.cs");

        CreatedFileScanner.NewFilesSince(directory.Path, before).ShouldBe([created]);
    }

    /// <summary>A project template's restore post-action fills obj/ with a dozen files; opening
    /// project.assets.json is not how New Project should finish.</summary>
    [Fact]
    public void ExcludesBuildOutput()
    {
        using var directory = new TempDirectory();
        var before = CreatedFileScanner.Snapshot(directory.Path);
        var source = directory.WriteFile("Lib.csproj");
        directory.WriteFile("obj/project.assets.json");
        directory.WriteFile("bin/Debug/Lib.dll");

        CreatedFileScanner.NewFilesSince(directory.Path, before).ShouldBe([source]);
    }

    /// <summary>A solution-kind template creates several project directories, each with its own
    /// obj/ - so the exclusion cannot look only at the first path segment.</summary>
    [Fact]
    public void ExcludesNestedBuildOutput()
    {
        using var directory = new TempDirectory();
        var before = CreatedFileScanner.Snapshot(directory.Path);
        var source = directory.WriteFile("src/Api/Api.csproj");
        directory.WriteFile("src/Api/obj/project.assets.json");

        CreatedFileScanner.NewFilesSince(directory.Path, before).ShouldBe([source]);
    }

    /// <summary>A file that merely has "obj" in its name is not build output.</summary>
    [Fact]
    public void DoesNotExcludeFilesNamedLikeBuildOutput()
    {
        using var directory = new TempDirectory();
        var before = CreatedFileScanner.Snapshot(directory.Path);
        var created = directory.WriteFile("object.cs");

        CreatedFileScanner.NewFilesSince(directory.Path, before).ShouldBe([created]);
    }

    /// <summary>For a project template the CLI creates the output directory itself, so the "before"
    /// snapshot is taken against something that doesn't exist yet.</summary>
    [Fact]
    public void ADirectoryThatDoesNotExistYetSnapshotsAsEmpty()
    {
        using var directory = new TempDirectory();
        var notYet = directory.PathFor("NewProject");

        CreatedFileScanner.Snapshot(notYet).ShouldBeEmpty();

        Directory.CreateDirectory(notYet);
        var created = directory.WriteFile("NewProject/NewProject.csproj");

        CreatedFileScanner.NewFilesSince(notYet, new HashSet<string>()).ShouldBe([created]);
    }

    [Fact]
    public void ResultsAreSortedSoFilesOpenInAStableOrder()
    {
        using var directory = new TempDirectory();
        var before = CreatedFileScanner.Snapshot(directory.Path);
        directory.WriteFile("Page.xaml.cs");
        directory.WriteFile("Page.xaml");

        CreatedFileScanner.NewFilesSince(directory.Path, before)
            .Select(Path.GetFileName)
            .ShouldBe(["Page.xaml", "Page.xaml.cs"]);
    }
}
