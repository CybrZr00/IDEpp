using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.SourceControl.Tests;

public class GitBranchParserTests
{
    [Fact]
    public void Parse_MarksTheHeadBranchAsCurrent()
    {
        var branches = GitBranchParser.Parse("main\t*\nfeature-x\t \n");

        branches.Count.ShouldBe(2);
        branches[0].ShouldBe(new GitBranch("main", IsCurrent: true));
        branches[1].ShouldBe(new GitBranch("feature-x", IsCurrent: false));
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsNoBranches()
    {
        var branches = GitBranchParser.Parse("");

        branches.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_SkipsMalformedLines()
    {
        var branches = GitBranchParser.Parse("main\t*\nnot-a-valid-line\n");

        branches.ShouldHaveSingleItem().Name.ShouldBe("main");
    }
}
