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
    public void Load_CorruptFile_ReturnsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(ProjectMemoryStore.MemoryDirectory(_solutionDirectory));
        File.WriteAllText(Path.Combine(ProjectMemoryStore.MemoryDirectory(_solutionDirectory), "chat-history.json"), "{ not valid json");

        _store.Load(_solutionDirectory).ShouldBeEmpty();
    }
}
