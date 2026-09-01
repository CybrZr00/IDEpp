using System.Globalization;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class SolutionTreeIconConverterTests
{
    private static object Convert(SolutionTreeNodeKind kind, string name) =>
        SolutionTreeIconConverter.Instance.Convert([kind, name], typeof(string), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(SolutionTreeNodeKind.Solution, "MySolution")]
    [InlineData(SolutionTreeNodeKind.Project, "MyProject")]
    [InlineData(SolutionTreeNodeKind.Folder, "Models")]
    public void NonFileKinds_GetANonEmptyGlyphRegardlessOfName(SolutionTreeNodeKind kind, string name)
    {
        Convert(kind, name).ShouldNotBe("");
    }

    [Theory]
    [InlineData("Foo.cs")]
    [InlineData("View.xaml")]
    [InlineData("settings.json")]
    public void FileKind_ReturnsANonEmptyGlyphForKnownExtensions(string name)
    {
        Convert(SolutionTreeNodeKind.File, name).ShouldNotBe("");
    }

    [Fact]
    public void FileKind_FallsBackToAGenericGlyphForUnknownExtensions()
    {
        Convert(SolutionTreeNodeKind.File, "data.bin").ShouldBe("📄");
    }

    [Fact]
    public void DifferentKinds_GetDifferentGlyphs()
    {
        var solution = Convert(SolutionTreeNodeKind.Solution, "x");
        var project = Convert(SolutionTreeNodeKind.Project, "x");
        var folder = Convert(SolutionTreeNodeKind.Folder, "x");

        solution.ShouldNotBe(project);
        project.ShouldNotBe(folder);
        solution.ShouldNotBe(folder);
    }
}
