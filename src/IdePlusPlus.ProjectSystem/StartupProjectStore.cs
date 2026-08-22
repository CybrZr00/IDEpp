namespace IdePlusPlus.ProjectSystem;

/// <summary>
/// Remembers which project in a multi-project solution Run/Debug should act on, the same way
/// Visual Studio's startup-project choice is a per-user, per-solution setting rather than something
/// checked into the .sln itself. Stored under .ideplusplus/ (already gitignored - see M7) alongside
/// the copilot memory/embeddings state, keyed by solution directory.
/// </summary>
public static class StartupProjectStore
{
    private static string FilePath(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", "startup-project.txt");

    public static void Save(string solutionDirectory, string projectPath)
    {
        var path = FilePath(solutionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, projectPath);
    }

    public static string? Load(string solutionDirectory)
    {
        var path = FilePath(solutionDirectory);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
