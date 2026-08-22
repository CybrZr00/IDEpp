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
}
