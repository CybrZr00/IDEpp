using IdePlusPlus.Copilot.Memory;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Memory.Tests;

public sealed class ChatHistoryStoreTests : IDisposable
{
    private readonly string _solutionDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-chat-{Guid.NewGuid()}");
    private readonly ChatHistoryStore _store = new();

    public ChatHistoryStoreTests() => Directory.CreateDirectory(_solutionDirectory);

    public void Dispose() => Directory.Delete(_solutionDirectory, recursive: true);

    [Fact]
    public void Load_NoSavedHistory_ReturnsEmpty()
    {
        _store.Load(_solutionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsMessagesInOrder()
    {
        var messages = new[]
        {
            new StoredChatMessage("User", "Hello"),
            new StoredChatMessage("Assistant", "Hi there"),
        };

        _store.Save(_solutionDirectory, messages);
        var loaded = _store.Load(_solutionDirectory);

        loaded.Count.ShouldBe(2);
        loaded[0].ShouldBe(messages[0]);
        loaded[1].ShouldBe(messages[1]);
    }

    [Fact]
    public void Save_OverwritesPreviouslySavedHistory()
    {
        _store.Save(_solutionDirectory, [new StoredChatMessage("User", "first")]);

        _store.Save(_solutionDirectory, [new StoredChatMessage("User", "second")]);

        _store.Load(_solutionDirectory).ShouldHaveSingleItem().Text.ShouldBe("second");
    }

    [Fact]
    public void Save_CreatesTheCopilotDirectoryIfMissing()
    {
        _store.Save(_solutionDirectory, [new StoredChatMessage("User", "hi")]);

        Directory.Exists(ProjectMemoryStore.MemoryDirectory(_solutionDirectory)).ShouldBeTrue();
    }

    [Fact]
    public void Save_CalledRepeatedlyMidConversation_LeavesTheLatestSnapshotReadable()
    {
        // The pane now checkpoints eagerly (per user message, per tool-loop iteration) rather than
        // only at the end of a turn, so a partially-complete conversation has to reload cleanly.
        _store.Save(_solutionDirectory, [new StoredChatMessage("User", "question")]);
        _store.Save(_solutionDirectory, [new StoredChatMessage("User", "question"), new StoredChatMessage("Assistant", "partial ans")]);

        var loaded = _store.Load(_solutionDirectory);

        loaded.Count.ShouldBe(2);
        loaded[1].Text.ShouldBe("partial ans");
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(ProjectMemoryStore.MemoryDirectory(_solutionDirectory));
        File.WriteAllText(Path.Combine(ProjectMemoryStore.MemoryDirectory(_solutionDirectory), "chat-history.json"), "{ not valid json");

        _store.Load(_solutionDirectory).ShouldBeEmpty();
    }

    /// <summary>ToolCallId/ToolCalls were added later so tool calls/results could round-trip too - a
    /// history file written before that only ever has "Role" and "Text", and must still deserialize
    /// cleanly rather than throwing or silently dropping every message.</summary>
    [Fact]
    public void Load_OldShapeFileWithoutToolCallFields_DeserializesWithThemNull()
    {
        Directory.CreateDirectory(ProjectMemoryStore.MemoryDirectory(_solutionDirectory));
        File.WriteAllText(
            Path.Combine(ProjectMemoryStore.MemoryDirectory(_solutionDirectory), "chat-history.json"),
            """[{"Role":"User","Text":"Hello"},{"Role":"Assistant","Text":"Hi there"}]""");

        var loaded = _store.Load(_solutionDirectory);

        loaded.Count.ShouldBe(2);
        loaded[0].Text.ShouldBe("Hello");
        loaded[0].ToolCallId.ShouldBeNull();
        loaded[0].ToolCalls.ShouldBeNull();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsToolCallsToo()
    {
        var messages = new[]
        {
            new StoredChatMessage(
                "Assistant", "",
                ToolCalls: [new StoredToolCall("call-1", "propose_edit", """{"file":"Foo.cs"}""")]),
            new StoredChatMessage("Tool", "Applied to Foo.cs.", ToolCallId: "call-1"),
        };

        _store.Save(_solutionDirectory, messages);
        var loaded = _store.Load(_solutionDirectory);

        loaded.Count.ShouldBe(2);
        loaded[0].ToolCalls.ShouldHaveSingleItem().ArgumentsJson.ShouldBe("""{"file":"Foo.cs"}""");
        loaded[1].ToolCallId.ShouldBe("call-1");
    }
}
