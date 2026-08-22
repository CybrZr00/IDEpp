using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

public class CodeChunkerTests
{
    [Fact]
    public void ChunkFile_ShortFile_ReturnsSingleChunkSpanningWholeFile()
    {
        var content = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}"));

        var chunks = CodeChunker.ChunkFile("Foo.cs", content).ToList();

        chunks.Count.ShouldBe(1);
        chunks[0].StartLine.ShouldBe(1);
        chunks[0].EndLine.ShouldBe(20);
        chunks[0].Text.ShouldBe(content);
    }

    [Fact]
    public void ChunkFile_EmptyFile_ReturnsNoChunks()
    {
        CodeChunker.ChunkFile("Empty.cs", "").ShouldBeEmpty();
    }

    [Fact]
    public void ChunkFile_LongFile_SplitsIntoOverlappingChunks()
    {
        var content = string.Join('\n', Enumerable.Range(1, 250).Select(i => $"line {i}"));

        var chunks = CodeChunker.ChunkFile("Foo.cs", content).ToList();

        chunks.Count.ShouldBeGreaterThan(1);
        chunks[0].StartLine.ShouldBe(1);
        chunks[0].EndLine.ShouldBe(120);
        // Consecutive chunks overlap by 10 lines so search context isn't lost at a chunk boundary.
        chunks[1].StartLine.ShouldBe(chunks[0].EndLine - 10 + 1);
        chunks[^1].EndLine.ShouldBe(250);
    }

    [Fact]
    public void ChunkFile_LongFile_CoversEveryLineWithoutGaps()
    {
        var content = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"line {i}"));

        var chunks = CodeChunker.ChunkFile("Foo.cs", content).ToList();

        for (var i = 1; i < chunks.Count; i++)
        {
            chunks[i].StartLine.ShouldBeLessThanOrEqualTo(chunks[i - 1].EndLine + 1);
        }
    }
}
