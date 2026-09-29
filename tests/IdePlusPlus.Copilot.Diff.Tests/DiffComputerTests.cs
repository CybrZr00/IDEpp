using IdePlusPlus.Editor.Diff;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Diff.Tests;

public class DiffComputerTests
{
    [Fact]
    public void Compute_MarksAnAddedLineWithinUnchangedContext()
    {
        var lines = DiffComputer.Compute("a\nc", "a\nb\nc");

        lines.Select(l => l.Kind).ShouldBe([DiffLineKind.Unchanged, DiffLineKind.Added, DiffLineKind.Unchanged]);
        lines[1].Text.ShouldBe("b");
    }

    [Fact]
    public void Compute_MarksARemovedLineWithinUnchangedContext()
    {
        var lines = DiffComputer.Compute("a\nb\nc", "a\nc");

        lines.Select(l => l.Kind).ShouldBe([DiffLineKind.Unchanged, DiffLineKind.Removed, DiffLineKind.Unchanged]);
        lines[1].Text.ShouldBe("b");
    }

    [Fact]
    public void Compute_OnIdenticalText_ProducesOnlyUnchangedLines()
    {
        var lines = DiffComputer.Compute("a\nb\nc", "a\nb\nc");

        lines.ShouldAllBe(l => l.Kind == DiffLineKind.Unchanged);
    }

    [Fact]
    public void Compute_ModifiedLine_PairsRemovedAndAddedWithWordLevelSegments()
    {
        var lines = DiffComputer.Compute("hello world\nfoo bar", "hello there\nfoo bar");

        lines.Select(l => l.Kind).ShouldBe([DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Unchanged]);

        var removed = lines[0];
        removed.Text.ShouldBe("hello world");
        removed.HasSegments.ShouldBeTrue();
        removed.Segments!.Select(s => (s.Text, s.IsHighlighted)).ShouldBe([("hello", false), (" ", false), ("world", true)]);

        var added = lines[1];
        added.Text.ShouldBe("hello there");
        added.HasSegments.ShouldBeTrue();
        added.Segments!.Select(s => (s.Text, s.IsHighlighted)).ShouldBe([("hello", false), (" ", false), ("there", true)]);
    }

    [Fact]
    public void Compute_WhollyAddedLine_HasNoSegments()
    {
        var lines = DiffComputer.Compute("a\nc", "a\nb\nc");

        lines[1].HasSegments.ShouldBeFalse();
        lines[1].Segments.ShouldBeNull();
    }

    [Fact]
    public void Compute_UnchangedLine_HasNoSegments()
    {
        var lines = DiffComputer.Compute("a\nb", "a\nb");

        lines.ShouldAllBe(l => !l.HasSegments);
    }
}
