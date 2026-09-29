using IdePlusPlus.ProjectSystem;
using Shouldly;
using Xunit;

namespace IdePlusPlus.ProjectSystem.Tests;

/// <summary>
/// The folder chain SolutionLoader builds, and that New Item reuses to place a created file without
/// reloading the solution.
/// </summary>
public class SolutionTreeNodeTests
{
    private static SolutionTreeNode Project() =>
        new("Demo", @"C:\repo\Demo\Demo.csproj", SolutionTreeNodeKind.Project);

    [Fact]
    public void CreatesTheFolderChain()
    {
        var project = Project();

        var leaf = project.GetOrCreateFolder(["Api", "V2"]);

        leaf.Name.ShouldBe("V2");
        project.Children.ShouldHaveSingleItem().Name.ShouldBe("Api");
    }

    [Fact]
    public void ReusesAnExistingFolderRatherThanDuplicatingIt()
    {
        var project = Project();

        var first = project.GetOrCreateFolder(["Models"]);
        var second = project.GetOrCreateFolder(["Models"]);

        second.ShouldBeSameAs(first);
        project.Children.Count.ShouldBe(1);
    }

    /// <summary>
    /// Given a directory, each folder gets a real path - which is what lets a folder be the target of
    /// "Add New Item" and what gives plugin context-menu commands something to act on. Before this,
    /// folder nodes had no path at all.
    /// </summary>
    [Fact]
    public void GivenADirectory_EachFolderGetsItsOwnPath()
    {
        var project = Project();

        var leaf = project.GetOrCreateFolder(["Api", "V2"], @"C:\repo\Demo");

        leaf.FullPath.ShouldBe(Path.Combine(@"C:\repo\Demo", "Api", "V2"));
        project.Children.ShouldHaveSingleItem().FullPath.ShouldBe(Path.Combine(@"C:\repo\Demo", "Api"));
    }

    /// <summary>The parameter is optional, and omitting it keeps the pathless behaviour this had
    /// before - so an existing caller that does not pass one is unaffected.</summary>
    [Fact]
    public void WithoutADirectory_FoldersStayPathless() =>
        Project().GetOrCreateFolder(["Models"]).FullPath.ShouldBeNull();

    [Fact]
    public void AnEmptyChainReturnsTheNodeItself()
    {
        var project = Project();

        project.GetOrCreateFolder([], @"C:\repo\Demo").ShouldBeSameAs(project);
        project.Children.ShouldBeEmpty();
    }

    [Fact]
    public void FlattenReturnsTheNodeAndEveryDescendant()
    {
        var project = Project();
        project.GetOrCreateFolder(["Api", "V2"], @"C:\repo\Demo")
            .Children.Add(new SolutionTreeNode("Thing.cs", @"C:\repo\Demo\Api\V2\Thing.cs", SolutionTreeNodeKind.File));

        project.Flatten().Select(n => n.Name).ShouldBe(["Demo", "Api", "V2", "Thing.cs"]);
    }

    [Fact]
    public void SortChildrenRecursively_OrdersFoldersBeforeFilesThenAlphabetically()
    {
        var project = Project();
        project.Children.Add(new SolutionTreeNode("Zeta.cs", null, SolutionTreeNodeKind.File));
        project.Children.Add(new SolutionTreeNode("Api", null, SolutionTreeNodeKind.Folder));
        project.Children.Add(new SolutionTreeNode("Alpha.cs", null, SolutionTreeNodeKind.File));

        project.SortChildrenRecursively();

        project.Children.Select(c => c.Name).ShouldBe(["Api", "Alpha.cs", "Zeta.cs"]);
    }

    [Fact]
    public void SortChildrenRecursively_RecursesIntoFolders()
    {
        var project = Project();
        var folder = project.GetOrCreateFolder(["Api"]);
        folder.Children.Add(new SolutionTreeNode("Zeta.cs", null, SolutionTreeNodeKind.File));
        folder.Children.Add(new SolutionTreeNode("Alpha.cs", null, SolutionTreeNodeKind.File));

        project.SortChildrenRecursively();

        folder.Children.Select(c => c.Name).ShouldBe(["Alpha.cs", "Zeta.cs"]);
    }
}
