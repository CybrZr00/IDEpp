using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.SourceControl.Tests;

public class GitStatusParserTests
{
    [Fact]
    public void Parse_FreshRepoWithNoCommits_ReportsBranchNameAndUntrackedFile()
    {
        var status = GitStatusParser.Parse("## No commits yet on main\n?? file.txt\n");

        status.Branch.ShouldBe("main");
        status.Ahead.ShouldBe(0);
        status.Behind.ShouldBe(0);
        status.Staged.ShouldBeEmpty();
        var change = status.Unstaged.ShouldHaveSingleItem();
        change.Path.ShouldBe("file.txt");
        change.Status.ShouldBe(GitFileStatus.Untracked);
        change.IsStaged.ShouldBeFalse();
    }

    [Fact]
    public void Parse_UpToDateWithUpstream_ReportsZeroAheadBehind()
    {
        var status = GitStatusParser.Parse("## main...origin/main\n");

        status.Branch.ShouldBe("main");
        status.Ahead.ShouldBe(0);
        status.Behind.ShouldBe(0);
    }

    [Fact]
    public void Parse_AheadAndBehindUpstream_ParsesBothCounts()
    {
        var status = GitStatusParser.Parse("## main...origin/main [ahead 2, behind 1]\n");

        status.Ahead.ShouldBe(2);
        status.Behind.ShouldBe(1);
    }

    [Fact]
    public void Parse_AheadOnly_ParsesAheadWithZeroBehind()
    {
        var status = GitStatusParser.Parse("## main...origin/main [ahead 3]\n");

        status.Ahead.ShouldBe(3);
        status.Behind.ShouldBe(0);
    }

    [Fact]
    public void Parse_DetachedHead_HasNullBranch()
    {
        var status = GitStatusParser.Parse("## HEAD (no branch)\n");

        status.Branch.ShouldBeNull();
    }

    [Fact]
    public void Parse_UnstagedModification_IsNotStaged()
    {
        var status = GitStatusParser.Parse("## main\n M src/Foo.cs\n");

        status.Staged.ShouldBeEmpty();
        var change = status.Unstaged.ShouldHaveSingleItem();
        change.Path.ShouldBe("src/Foo.cs");
        change.Status.ShouldBe(GitFileStatus.Modified);
        change.IsStaged.ShouldBeFalse();
    }

    [Fact]
    public void Parse_StagedModification_IsStaged()
    {
        var status = GitStatusParser.Parse("## main\nM  src/Foo.cs\n");

        status.Unstaged.ShouldBeEmpty();
        var change = status.Staged.ShouldHaveSingleItem();
        change.Path.ShouldBe("src/Foo.cs");
        change.Status.ShouldBe(GitFileStatus.Modified);
        change.IsStaged.ShouldBeTrue();
    }

    [Fact]
    public void Parse_StagedThenFurtherModifiedFile_AppearsInBothLists()
    {
        var status = GitStatusParser.Parse("## main\nMM src/Foo.cs\n");

        status.Staged.ShouldHaveSingleItem().Path.ShouldBe("src/Foo.cs");
        status.Unstaged.ShouldHaveSingleItem().Path.ShouldBe("src/Foo.cs");
    }

    [Fact]
    public void Parse_StagedAddedFile_MapsToAdded()
    {
        var status = GitStatusParser.Parse("## main\nA  new.cs\n");

        status.Staged.ShouldHaveSingleItem().Status.ShouldBe(GitFileStatus.Added);
    }

    [Fact]
    public void Parse_UnstagedDeletedFile_MapsToDeleted()
    {
        var status = GitStatusParser.Parse("## main\n D gone.cs\n");

        status.Unstaged.ShouldHaveSingleItem().Status.ShouldBe(GitFileStatus.Deleted);
    }

    [Fact]
    public void Parse_StagedRename_UsesNewPathAndRenamedStatus()
    {
        var status = GitStatusParser.Parse("## main\nR  old.cs -> new.cs\n");

        var change = status.Staged.ShouldHaveSingleItem();
        change.Path.ShouldBe("new.cs");
        change.Status.ShouldBe(GitFileStatus.Renamed);
    }

    [Fact]
    public void Parse_UntrackedFile_IsUnstagedAndUntracked()
    {
        var status = GitStatusParser.Parse("## main\n?? untracked.txt\n");

        var change = status.Unstaged.ShouldHaveSingleItem();
        change.Path.ShouldBe("untracked.txt");
        change.Status.ShouldBe(GitFileStatus.Untracked);
    }

    [Fact]
    public void Parse_MergeConflict_IsReportedAsSingleConflictedUnstagedEntry()
    {
        var status = GitStatusParser.Parse("## main\nUU conflict.cs\n");

        status.Staged.ShouldBeEmpty();
        var change = status.Unstaged.ShouldHaveSingleItem();
        change.Path.ShouldBe("conflict.cs");
        change.Status.ShouldBe(GitFileStatus.Conflicted);
    }

    [Fact]
    public void Parse_IgnoredFile_IsOmitted()
    {
        var status = GitStatusParser.Parse("## main\n!! bin/Debug/output.dll\n");

        status.Staged.ShouldBeEmpty();
        status.Unstaged.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsCleanStatusWithNullBranch()
    {
        var status = GitStatusParser.Parse("");

        status.Branch.ShouldBeNull();
        status.Staged.ShouldBeEmpty();
        status.Unstaged.ShouldBeEmpty();
    }
}
