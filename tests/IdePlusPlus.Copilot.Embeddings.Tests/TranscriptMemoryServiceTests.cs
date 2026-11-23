using IdePlusPlus.Copilot.Memory;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

public sealed class TranscriptMemoryServiceTests : IDisposable
{
    private readonly string _solution = Path.Combine(Path.GetTempPath(), $"ideplusplus-transcript-memory-{Guid.NewGuid():N}");
    private readonly BagOfWordsEmbedder _embedder = new();
    private readonly TranscriptMemoryService _service;

    public TranscriptMemoryServiceTests()
    {
        Directory.CreateDirectory(_solution);
        _service = NewService();
        _service.SetSolution(_solution);
    }

    public void Dispose() => Directory.Delete(_solution, recursive: true);

    /// <summary>A fresh service over the same folder - what the IDE does on restart.</summary>
    private TranscriptMemoryService NewService(TimeSpan? retryDelay = null) =>
        new(_embedder, new TranscriptStore(), retryDelay ?? TimeSpan.FromHours(1));

    private static Task<TranscriptSearchResult> Search(
        TranscriptMemoryService service, string query, TranscriptKindFilter kinds = TranscriptKindFilter.Any, int limit = 5) =>
        service.SearchAsync(query, kinds, limit, CancellationToken.None);

    private string Conversation() => _service.StartConversation().ShouldNotBeNull();

    [Fact]
    public async Task AnEntryIsFoundByKeyword_EvenWithNoEmbeddingModel_AndTheResultSaysSo()
    {
        _embedder.Available = false;
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "The restore step runs twice because of the Directory.Build.props import.").ShouldBeTrue();
        _service.Record(id, TranscriptKinds.Assistant, "Chocolate cake needs three eggs.").ShouldBeTrue();
        await _service.WhenIdleAsync();

        var result = await Search(_service, "why does restore run twice");

