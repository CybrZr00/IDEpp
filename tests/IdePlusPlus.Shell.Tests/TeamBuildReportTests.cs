using IdePlusPlus.Shell.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// "Could not copy the file ...\w1\FamWholesale\node_modules\bootstrap\scss\x.scss because it was not
/// found" is MSBuild's whole explanation. What the user needs to be told is that the file is in their
/// own tree and missing from the team's copy only because git ignores it.
/// </summary>
public sealed class TeamBuildReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-report-{Guid.NewGuid():N}");
    private readonly string _worktree;
    private readonly string _repository;

    public TeamBuildReportTests()
    {
        _worktree = Directory.CreateDirectory(Path.Combine(_root, "ultra", "run1", "w1")).FullName;
        _repository = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void InRepository(string relativePath)
    {
        var path = Path.Combine(_repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private void InWorktree(string relativePath)
    {
        var path = Path.Combine(_worktree, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private string CopyError(string relativePath) =>
        $"C:\\sdk\\Microsoft.Common.CurrentVersion.targets(5455,5): error MSB3030: Could not copy the file \"{Path.Combine(_worktree, relativePath)}\" because it was not found.";

    [Fact]
    public void ManyFilesUnderOneIgnoredFolder_AreReportedAsThatFolder_Once()
    {
        InWorktree(@"FamWholesale\FamWholesale.csproj");
        InRepository(@"FamWholesale\node_modules\bootstrap\scss\_type.scss");
        InRepository(@"FamWholesale\node_modules\bootstrap\scss\forms\_form-check.scss");
        var report = string.Join("\n", CopyError(@"FamWholesale\node_modules\bootstrap\scss\_type.scss"),
            CopyError(@"FamWholesale\node_modules\bootstrap\scss\forms\_form-check.scss"),
            CopyError(@"FamWholesale\node_modules\bootstrap\scss\_type.scss"));

        TeamBuildReport.FindMissingFromCopy(report, _worktree, _repository).ShouldBe([@"FamWholesale\node_modules"]);
    }

    [Fact]
    public void AFileMissingFromTheUsersTreeToo_IsNotBlamedOnTheCopy()
    {
        InWorktree(@"Lib\Lib.csproj");
        var report = CopyError(@"Lib\typo\data.txt");

        TeamBuildReport.FindMissingFromCopy(report, _worktree, _repository).ShouldBeEmpty();
    }

    [Fact]
    public void AFileThatIsInTheCopy_IsNotReported()
    {
        InWorktree(@"Lib\data.txt");
        InRepository(@"Lib\data.txt");

        TeamBuildReport.FindMissingFromCopy(CopyError(@"Lib\data.txt"), _worktree, _repository).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"{0}\Lib\generated\Api.g.cs(12,5): error CS0246: The type 'X' could not be found")]
    [InlineData(@"{0}\Lib\generated\Api.g.cs: error CS2001: Source file could not be found")]
    [InlineData(@"error : Could not find a part of the path '{0}\Lib\generated\Api.g.cs'. [{0}\Lib\Lib.csproj]")]
    public void PathsEndWhereMSBuildsOwnPunctuationBegins(string template)
    {
        InWorktree(@"Lib\Lib.csproj");
        InRepository(@"Lib\generated\Api.g.cs");

        TeamBuildReport.FindMissingFromCopy(string.Format(template, _worktree), _worktree, _repository)
            .ShouldBe([@"Lib\generated"]);
    }

    [Fact]
    public void SeveralFolders_AreEachReported_AndTheListIsCapped()
    {
        InWorktree(@"Lib\Lib.csproj");
        var report = new List<string>();
        for (var i = 0; i < 15; i++)
        {
            InRepository($@"Lib\ignored{i:00}\file.txt");
            report.Add(CopyError($@"Lib\ignored{i:00}\file.txt"));
        }

        var found = TeamBuildReport.FindMissingFromCopy(string.Join("\n", report), _worktree, _repository);

        found.Count.ShouldBe(10);
        found[0].ShouldBe(@"Lib\ignored00");
    }

    [Fact]
    public void AReportThatNamesNothingInTheWorktree_IsEmpty()
    {
        TeamBuildReport.FindMissingFromCopy("Build FAILED. error CS1002: ; expected", _worktree, _repository).ShouldBeEmpty();
    }
}
