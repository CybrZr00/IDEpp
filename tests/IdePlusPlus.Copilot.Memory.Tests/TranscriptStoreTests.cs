using System.Text;
using IdePlusPlus.Copilot.Memory;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Memory.Tests;

public sealed class TranscriptStoreTests : IDisposable
{
    private readonly string _solutionDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-transcripts-{Guid.NewGuid()}");
    private readonly TranscriptStore _store = new();

    public TranscriptStoreTests() => Directory.CreateDirectory(_solutionDirectory);

    public void Dispose() => Directory.Delete(_solutionDirectory, recursive: true);

    private string FileOf(string conversationId) =>
        Path.Combine(TranscriptStore.TranscriptsDirectory(_solutionDirectory), conversationId + ".jsonl");

    [Fact]
    public void LoadAll_WithNothingSaved_ReturnsEmpty()
    {
        _store.LoadAll(_solutionDirectory).ShouldBeEmpty();
        _store.LatestConversationId(_solutionDirectory).ShouldBeNull();
    }

    [Fact]
    public void Append_ThenLoad_RoundTripsKindTextAndSourceInOrder()
    {
        var id = _store.StartConversation(_solutionDirectory);

        _store.Append(_solutionDirectory, id, TranscriptKinds.User, "Why is the build slow?");
        _store.Append(_solutionDirectory, id, TranscriptKinds.Thought, "The user wants a cause, check the targets.");
        _store.Append(_solutionDirectory, id, TranscriptKinds.Assistant, "It restores on every run.", source: "reply");

        var loaded = _store.LoadConversation(_solutionDirectory, id);

        loaded.Select(e => e.Sequence).ShouldBe([1, 2, 3]);
        loaded.Select(e => e.Kind).ShouldBe([TranscriptKinds.User, TranscriptKinds.Thought, TranscriptKinds.Assistant]);
        loaded[0].Text.ShouldBe("Why is the build slow?");
        loaded[2].Source.ShouldBe("reply");
        loaded.ShouldAllBe(e => e.ConversationId == id);
    }

    [Fact]
    public void Append_KeepsCodeAndNonAsciiTextExactly_AndStoresItReadably()
    {
        var id = _store.StartConversation(_solutionDirectory);
        const string text = "if (a < b && c > d) { return \"x\"; }\n\tcafé 中文 😀";

        _store.Append(_solutionDirectory, id, TranscriptKinds.Assistant, text);

        _store.LoadConversation(_solutionDirectory, id).ShouldHaveSingleItem().Text.ShouldBe(text);
        // Not escaped to < and friends: the file is meant to be greppable.
        File.ReadAllText(FileOf(id)).ShouldContain("a < b && c > d");
    }

    [Fact]
    public void StartConversation_TwiceInTheSameSecond_GivesDistinctIds()
    {
        var moment = new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);

        var first = _store.StartConversation(_solutionDirectory, moment);
        var second = _store.StartConversation(_solutionDirectory, moment);
        var third = _store.StartConversation(_solutionDirectory, moment);

