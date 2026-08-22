using IdePlusPlus.ProjectSystem;
using Shouldly;
using Xunit;

namespace IdePlusPlus.ProjectSystem.Tests;

public class SolutionLoaderTests
{
    private static readonly string FixtureSolutionPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "SampleConsoleApp", "SampleConsoleApp.slnx");

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
}
