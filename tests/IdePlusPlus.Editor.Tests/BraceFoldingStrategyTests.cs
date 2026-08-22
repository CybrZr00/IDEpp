using IdePlusPlus.Editor.Folding;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

public class BraceFoldingStrategyTests
{
    [Fact]
    public void ComputeFoldRegions_SingleBlock_ReturnsOneRegionSpanningTheBraces()
    {
        const string text = "void Foo() {\n  return;\n}";

        var regions = BraceFoldingStrategy.ComputeFoldRegions(text);

        var region = regions.ShouldHaveSingleItem();
        region.Start.ShouldBe(text.IndexOf('{'));
        region.End.ShouldBe(text.IndexOf('}') + 1);
    }

    [Fact]
    public void ComputeFoldRegions_NestedBlocks_ReturnsBothRegionsSortedByStart()
    {
        const string text = "{ { } }";

        var regions = BraceFoldingStrategy.ComputeFoldRegions(text);

        regions.Count.ShouldBe(2);
        regions[0].Start.ShouldBeLessThan(regions[1].Start);
        regions[0].Start.ShouldBe(0);
        regions[0].End.ShouldBe(text.Length);
    }

    [Fact]
    public void ComputeFoldRegions_EmptyBraces_StillProducesARegionSpanningBoth()
    {
        // The strategy folds purely on offset positions, not on whether there's meaningful
        // content between the braces - "{}" still yields a (technically foldable) region.
        const string text = "{}";

        var regions = BraceFoldingStrategy.ComputeFoldRegions(text);

        var region = regions.ShouldHaveSingleItem();
        region.ShouldBe((0, 2));
    }

    [Fact]
    public void ComputeFoldRegions_UnmatchedClosingBrace_IsIgnored()
    {
        const string text = "} { }";

        var regions = BraceFoldingStrategy.ComputeFoldRegions(text);

        var region = regions.ShouldHaveSingleItem();
        region.Start.ShouldBe(2);
        region.End.ShouldBe(5);
    }

    [Fact]
    public void ComputeFoldRegions_NoBraces_ReturnsEmpty()
    {
        BraceFoldingStrategy.ComputeFoldRegions("plain text, no braces").ShouldBeEmpty();
    }
}
