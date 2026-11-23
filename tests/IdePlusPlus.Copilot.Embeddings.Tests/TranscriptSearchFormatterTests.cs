using IdePlusPlus.Copilot.Memory;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

public sealed class TranscriptSearchFormatterTests
{
    private static TranscriptSearchHit Hit(string conversation, int sequence, string kind, string excerpt) =>
        new(new TranscriptEntry(conversation, sequence, new DateTimeOffset(2026, 11, 14, 16, 2, 0, TimeSpan.Zero), kind, excerpt), excerpt, 1.0);

    [Fact]
    public void NoHits_SaysSo_AndQuotesTheQuestion()
    {
        var text = TranscriptSearchFormatter.Format("the cache bug", new TranscriptSearchResult([], SemanticAvailable: true, UnembeddedPassages: 0), null);

        text.ShouldBe("No matches in the saved conversation transcripts for \"the cache bug\".");
    }

    [Fact]
    public void Hits_AreNumbered_LabelledByKind_AndSaySomethingAboutWhereTheyCameFrom()
    {
        var result = new TranscriptSearchResult(
        [
            Hit("2026-11-10_090000", 4, TranscriptKinds.Thought, "Reasoning text."),
            Hit("2026-11-14_152233", 7, TranscriptKinds.User, "User text."),
            Hit("2026-11-14_152233", 8, TranscriptKinds.Assistant, "Assistant text."),
        ],
        SemanticAvailable: true,
        UnembeddedPassages: 0);

        var text = TranscriptSearchFormatter.Format("q", result, currentConversationId: "2026-11-14_152233");

        text.ShouldStartWith("3 match(es) in the saved conversation transcripts, closest first.");
        text.ShouldContain("[1] ");
        text.ShouldContain("assistant thought | conversation 2026-11-10_090000 | entry 4\nReasoning text.");
        text.ShouldContain("user message | this conversation | entry 7\nUser text.");
        text.ShouldContain("assistant message | this conversation | entry 8\nAssistant text.");
        text.ShouldNotContain("Matching by meaning is not available");
    }

    [Fact]
    public void WithoutSemanticSearch_TheModelIsToldTheseAreOnlyKeywordMatches()
    {
        var result = new TranscriptSearchResult([Hit("c", 1, TranscriptKinds.User, "x")], SemanticAvailable: false, UnembeddedPassages: 4);

        var text = TranscriptSearchFormatter.Format("q", result, null);

        text.ShouldContain("keyword matches only");
    }

    [Fact]
    public void WithSemanticSearchButABacklog_TheModelIsToldHowMuchWasKeywordOnly()
    {
        var result = new TranscriptSearchResult([Hit("c", 1, TranscriptKinds.User, "x")], SemanticAvailable: true, UnembeddedPassages: 3);

        var text = TranscriptSearchFormatter.Format("q", result, null);

        text.ShouldContain("3 stored passage(s) are not embedded yet");
        text.ShouldNotContain("keyword matches only");
    }
}
