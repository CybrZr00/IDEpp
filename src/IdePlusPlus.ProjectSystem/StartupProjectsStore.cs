using System.Text.Json;

namespace IdePlusPlus.ProjectSystem;

/// <summary>
/// Persists the settable list of startup projects that all launch together on F5. Supersedes
/// <see cref="StartupProjectStore"/> (which persists exactly one path as plain text) without deleting
/// it or its on-disk file: <see cref="Load"/> falls back to the old single-path file so a solution
/// that only ever saved through the old store doesn't silently lose its remembered startup project.
/// </summary>
public static class StartupProjectsStore
{
    private static string FilePath(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", "startup-projects.json");

    public static void Save(string solutionDirectory, IReadOnlyList<string> projectPaths)
    {
        var path = FilePath(solutionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(projectPaths));
    }

    public static IReadOnlyList<string> Load(string solutionDirectory)
    {
        var path = FilePath(solutionDirectory);
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var projectPaths = JsonSerializer.Deserialize<List<string>>(json);
            if (projectPaths is not null)
            {
                return projectPaths;
            }
        }

        var legacyPath = StartupProjectStore.Load(solutionDirectory);
        if (legacyPath is not null)
        {
            return [legacyPath];
        }

        return [];
    }
}
