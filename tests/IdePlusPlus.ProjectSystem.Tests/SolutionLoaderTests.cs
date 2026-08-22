using IdePlusPlus.ProjectSystem;
using Shouldly;
using Xunit;

namespace IdePlusPlus.ProjectSystem.Tests;

public class SolutionLoaderTests
{
    private static readonly string FixtureSolutionPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "SampleConsoleApp", "SampleConsoleApp.slnx");

    private static readonly string MultiTargetFixtureSolutionPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "SampleMultiTargetApp", "SampleMultiTargetApp.slnx");

    [Fact]
    public void Load_ParsesSlnxIntoSolutionProjectFileTree()
    {
        var root = SolutionLoader.Load(FixtureSolutionPath);

        root.Kind.ShouldBe(SolutionTreeNodeKind.Solution);
        root.Name.ShouldBe("SampleConsoleApp");

        var project = root.Children.ShouldHaveSingleItem();
        project.Kind.ShouldBe(SolutionTreeNodeKind.Project);
        project.Name.ShouldBe("SampleConsoleApp");

        var programFile = project.Children.ShouldHaveSingleItem();
        programFile.Kind.ShouldBe(SolutionTreeNodeKind.File);
        programFile.Name.ShouldBe("Program.cs");
        File.Exists(programFile.FullPath).ShouldBeTrue();
    }

    [Fact]
    public void Load_SingleTargetFrameworkProject_HasNullTargetFramework()
    {
        var root = SolutionLoader.Load(FixtureSolutionPath);

        var project = root.Children.ShouldHaveSingleItem();
        project.TargetFramework.ShouldBeNull();
    }

    [Fact]
    public void Load_MultiTargetedProject_ResolvesTheWindowsTargetFramework()
    {
        var root = SolutionLoader.Load(MultiTargetFixtureSolutionPath);

        var project = root.Children.ShouldHaveSingleItem();
        project.TargetFramework.ShouldBe("net10.0-windows10.0.19041.0");
    }
}