        result.SemanticAvailable.ShouldBeFalse();
        result.UnembeddedPassages.ShouldBe(2);
        var hit = result.Hits.ShouldHaveSingleItem();
        hit.Excerpt.ShouldContain("Directory.Build.props");
        _embedder.QueryCalls.ShouldBe(0, "there was nothing to compare a question with");
    }

    [Fact]
    public async Task EntriesRecordedBeforeARestart_AreStillThereAfterIt()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.User, "Please rename PersistChatHistory to SaveConversation.");
        _service.Record(id, TranscriptKinds.Thought, "A rename touches the view model and its tests.");
        await _service.WhenIdleAsync();

        var afterRestart = NewService();
        afterRestart.SetSolution(_solution);

        afterRestart.EntryCount.ShouldBe(2);
        var result = await Search(afterRestart, "PersistChatHistory");
        result.Hits[0].Entry.Kind.ShouldBe(TranscriptKinds.User);
        result.Hits[0].Excerpt.ShouldContain("PersistChatHistory");
    }

    [Fact]
    public async Task SearchingByMeaning_RanksTheEntryAboutTheTopicFirst()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "Chocolate cake needs three eggs and some flour.");
        _service.Record(id, TranscriptKinds.Assistant, "The database connection pool times out when every connection is busy.");
        _service.Record(id, TranscriptKinds.Assistant, "Rename the button and update the xaml.");
        await _service.WhenIdleAsync();

        // Shares no word with the keyword rankings' terms except "connection": similarity does the work.
        var result = await Search(_service, "connection pool timeout");

        result.SemanticAvailable.ShouldBeTrue();
        result.UnembeddedPassages.ShouldBe(0);
        result.Hits[0].Entry.Text.ShouldContain("database connection pool");
    }

    [Fact]
    public async Task AnEntryBothRankingsLike_BeatsOneOnlyOneOfThemLikes()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "Deployment uses the staging slot first.");
        _service.Record(id, TranscriptKinds.Assistant, "The staging deployment slot swap failed on the warmup check.");
        await _service.WhenIdleAsync();

        var result = await Search(_service, "staging deployment slot warmup");

        result.Hits[0].Entry.Text.ShouldContain("warmup check");
    }

    [Fact]
    public async Task TheBacklogRecordedWhileNoModelWasThere_IsEmbeddedOnceOneIs()
    {
        _embedder.Available = false;
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "First note about indexing.");
        _service.Record(id, TranscriptKinds.Assistant, "Second note about indexing.");
        await _service.WhenIdleAsync();
        _service.PendingPassageCount.ShouldBe(2);

        _embedder.Available = true;
        _service.ResumeEmbedding();
        await _service.WhenIdleAsync();

        _service.PendingPassageCount.ShouldBe(0);
        (await Search(_service, "indexing notes")).SemanticAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task AfterAFailure_ItWaitsOutTheRetryDelay_RatherThanTryingOnEveryMessage()
    {
        _embedder.Available = false;
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "one");
        await _service.WhenIdleAsync();
        var callsAfterFirstFailure = _embedder.PassageCalls;

        _embedder.Available = true;
        _service.Record(id, TranscriptKinds.Assistant, "two");
        _service.Record(id, TranscriptKinds.Assistant, "three");
        await _service.WhenIdleAsync();

        _embedder.PassageCalls.ShouldBe(callsAfterFirstFailure, "the retry delay had not passed");
        _service.PendingPassageCount.ShouldBe(3);
    }

    [Fact]
    public async Task SavedVectors_AreReloadedAfterARestart_NotComputedAgain()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "Alpha beta gamma.");
        _service.Record(id, TranscriptKinds.Assistant, "Delta epsilon zeta.");
        await _service.WhenIdleAsync();
        _embedder.PassageCalls.ShouldBe(2);

        var afterRestart = NewService();
        afterRestart.SetSolution(_solution);
        await afterRestart.WhenIdleAsync();

        _embedder.PassageCalls.ShouldBe(2, "the vectors were on disk");
        afterRestart.PendingPassageCount.ShouldBe(0);
        (await Search(afterRestart, "delta epsilon")).SemanticAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task ASwitchOfEmbeddingModel_EmbedsAgain_AndSwitchingBackReusesTheFirstModelsVectors()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "Alpha beta gamma.");
        await _service.WhenIdleAsync();
        _embedder.PassageCalls.ShouldBe(1);

        _embedder.ModelId = "fake:b";
        (await Search(_service, "alpha")).UnembeddedPassages.ShouldBe(1, "model b has no vector for it yet");
        _service.ResumeEmbedding();
        await _service.WhenIdleAsync();
        _embedder.PassageCalls.ShouldBe(2);

        _embedder.ModelId = "fake:a";
        var result = await Search(_service, "alpha");

        result.UnembeddedPassages.ShouldBe(0, "model a's vector was saved");
        _embedder.PassageCalls.ShouldBe(2);
    }

    [Fact]
    public async Task OnlyTheFirstPassagesOfAVeryLongEntry_AreEmbedded_ButAllOfItIsSearchable()
    {
        var id = Conversation();
        var paragraphs = Enumerable.Range(1, 40).Select(i => $"paragraph number {i} " + new string('x', 1000));
        _service.Record(id, TranscriptKinds.Thought, string.Join("\n\n", paragraphs));
        await _service.WhenIdleAsync();

        _embedder.PassageCalls.ShouldBe(TranscriptMemoryService.MaxEmbeddedPassagesPerEntry);
        _service.PendingPassageCount.ShouldBe(0);

        var result = await Search(_service, "paragraph 40");
        result.Hits.ShouldHaveSingleItem().Excerpt.ShouldContain("paragraph number 40");
    }

    [Fact]
    public async Task AnEntryWithSeveralMatchingPassages_IsOneHit()
    {
        var id = Conversation();
        var paragraphs = Enumerable.Range(1, 6).Select(i => $"The cache invalidation bug, take {i}. " + new string('y', 900));
        _service.Record(id, TranscriptKinds.Thought, string.Join("\n\n", paragraphs));
        await _service.WhenIdleAsync();

        var result = await Search(_service, "cache invalidation bug");

        result.Hits.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TheKindFilter_SeparatesThoughtsFromMessages()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.User, "Should we cache the schema lookup?");
        _service.Record(id, TranscriptKinds.Thought, "Caching the schema lookup is risky because migrations change it.");
        _service.Record(id, TranscriptKinds.Assistant, "I would not cache the schema lookup.");
        await _service.WhenIdleAsync();

        var thoughts = await Search(_service, "cache schema lookup", TranscriptKindFilter.Thoughts);
        var messages = await Search(_service, "cache schema lookup", TranscriptKindFilter.Messages);
        var any = await Search(_service, "cache schema lookup", TranscriptKindFilter.Any);

        thoughts.Hits.ShouldHaveSingleItem().Entry.Kind.ShouldBe(TranscriptKinds.Thought);
        messages.Hits.Select(h => h.Entry.Kind).ShouldBe([TranscriptKinds.User, TranscriptKinds.Assistant], ignoreOrder: true);
        any.Hits.Count.ShouldBe(3);
    }

    [Fact]
    public async Task TheLimit_CapsTheHits()
    {
        var id = Conversation();
        for (var i = 0; i < 8; i++)
        {
            _service.Record(id, TranscriptKinds.Assistant, $"Note {i} about the widget.");
        }

        await _service.WhenIdleAsync();

        (await Search(_service, "widget", limit: 3)).Hits.Count.ShouldBe(3);
        (await Search(_service, "widget", limit: 500)).Hits.Count.ShouldBe(8);
    }

    [Fact]
    public async Task AnotherSolutionsTranscripts_AreNotSearchable_AndNothingIsRecordedWithNoSolution()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.User, "A secret about the first solution.");
        await _service.WhenIdleAsync();

        var other = Path.Combine(Path.GetTempPath(), $"ideplusplus-transcript-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(other);
        try
        {
            _service.SetSolution(other);
            (await Search(_service, "secret")).Hits.ShouldBeEmpty();
            _service.EntryCount.ShouldBe(0);

            _service.SetSolution(null);
            _service.StartConversation().ShouldBeNull();
            _service.Record(id, TranscriptKinds.User, "nowhere to go").ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void BlankText_RecordsNothing_AndABadConversationIdIsAFailureNotAnException()
    {
        var id = Conversation();

        _service.Record(id, TranscriptKinds.User, "   \n ").ShouldBeTrue();
        _service.EntryCount.ShouldBe(0);

        _service.Record(@"..\..\escape", TranscriptKinds.User, "text").ShouldBeFalse();
        _service.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task ACorruptVectorsFile_CostsOnlyTheEmbeddingWork_NotTheEntries()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "Alpha beta gamma.");
        await _service.WhenIdleAsync();
        File.WriteAllText(TranscriptStore.VectorsFilePath(_solution), "not json\n{\"e\":\"x\"}\n{\"e\":\"" + id + "#1\",\"i\":0,\"m\":\"fake:a\",\"v\":\"!!!\"}\n");

        var afterRestart = NewService();
        afterRestart.SetSolution(_solution);
        await afterRestart.WhenIdleAsync();

        afterRestart.EntryCount.ShouldBe(1);
        afterRestart.PendingPassageCount.ShouldBe(0, "re-embedded from the entry's own text");
        (await Search(afterRestart, "alpha beta")).Hits.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ASearchWithNoWordsInIt_FindsNothingRatherThanEverything()
    {
        var id = Conversation();
        _service.Record(id, TranscriptKinds.Assistant, "Something was said.");
        await _service.WhenIdleAsync();
        _embedder.Available = false;

        (await Search(_service, "")).Hits.ShouldBeEmpty();
        (await Search(_service, "of it to")).Hits.ShouldBeEmpty();
    }
}
