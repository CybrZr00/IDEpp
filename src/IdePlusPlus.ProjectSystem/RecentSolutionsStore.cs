using System.Text.Json;

namespace IdePlusPlus.ProjectSystem;

/// <summary>
/// Global MRU list of opened solution paths under %LOCALAPPDATA%\IdePlusPlus\recent-solutions.json -
/// mirrors EditorWorkspace's recent-files.json pattern exactly, just scoped to solutions instead of
/// individual files, and global rather than per-solution the way StartupProjectStore is.
/// </summary>
public static class RecentSolutionsStore
{
    private const int MaxRecentSolutions = 10;

    private static readonly string RecentSolutionsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "recent-solutions.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(RecentSolutionsPath))
            {
                return [];
            }

            var json = File.ReadAllText(RecentSolutionsPath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception)
        {
            // Corrupt/unreadable MRU file from a previous version - start with an empty list.
            return [];
        }
    }

    /// <summary>Moves solutionPath to the front of the MRU list (adding it if new), persists, and
    /// returns the updated list.</summary>
    public static IReadOnlyList<string> Touch(string solutionPath)
    {
        var recent = Load().ToList();
        recent.Remove(solutionPath);
        recent.Insert(0, solutionPath);
        while (recent.Count > MaxRecentSolutions)
        {
            recent.RemoveAt(recent.Count - 1);
        }

        var directory = Path.GetDirectoryName(RecentSolutionsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(RecentSolutionsPath, JsonSerializer.Serialize(recent));

        return recent;
    }
}
