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

    public static string SessionsDirectory(string solutionDirectory) =>
        Path.Combine(MemoryDirectory(solutionDirectory), "sessions");

    /// <summary>Writes the transcript to a new file and returns its path. Never overwrites: the name
    /// only has one-second resolution, and Save Memory followed straight away by New Chat would
    /// otherwise lose the first of the two logs.</summary>
    public string SaveSessionLog(string solutionDirectory, string transcript)
    {
        var sessionsDirectory = SessionsDirectory(solutionDirectory);
        Directory.CreateDirectory(sessionsDirectory);

        var stem = $"{DateTime.Now:yyyy-MM-dd_HHmmss}";
        var path = Path.Combine(sessionsDirectory, $"{stem}.md");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(sessionsDirectory, $"{stem}_{suffix}.md");
        }

        File.WriteAllText(path, transcript);
        return path;
    }
}
