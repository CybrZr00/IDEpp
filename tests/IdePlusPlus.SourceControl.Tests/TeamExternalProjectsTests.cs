using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.SourceControl.Tests;

/// <summary>
/// A solution that builds from a folder beside its repository ("..\RRS.FamRetail") only builds in a
/// team worktree if that folder is made to appear where the worktree's relative paths look for it.
/// These use real folders, since what matters is how the real junction behaves.
/// </summary>
public sealed class TeamExternalProjectsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-external-{Guid.NewGuid():N}");
    private readonly string _repo;

    public TeamExternalProjectsTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - a lingering handle on Windows shouldn't fail the test run.
        }
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string SlnLine(string name, string relativePath) =>
        $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{relativePath}\", \"{{776E1F17-ED8F-419B-9633-F21A0B3D120C}}\"\nEndProject\n";

    private const string EmptyProject = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

    [Fact]
    public void Find_ASlnThatListsAProjectBesideTheRepository_ReturnsThatFolder()
    {
        Write(@"repo\App\App.csproj", EmptyProject);
        Write(@"Lib\Lib.csproj", EmptyProject);
        var sln = Write(@"repo\App.sln", SlnLine("App", @"App\App.csproj") + SlnLine("Lib", @"..\Lib\Lib.csproj"));

        var found = TeamExternalProjects.Find(sln, _repo);

        var sibling = found.Siblings.ShouldHaveSingleItem();
        sibling.Name.ShouldBe("Lib");
        sibling.SourcePath.ShouldBe(Path.Combine(_root, "Lib"));
        found.Unreachable.ShouldBeEmpty();
    }

    [Fact]
    public void Find_ASlnxThatListsAProjectBesideTheRepository_ReturnsThatFolder()
    {
        Write(@"repo\App\App.csproj", EmptyProject);
        Write(@"Lib\Lib.csproj", EmptyProject);
        var slnx = Write(
            @"repo\App.slnx",
            "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"../Lib/Lib.csproj\" /></Solution>");

        TeamExternalProjects.Find(slnx, _repo).Siblings.Select(s => s.Name).ShouldBe(["Lib"]);
    }

    [Fact]
    public void Find_AProjectReferenceThatLeavesTheRepository_IsFoundEvenWhenTheSolutionDoesNotListIt()
    {
        // The real shape of FamRestApi.AppHost -> ..\..\RRS.FamRetail: the solution lists only AppHost.
        Write(@"Lib\Lib.csproj", EmptyProject);
        Write(
            @"repo\AppHost\AppHost.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\..\\Lib\\Lib.csproj\" /></ItemGroup></Project>");
        var sln = Write(@"repo\App.sln", SlnLine("AppHost", @"AppHost\AppHost.csproj"));

        TeamExternalProjects.Find(sln, _repo).Siblings.Select(s => s.Name).ShouldBe(["Lib"]);
    }

    [Fact]
    public void Find_FollowsReferencesFromOneExternalProjectToAnother()
    {
        Write(@"Core\Core.csproj", EmptyProject);
        Write(
            @"Lib\Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup></Project>");
        var sln = Write(@"repo\App.sln", SlnLine("Lib", @"..\Lib\Lib.csproj"));

        TeamExternalProjects.Find(sln, _repo).Siblings.Select(s => s.Name).ShouldBe(["Core", "Lib"]);
    }

    [Fact]
    public void Find_ASelfContainedSolution_ReturnsNothing()
    {
        Write(@"repo\App\App.csproj", EmptyProject);
        Write(
            @"repo\Tests\Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\App\\App.csproj\" /></ItemGroup></Project>");
        var sln = Write(@"repo\App.sln", SlnLine("App", @"App\App.csproj") + SlnLine("Tests", @"Tests\Tests.csproj"));

        var found = TeamExternalProjects.Find(sln, _repo);

        found.Siblings.ShouldBeEmpty();
        found.Unreachable.ShouldBeEmpty();
    }

    [Fact]
    public void Find_AProjectTwoLevelsAboveTheRepository_IsReportedUnreachable()
    {
        // A link can only stand in for a folder beside the repository, which is the one place a worktree's
        // "..\" lands inside the run folder.
        var nested = Path.Combine(_root, "group", "repo");
        Directory.CreateDirectory(nested);
        Write(@"shared\Shared.csproj", EmptyProject);
        var sln = Write(@"group\repo\App.sln", SlnLine("Shared", @"..\..\shared\Shared.csproj"));

        var found = TeamExternalProjects.Find(sln, nested);

        found.Siblings.ShouldBeEmpty();
        found.Unreachable.ShouldHaveSingleItem().ShouldEndWith("Shared.csproj");
    }

    [Fact]
    public void Find_ASolutionFolderAndAMissingProject_AreIgnored()
    {
        var sln = Write(
            @"repo\App.sln",
            SlnLine("Solution Items", "Solution Items") + SlnLine("Gone", @"..\Gone\Gone.csproj"));

        TeamExternalProjects.Find(sln, _repo).ShouldBeSameAs(TeamExternalFolders.None);
    }

    [Fact]
    public void Find_AnUnreadableSolution_ReturnsNothingInsteadOfThrowing()
    {
        var slnx = Write(@"repo\App.slnx", "<Solution><Project");

        TeamExternalProjects.Find(slnx, _repo).ShouldBeSameAs(TeamExternalFolders.None);
        TeamExternalProjects.Find(Path.Combine(_repo, "missing.sln"), _repo).ShouldBeSameAs(TeamExternalFolders.None);
    }

    [Fact]
    public void Link_MakesTheFolderAppearInTheRunDirectory_AndUnlinkLeavesTheOriginalAlone()
    {
        Write(@"Lib\Lib.csproj", EmptyProject);
        Write(@"Lib\Sub\Keep.txt", "keep me");
        var run = Directory.CreateDirectory(Path.Combine(_root, "run")).FullName;

        var links = TeamExternalProjects.Link(run, [new TeamExternalFolder("Lib", Path.Combine(_root, "Lib"))]);

        var link = links.ShouldHaveSingleItem();
        link.ShouldBe(Path.Combine(run, "Lib"));
        File.Exists(Path.Combine(run, "Lib", "Lib.csproj")).ShouldBeTrue("a worktree's ..\\Lib\\Lib.csproj has to resolve");

        TeamExternalProjects.Unlink(links);

        Directory.Exists(link).ShouldBeFalse();
        File.ReadAllText(Path.Combine(_root, "Lib", "Sub", "Keep.txt")).ShouldBe("keep me");
    }

    [Fact]
    public void Unlink_ARealFolder_IsLeftAlone()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "not-a-link")).FullName;
        File.WriteAllText(Path.Combine(folder, "file.txt"), "x");

        TeamExternalProjects.Unlink([folder]);

        File.Exists(Path.Combine(folder, "file.txt")).ShouldBeTrue();
    }

    [Fact]
    public void Link_SomethingAlreadyThere_IsLeftAloneAndNotReturned()
    {
        Write(@"Lib\Lib.csproj", EmptyProject);
        var run = Directory.CreateDirectory(Path.Combine(_root, "run")).FullName;
        Directory.CreateDirectory(Path.Combine(run, "Lib"));

        TeamExternalProjects.Link(run, [new TeamExternalFolder("Lib", Path.Combine(_root, "Lib"))]).ShouldBeEmpty();
    }
}
