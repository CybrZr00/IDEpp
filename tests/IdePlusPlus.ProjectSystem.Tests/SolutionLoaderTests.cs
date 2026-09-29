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

    /// <summary>
    /// The sample declares no &lt;RootNamespace&gt;, which is the common case - the SDK defaults it to
    /// the project name. New Item derives a new file's namespace from this, so reading it as empty
    /// would put every generated file in the global namespace.
    /// </summary>
    [Fact]
    public void Load_ProjectWithNoExplicitRootNamespace_StillReportsOne()
    {
        var root = SolutionLoader.Load(FixtureSolutionPath);

        var project = root.Children.ShouldHaveSingleItem();
        project.RootNamespace.ShouldBe("SampleConsoleApp");
    }

    /// <summary>Only Project nodes have one; a file node's would be meaningless.</summary>
    [Fact]
    public void Load_NonProjectNodes_HaveNoRootNamespace()
    {
        var root = SolutionLoader.Load(FixtureSolutionPath);

        root.RootNamespace.ShouldBeNull();
        root.Children.ShouldHaveSingleItem().Children.ShouldHaveSingleItem().RootNamespace.ShouldBeNull();
    }

    [Fact]
    public void Load_MultiTargetedProject_ResolvesTheWindowsTargetFramework()
    {
        var root = SolutionLoader.Load(MultiTargetFixtureSolutionPath);

        var project = root.Children.ShouldHaveSingleItem();
        project.TargetFramework.ShouldBe("net10.0-windows10.0.19041.0");
    }
}
