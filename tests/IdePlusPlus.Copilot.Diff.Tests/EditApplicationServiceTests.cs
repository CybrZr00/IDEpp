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

    private static EditOperation ReplaceRange(int? startLine, int? endLine, string newText) =>
        new("Foo.cs", "replace_range", startLine, endLine, newText);

    private static EditOperation ReplaceText(string? oldText, string newText) =>
        new("Foo.cs", "replace_text", null, null, newText, oldText);

    /// <summary>Asserts the operation is accepted and returns the resulting content.</summary>
    private static string Compute(EditOperation operation, string currentContent)
    {
        EditApplicationService
            .TryComputeResultingContent(operation, currentContent, out var result, out var error)
            .ShouldBeTrue($"expected success but got: {error}");
        return result;
    }

    /// <summary>Asserts the operation is refused and returns the message.</summary>
    private static string Refuse(EditOperation operation, string currentContent)
    {
        EditApplicationService
            .TryComputeResultingContent(operation, currentContent, out var result, out var error)
            .ShouldBeFalse();
        result.ShouldBe("");
        error.ShouldNotBeNullOrWhiteSpace();
        return error!;
    }

    [Fact]
    public void TryComputeResultingContent_WholeFile_ReturnsTheNewTextVerbatim()
    {
        var result = Compute(WholeFile("only line"), "a\nb\nc");

        result.ShouldBe("only line");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_ReplacesJustTheInclusiveRange()
    {
        var result = Compute(ReplaceRange(2, 3, "B\nC"), "a\nb\nc\nd");

        result.ShouldBe("a\nB\nC\nd");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_WithStartEqualToEnd_ReplacesASingleLine()
    {
        var result = Compute(ReplaceRange(2, 2, "B"), "a\nb\nc");

        result.ShouldBe("a\nB\nc");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_CanGrowTheFile()
    {
        var result = Compute(ReplaceRange(2, 2, "B1\nB2\nB3"), "a\nb\nc");

        result.ShouldBe("a\nB1\nB2\nB3\nc");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_CanShrinkTheFile()
    {
        var result = Compute(ReplaceRange(2, 4, "B"), "a\nb\nc\nd\ne");

        result.ShouldBe("a\nB\ne");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_ReplacingTheFirstLine()
    {
        var result = Compute(ReplaceRange(1, 1, "A"), "a\nb\nc");

        result.ShouldBe("A\nb\nc");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_ReplacingTheLastLine()
    {
        var result = Compute(ReplaceRange(3, 3, "C"), "a\nb\nc");

        result.ShouldBe("a\nb\nC");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_SpanningTheWholeFile()
    {
        var result = Compute(ReplaceRange(1, 3, "x"), "a\nb\nc");

        result.ShouldBe("x");
    }

    /// <summary>Line endings are normalized to LF on both sides, so an edit to a CRLF file rewrites
    /// the whole file's endings. Pinned as current behaviour, not endorsed as correct.</summary>
    [Fact]
    public void TryComputeResultingContent_ReplaceRange_NormalizesCrlfInTheExistingContent()
    {
        var result = Compute(ReplaceRange(2, 2, "B"), "a\r\nb\r\nc");

        result.ShouldBe("a\nB\nc");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_NormalizesCrlfInTheNewText()
    {
        var result = Compute(ReplaceRange(2, 2, "B1\r\nB2"), "a\nb\nc");

        result.ShouldBe("a\nB1\nB2\nc");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_OnAnEmptyFile_ProducesJustTheNewText()
    {
        var result = Compute(ReplaceRange(1, 1, "first"), "");

        result.ShouldBe("first");
    }

    // --- replace_text: the anchored mode that cannot go stale ---

    [Fact]
    public void TryComputeResultingContent_ReplaceText_SwapsTheMatchedSnippet()
    {
        Compute(ReplaceText("var x = 1;", "var x = 2;"), "a\nvar x = 1;\nb")
            .ShouldBe("a\nvar x = 2;\nb");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceText_CanSpanSeveralLines()
    {
        Compute(ReplaceText("b\nc", "B\nC\nD"), "a\nb\nc\nd").ShouldBe("a\nB\nC\nD\nd");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceText_CanReplaceAPartialLine()
    {
        Compute(ReplaceText("World", "There"), "Hello World!").ShouldBe("Hello There!");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceText_CanDeleteBySupplyingEmptyNewText()
    {
        Compute(ReplaceText("b\n", ""), "a\nb\nc").ShouldBe("a\nc");
    }

    /// <summary>Unlike a stale line range, a stale anchor fails visibly instead of deleting to the
    /// end of the file. That difference is the entire point of this mode.</summary>
    [Fact]
    public void TryComputeResultingContent_ReplaceText_WhenTheAnchorNoLongerMatches_IsRefused()
    {
        Refuse(ReplaceText("var x = 1;", "var x = 2;"), "a\nvar y = 9;\nb")
            .ShouldContain("not found");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceText_WhenTheAnchorIsAmbiguous_IsRefused()
    {
        Refuse(ReplaceText("return;", "return null;"), "return;\nx\nreturn;")
            .ShouldContain("2 times");
    }

    /// <summary>Overlapping matches are ambiguous too - counting non-overlapping only would let
    /// "aa" against "aaa" through as a single confident match.</summary>
    [Fact]
    public void TryComputeResultingContent_ReplaceText_CountsOverlappingMatchesAsAmbiguous()
    {
        Refuse(ReplaceText("aa", "b"), "aaa").ShouldContain("2 times");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceText_WithoutOldText_IsRefused()
    {
        Refuse(ReplaceText(null, "x"), "a\nb").ShouldContain("old_text");
    }

    /// <summary>An empty anchor matches at every position; treating it as "replace nothing" or
    /// "replace everything" would both be guesses.</summary>
    [Fact]
    public void TryComputeResultingContent_ReplaceText_WithEmptyOldText_IsRefused()
    {
        Refuse(ReplaceText("", "x"), "a\nb").ShouldContain("old_text");
    }

    /// <summary>The model sends LF; the file on disk may be CRLF. Matching must not depend on that.</summary>
    [Fact]
    public void TryComputeResultingContent_ReplaceText_MatchesAcrossLineEndingStyles()
    {
        Compute(ReplaceText("b\nc", "X"), "a\r\nb\r\nc\r\nd").ShouldBe("a\nX\nd");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceText_RefusalMessageSaysHowToDisambiguate()
    {
        Refuse(ReplaceText("return;", "return null;"), "return;\nx\nreturn;")
            .ShouldContain("more surrounding lines");
    }

    // --- Range validation: the paths that used to truncate silently ---

    /// <summary>The headline case. A null end_line clamped to the line count, so the edit deleted
    /// from start_line to EOF and still reported success.</summary>
    [Fact]
    public void TryComputeResultingContent_ReplaceRange_WithoutEndLine_IsRefusedRatherThanDeletingToEof()
    {
        Refuse(ReplaceRange(2, null, "B"), "a\nb\nc\nd\ne").ShouldContain("end_line");
    }

    /// <summary>Worse than the above: both null clamped to 0..count, replacing the entire file
    /// with whatever fragment the model sent.</summary>
    [Fact]
    public void TryComputeResultingContent_ReplaceRange_WithNeitherLineNumber_IsRefusedRatherThanReplacingTheFile()
    {
        Refuse(ReplaceRange(null, null, "B"), "a\nb\nc\nd\ne").ShouldContain("start_line");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_WithoutStartLine_IsRefused()
    {
        Refuse(ReplaceRange(null, 3, "B"), "a\nb\nc").ShouldContain("start_line");
    }

    /// <summary>The stale-line-number case: a multi-edit turn renumbers nothing, so the second
    /// edit's end_line routinely points past a file the first edit shortened.</summary>
    [Fact]
    public void TryComputeResultingContent_ReplaceRange_WithEndLinePastTheFile_IsRefused()
    {
        Refuse(ReplaceRange(2, 99, "B"), "a\nb\nc").ShouldContain("99");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_WithStartLinePastTheFile_IsRefused()
    {
        Refuse(ReplaceRange(50, 60, "B"), "a\nb\nc").ShouldContain("50");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_WithEndLineBeforeStartLine_IsRefused()
    {
        Refuse(ReplaceRange(3, 2, "B"), "a\nb\nc").ShouldContain("before");
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_WithAZeroStartLine_IsRefused()
    {
        Refuse(ReplaceRange(0, 1, "B"), "a\nb\nc").ShouldContain("start_line");
    }

    /// <summary>whole_file carries no line numbers, so validation must not reject it.</summary>
    [Fact]
    public void TryComputeResultingContent_WholeFile_IsUnaffectedByTheAbsentLineNumbers()
    {
        Compute(WholeFile("replacement"), "a\nb\nc").ShouldBe("replacement");
    }

    /// <summary>Refusals are written for the model to act on, not just for a log line.</summary>
    [Fact]
    public void TryComputeResultingContent_RefusalMessage_TellsTheModelToReReadTheFile()
    {
        Refuse(ReplaceRange(2, 99, "B"), "a\nb\nc").ShouldContain("Re-read the file");
    }

    // --- ResolveWithinWorkspace: the write path's containment check ---

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "IdePlusPlusEditTests", "MySolution");

    [Fact]
    public void ResolveWithinWorkspace_RelativePathInsideTheSolution_Resolves()
    {
        var resolved = EditApplicationService.ResolveWithinWorkspace("src/Foo/Bar.cs", Root);

        resolved.ShouldBe(Path.GetFullPath(Path.Combine(Root, "src/Foo/Bar.cs")));
    }

    [Fact]
    public void ResolveWithinWorkspace_AbsolutePathInsideTheSolution_Resolves()
    {
        var absolute = Path.Combine(Root, "src", "Foo.cs");

        EditApplicationService.ResolveWithinWorkspace(absolute, Root).ShouldBe(Path.GetFullPath(absolute));
    }

    [Fact]
    public void ResolveWithinWorkspace_TraversalOutOfTheSolution_IsRefused()
    {
        EditApplicationService
            .ResolveWithinWorkspace("../../../AppData/Local/IdePlusPlus/secrets.json", Root)
            .ShouldBeNull();
    }

    [Fact]
    public void ResolveWithinWorkspace_AbsolutePathOutsideTheSolution_IsRefused()
    {
        EditApplicationService
            .ResolveWithinWorkspace(Path.Combine(Path.GetTempPath(), "Elsewhere", "Evil.cs"), Root)
            .ShouldBeNull();
    }

    /// <summary>A sibling directory sharing a name prefix must not pass as inside the solution.</summary>
    [Fact]
    public void ResolveWithinWorkspace_SiblingDirectoryWithASharedNamePrefix_IsRefused()
    {
        EditApplicationService.ResolveWithinWorkspace("../MySolution.Backup/Foo.cs", Root).ShouldBeNull();
    }

    /// <summary>No solution open means no workspace to be inside of - refuse rather than falling
    /// back to the process working directory, which is wherever the IDE happened to launch from.</summary>
    [Fact]
    public void ResolveWithinWorkspace_WithNoSolutionOpen_IsRefused()
    {
        EditApplicationService.ResolveWithinWorkspace("Foo.cs", null).ShouldBeNull();
    }

    [Fact]
    public void ResolveWithinWorkspace_TheSolutionDirectoryItself_Resolves()
    {
        EditApplicationService.ResolveWithinWorkspace(".", Root).ShouldBe(Path.GetFullPath(Root));
    }

    [Fact]
    public void TryComputeResultingContent_ReplaceRange_PreservesATrailingBlankLine()
    {
        var result = Compute(ReplaceRange(1, 1, "A"), "a\nb\n");

        result.ShouldBe("A\nb\n");
    }
}