        new[] { first, second, third }.Distinct().Count().ShouldBe(3);
        File.Exists(FileOf(first)).ShouldBeTrue();
        File.Exists(FileOf(second)).ShouldBeTrue();
    }

    [Fact]
    public void LoadAll_ReturnsOlderConversationsFirst_AndEachOneInOrder()
    {
        var older = _store.StartConversation(_solutionDirectory, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var newer = _store.StartConversation(_solutionDirectory, new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        // Interleaved on purpose: the files, not the order of the calls, decide.
        _store.Append(_solutionDirectory, newer, TranscriptKinds.User, "second conversation, first message");
        _store.Append(_solutionDirectory, older, TranscriptKinds.User, "first conversation, first message");
        _store.Append(_solutionDirectory, older, TranscriptKinds.Assistant, "first conversation, second message");

        var all = _store.LoadAll(_solutionDirectory);

        all.Select(e => e.Text).ShouldBe(
        [
            "first conversation, first message",
            "first conversation, second message",
            "second conversation, first message",
        ]);
        _store.LatestConversationId(_solutionDirectory).ShouldBe(newer);
    }

    [Fact]
    public void Sequence_ContinuesWhereTheFileLeftOff_AfterARestart()
    {
        var id = _store.StartConversation(_solutionDirectory);
        _store.Append(_solutionDirectory, id, TranscriptKinds.User, "one");
        _store.Append(_solutionDirectory, id, TranscriptKinds.Assistant, "two");

        var afterRestart = new TranscriptStore();
        var entry = afterRestart.Append(_solutionDirectory, id, TranscriptKinds.User, "three");

        entry.Sequence.ShouldBe(3);
        entry.Id.ShouldBe($"{id}#3");
        afterRestart.LoadConversation(_solutionDirectory, id).Select(e => e.Text).ShouldBe(["one", "two", "three"]);
    }

    [Fact]
    public void ALineCutShortByACrash_IsSkipped_AndTheNextEntryIsNotWeldedOntoIt()
    {
        var id = _store.StartConversation(_solutionDirectory);
        _store.Append(_solutionDirectory, id, TranscriptKinds.User, "before");
        // What a crash mid-write leaves: half an entry and no newline.
        File.AppendAllText(FileOf(id), "{\"ConversationId\":\"" + id + "\",\"Sequence\":2,\"Kind\":\"assis", new UTF8Encoding(false));

        var afterRestart = new TranscriptStore();
        afterRestart.Append(_solutionDirectory, id, TranscriptKinds.User, "after");

        afterRestart.LoadConversation(_solutionDirectory, id).Select(e => e.Text).ShouldBe(["before", "after"]);
    }

    [Fact]
    public void LoadConversation_SkipsGarbageLinesWithoutLosingTheOnesAroundThem()
    {
        var id = _store.StartConversation(_solutionDirectory);
        _store.Append(_solutionDirectory, id, TranscriptKinds.User, "kept one");
        File.AppendAllText(FileOf(id), "this is not json\n\n{\"unrelated\":true}\n", new UTF8Encoding(false));
        _store.Append(_solutionDirectory, id, TranscriptKinds.User, "kept two");

        _store.LoadConversation(_solutionDirectory, id).Select(e => e.Text).ShouldBe(["kept one", "kept two"]);
    }

    [Fact]
    public void Append_RefusesAConversationIdThatCouldEscapeTheTranscriptsFolder()
    {
        Should.Throw<ArgumentException>(() =>
            _store.Append(_solutionDirectory, @"..\..\outside", TranscriptKinds.User, "x"));
        Should.Throw<ArgumentException>(() =>
            _store.Append(_solutionDirectory, "", TranscriptKinds.User, "x"));
        _store.LoadConversation(_solutionDirectory, @"..\chat-history").ShouldBeEmpty();
    }

    [Fact]
    public void TheVectorsSidecar_IsNeverMistakenForAConversation()
    {
        var id = _store.StartConversation(_solutionDirectory);
        _store.Append(_solutionDirectory, id, TranscriptKinds.User, "hello");
        File.WriteAllText(TranscriptStore.VectorsFilePath(_solutionDirectory), "{\"e\":\"x\"}\n");

        _store.LatestConversationId(_solutionDirectory).ShouldBe(id);
        _store.LoadAll(_solutionDirectory).ShouldHaveSingleItem();
    }

    [Fact]
    public void ConcurrentAppends_AllLandWithDistinctSequenceNumbers()
    {
        var id = _store.StartConversation(_solutionDirectory);

        Parallel.For(0, 50, i => _store.Append(_solutionDirectory, id, TranscriptKinds.User, $"message {i}"));

        var loaded = _store.LoadConversation(_solutionDirectory, id);
        loaded.Count.ShouldBe(50);
        loaded.Select(e => e.Sequence).Distinct().Count().ShouldBe(50);
        loaded.Select(e => e.Sequence).ShouldBe(Enumerable.Range(1, 50));
    }
}
