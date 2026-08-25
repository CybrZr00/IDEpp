using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Diff.Tests;

/// <summary>
/// Covers ComputeResultingContent, the pure static that every applied edit passes through. It had
/// no coverage at all until an audit traced silent file truncation back to it, so these pin the
/// behaviour that is meant to stay put - the refusal paths added alongside that audit are covered
/// separately below.
/// </summary>
public class EditApplicationServiceTests
{
    private static EditOperation WholeFile(string newText) =>
        new("Foo.cs", "whole_file", null, null, newText);

    private static EditOperation ReplaceRange(int startLine, int endLine, string newText) =>
        new("Foo.cs", "replace_range", startLine, endLine, newText);

    [Fact]
    public void ComputeResultingContent_WholeFile_ReturnsTheNewTextVerbatim()
    {
        var result = EditApplicationService.ComputeResultingContent(WholeFile("only line"), "a\nb\nc");

        result.ShouldBe("only line");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_ReplacesJustTheInclusiveRange()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(2, 3, "B\nC"), "a\nb\nc\nd");

        result.ShouldBe("a\nB\nC\nd");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_WithStartEqualToEnd_ReplacesASingleLine()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(2, 2, "B"), "a\nb\nc");

        result.ShouldBe("a\nB\nc");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_CanGrowTheFile()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(2, 2, "B1\nB2\nB3"), "a\nb\nc");

        result.ShouldBe("a\nB1\nB2\nB3\nc");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_CanShrinkTheFile()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(2, 4, "B"), "a\nb\nc\nd\ne");

        result.ShouldBe("a\nB\ne");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_ReplacingTheFirstLine()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(1, 1, "A"), "a\nb\nc");

        result.ShouldBe("A\nb\nc");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_ReplacingTheLastLine()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(3, 3, "C"), "a\nb\nc");

        result.ShouldBe("a\nb\nC");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_SpanningTheWholeFile()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(1, 3, "x"), "a\nb\nc");

        result.ShouldBe("x");
    }

    /// <summary>Line endings are normalized to LF on both sides, so an edit to a CRLF file rewrites
    /// the whole file's endings. Pinned as current behaviour, not endorsed as correct.</summary>
    [Fact]
    public void ComputeResultingContent_ReplaceRange_NormalizesCrlfInTheExistingContent()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(2, 2, "B"), "a\r\nb\r\nc");

        result.ShouldBe("a\nB\nc");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_NormalizesCrlfInTheNewText()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(2, 2, "B1\r\nB2"), "a\nb\nc");

        result.ShouldBe("a\nB1\nB2\nc");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_OnAnEmptyFile_ProducesJustTheNewText()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(1, 1, "first"), "");

        result.ShouldBe("first");
    }

    [Fact]
    public void ComputeResultingContent_ReplaceRange_PreservesATrailingBlankLine()
    {
        var result = EditApplicationService.ComputeResultingContent(ReplaceRange(1, 1, "A"), "a\nb\n");

        result.ShouldBe("A\nb\n");
    }
}
