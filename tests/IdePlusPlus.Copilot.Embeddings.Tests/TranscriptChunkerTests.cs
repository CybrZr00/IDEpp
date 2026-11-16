using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

public sealed class TranscriptChunkerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void BlankText_GivesNoChunks(string text)
    {
        TranscriptChunker.Chunk(text).ShouldBeEmpty();
    }

    [Fact]
    public void ShortText_IsOneChunk_TrimmedAndWithUnixLineEndings()
    {
        TranscriptChunker.Chunk("  hello\r\nworld  ").ShouldHaveSingleItem().ShouldBe("hello\nworld");
    }

    [Fact]
    public void Paragraphs_ArePackedWholeUpToTheBudget()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"paragraph {i} " + new string('x', 40)));

        var chunks = TranscriptChunker.Chunk(text, maxChars: 120);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(c => c.Length <= 120);
        // No paragraph was cut in half: every chunk is a whole number of them.
        chunks.ShouldAllBe(c => c.Split("\n\n").All(p => p.StartsWith("paragraph ", StringComparison.Ordinal) && p.EndsWith("xxxx", StringComparison.Ordinal)));
        string.Join("\n\n", chunks).ShouldBe(text);
    }

    [Fact]
    public void OneHugeParagraph_IsCutAtSpaces_AndNothingIsLost()
    {
        var words = Enumerable.Range(1, 400).Select(i => $"word{i}").ToList();
        var text = string.Join(' ', words);

        var chunks = TranscriptChunker.Chunk(text, maxChars: 200);

        chunks.Count.ShouldBeGreaterThan(5);
        chunks.ShouldAllBe(c => c.Length <= 200);
        // Cut between words, never through one.
        chunks.SelectMany(c => c.Split(' ')).ShouldBe(words);
    }

    [Fact]
    public void AnUnbrokenRun_IsStillCutToTheBudget()
    {
        var chunks = TranscriptChunker.Chunk(new string('a', 1000), maxChars: 300);

        chunks.Count.ShouldBe(4);
        chunks.ShouldAllBe(c => c.Length <= 300);
        string.Concat(chunks).Length.ShouldBe(1000);
    }

    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        // Emoji are two UTF-16 chars each; a budget of 5 would land between the halves of one.
        var text = string.Concat(Enumerable.Repeat("😀", 20));

        var chunks = TranscriptChunker.Chunk(text, maxChars: 5);

        chunks.ShouldAllBe(c => c.Length > 0 && !char.IsHighSurrogate(c.Last()) && !char.IsLowSurrogate(c.First()));
        string.Concat(chunks).ShouldBe(text);
    }
}
