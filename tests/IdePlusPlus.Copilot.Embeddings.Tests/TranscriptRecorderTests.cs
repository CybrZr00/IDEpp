using IdePlusPlus.Copilot.Memory;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

public sealed class TranscriptRecorderTests : IDisposable
{
    private readonly string _solution = Path.Combine(Path.GetTempPath(), $"ideplusplus-recorder-{Guid.NewGuid():N}");
    private readonly TranscriptMemoryService _service;
    private readonly TranscriptStore _store = new();

    public TranscriptRecorderTests()
    {
        Directory.CreateDirectory(_solution);
        // No embedding model: these tests are about what is written, not how it is searched.
        _service = new TranscriptMemoryService(new BagOfWordsEmbedder { Available = false }, _store, TimeSpan.FromHours(1));
        _service.SetSolution(_solution);
    }

    public void Dispose() => Directory.Delete(_solution, recursive: true);

    private TranscriptRecorder Recorder(string source = "sub-agent 1") =>
        new(_service, _solution, _service.StartConversation()!, source);

    [Fact]
    public void ATaskAndAModelTurn_AreRecordedInOrder_UnderTheSourceLabel()
    {
        var recorder = Recorder("sub-agent 2");

        recorder.RecordTask("Find every caller of Foo.");
        recorder.RecordModelTurn("Check the references first.", "Foo has three callers.");

        var entries = _store.LoadAll(_solution);
        entries.Select(e => e.Kind).ShouldBe([TranscriptKinds.User, TranscriptKinds.Thought, TranscriptKinds.Assistant]);
        entries.Select(e => e.Text).ShouldBe(["Find every caller of Foo.", "Check the references first.", "Foo has three callers."]);
        entries.ShouldAllBe(e => e.Source == "sub-agent 2");
    }

    [Fact]
    public void WithSource_KeepsTheConversation_ButChangesTheLabel()
    {
        var first = Recorder("ultra lead");
        var worker = first.WithSource("ultra worker alice");

        first.RecordModelTurn(null, "Plan written.");
        worker.RecordModelTurn(null, "Task claimed.");

        var entries = _store.LoadAll(_solution);
        entries.Select(e => e.Source).ShouldBe(["ultra lead", "ultra worker alice"]);
        entries.Select(e => e.ConversationId).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void BlankTextAndMissingReasoning_RecordNothing()
    {
        var recorder = Recorder();

        recorder.RecordModelTurn(null, "  ");
        recorder.RecordModelTurn("", "");
        recorder.RecordTask("");

        _store.LoadAll(_solution).ShouldBeEmpty();
    }

    [Fact]
    public void AfterTheSolutionChanges_ALateEntryIsDroppedRatherThanWrittenToTheNextSolution()
    {
        var recorder = Recorder();
        recorder.RecordModelTurn(null, "Before the switch.");

        var other = Path.Combine(Path.GetTempPath(), $"ideplusplus-recorder-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(other);
        try
        {
            _service.SetSolution(other);
            recorder.RecordModelTurn("A late thought.", "A late reply.");

            _store.LoadAll(other).ShouldBeEmpty();
            _store.LoadAll(_solution).Select(e => e.Text).ShouldBe(["Before the switch."]);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void StartRecorder_GivesAScheduledRunAConversationOfItsOwn_ThatAPaneNeverResumes()
    {
        var chat = _service.StartConversation()!;
        _service.Record(chat, TranscriptKinds.User, "A chat message.");

        var recorder = _service.StartRecorder(_solution, "scheduled: nightly review").ShouldNotBeNull();
        recorder.RecordTask("Review yesterday's commits.");
        recorder.RecordModelTurn(null, "Nothing alarming.");

        var scheduled = _store.LoadAll(_solution).Where(e => e.Source == "scheduled: nightly review").ToList();
        scheduled.Count.ShouldBe(2);
        scheduled.Select(e => e.ConversationId).Distinct().ShouldHaveSingleItem().ShouldNotBe(chat);
        _service.LatestConversationId().ShouldBe(chat);
    }

    [Fact]
    public void StartRecorder_ForASolutionThatIsNoLongerOpen_IsNullAndCreatesNothing()
    {
        var other = Path.Combine(Path.GetTempPath(), $"ideplusplus-recorder-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(other);
        try
        {
            _service.StartRecorder(other, "scheduled: stale").ShouldBeNull();

            Directory.Exists(TranscriptStore.TranscriptsDirectory(other)).ShouldBeFalse();
            Directory.Exists(TranscriptStore.TranscriptsDirectory(_solution)).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void ARecorderForAnotherConversation_StillWritesToItsOwn_AfterTheCurrentOneMovedOn()
    {
        var recorder = Recorder();
        var later = _service.StartConversation()!;
        _service.Record(later, TranscriptKinds.User, "A newer conversation.");

        recorder.RecordModelTurn(null, "Still the first conversation's agent.");

        var byConversation = _store.LoadAll(_solution).GroupBy(e => e.ConversationId).ToList();
        byConversation.Count.ShouldBe(2);
        byConversation.Single(g => g.Key == later).Select(e => e.Text).ShouldBe(["A newer conversation."]);
    }
}
