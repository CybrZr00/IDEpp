namespace IdePlusPlus.Copilot.Memory;

/// <summary>
/// project-memory.md is the durable, curated cross-session context - kept small and readable,
/// git-trackable, and edited only through the same propose_edit/diff/accept pipeline as any other
/// file (see M5), never written directly by this class. sessions/*.md are a raw audit trail only.
/// </summary>
public sealed class ProjectMemoryStore
{
    public static string MemoryDirectory(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", "copilot");

    public static string MemoryFilePath(string solutionDirectory) =>
        Path.Combine(MemoryDirectory(solutionDirectory), "project-memory.md");

    public static string IndexFilePath(string solutionDirectory) =>
        Path.Combine(MemoryDirectory(solutionDirectory), "embeddings.json");

    public string LoadMemory(string solutionDirectory)
    {
        var path = MemoryFilePath(solutionDirectory);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void SaveSessionLog(string solutionDirectory, string transcript)
    {
        var sessionsDirectory = Path.Combine(MemoryDirectory(solutionDirectory), "sessions");
        Directory.CreateDirectory(sessionsDirectory);

        var fileName = $"{DateTime.Now:yyyy-MM-dd_HHmmss}.md";
        File.WriteAllText(Path.Combine(sessionsDirectory, fileName), transcript);
    }
}
