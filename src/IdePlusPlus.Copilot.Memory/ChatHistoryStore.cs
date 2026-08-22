using System.Text.Json;

namespace IdePlusPlus.Copilot.Memory;

/// <summary>
/// The raw conversation transcript, auto-saved after every assistant turn so it survives an app
/// restart - unlike project-memory.md, which is a curated, user-triggered summary. Scoped under the
/// same per-solution .ideplusplus/copilot directory ProjectMemoryStore already uses.
/// </summary>
public sealed class ChatHistoryStore
{
    private static string FilePath(string solutionDirectory) =>
        Path.Combine(ProjectMemoryStore.MemoryDirectory(solutionDirectory), "chat-history.json");

    public IReadOnlyList<StoredChatMessage> Load(string solutionDirectory)
    {
        var path = FilePath(solutionDirectory);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<StoredChatMessage>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(string solutionDirectory, IReadOnlyList<StoredChatMessage> messages)
    {
        var path = FilePath(solutionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(messages));
    }
}
