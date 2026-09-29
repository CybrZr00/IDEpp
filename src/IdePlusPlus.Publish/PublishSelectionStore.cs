using System.Text.Json;

namespace IdePlusPlus.Publish;

/// <summary>
/// Remembers which publish profile is selected for which project, at
/// .ideplusplus/publish.json - same per-solution convention as StartupConfigurationStore. Keys are
/// solution-relative project paths so the file survives the solution folder being moved or cloned
/// somewhere else.
///
/// This is a UI convenience only. Losing it costs the user one dropdown selection, so a malformed
/// file degrades to empty rather than being reported.
/// </summary>
public static class PublishSelectionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string FilePath(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", "publish.json");

    public static IReadOnlyDictionary<string, string> Load(string solutionDirectory)
    {
        var path = FilePath(solutionDirectory);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), SerializerOptions);
            return new Dictionary<string, string>(
                stored?.ActiveProfiles ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(string solutionDirectory, IReadOnlyDictionary<string, string> activeProfiles)
    {
        var path = FilePath(solutionDirectory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new StoredFile(new Dictionary<string, string>(activeProfiles)), SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or locked solution folder shouldn't turn selecting a dropdown item into an error.
        }
    }

    /// <summary>The key <see cref="Load"/>/<see cref="Save"/> use for a project - its path relative to
    /// the solution directory, with forward slashes so the file reads the same on any platform.</summary>
    public static string KeyFor(string solutionDirectory, string projectPath) =>
        Path.GetRelativePath(solutionDirectory, projectPath).Replace(Path.DirectorySeparatorChar, '/');

    private sealed record StoredFile(Dictionary<string, string> ActiveProfiles);
}
