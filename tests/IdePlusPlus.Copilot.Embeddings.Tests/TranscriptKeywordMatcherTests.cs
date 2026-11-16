using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

public sealed class TranscriptKeywordMatcherTests
{
    [Fact]
    public void Terms_KeepIdentifiersWhole_AndDropShortAndCommonWords()
    {
        TranscriptKeywordMatcher.Terms("Why does PersistChatHistory run in the New_Chat flow?")
            .ShouldBe(["PersistChatHistory", "run", "New_Chat", "flow"]);
    }

    [Fact]
    public void Terms_AreDistinct_IgnoringCase()
    {
        TranscriptKeywordMatcher.Terms("Build build BUILD errors").ShouldBe(["Build", "errors"]);
    }

    [Fact]
    public void Terms_OfAQuestionMadeOnlyOfCommonWords_AreEmpty()
    {
        TranscriptKeywordMatcher.Terms("what was it about?").ShouldBeEmpty();
    }

    [Fact]
    public void Score_PrefersMoreDifferentTerms_OverMoreRepeats()
    {
        string[] terms = ["cache", "schema"];

        var both = TranscriptKeywordMatcher.Score("the schema cache", terms);
        var repeated = TranscriptKeywordMatcher.Score("cache cache cache cache cache cache", terms);

        both.ShouldBeGreaterThan(repeated);
    }

    [Fact]
    public void Terms_KeepShortOnesThatMeanSomething_LikeNumbersAndAbbreviations()
    {
        TranscriptKeywordMatcher.Terms("is the db at v2 or 40?").ShouldBe(["db", "v2", "40"]);
    }

    [Fact]
    public void AShortTerm_MatchesAsAWholeWordOnly_ALongOneMatchesInsideWords()
    {
        TranscriptKeywordMatcher.Score("the db is down", ["db"]).ShouldBeGreaterThan(0);
        TranscriptKeywordMatcher.Score("the feedback loop", ["db"]).ShouldBe(0);
        TranscriptKeywordMatcher.Score("see PersistChatHistoryAsync", ["PersistChat"]).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Score_IsCaseInsensitive_AndZeroWhenNothingMatches()
    {
        TranscriptKeywordMatcher.Score("The SCHEMA changed", ["schema"]).ShouldBeGreaterThan(0);
        TranscriptKeywordMatcher.Score("nothing relevant here", ["schema"]).ShouldBe(0);
    }
}
