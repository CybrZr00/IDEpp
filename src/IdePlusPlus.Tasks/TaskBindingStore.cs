using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdePlusPlus.Tasks;

/// <summary>
/// Persists task-to-build-event bindings at .ideplusplus/tasks.json - same per-solution convention
/// as PublishSelectionStore and StartupConfigurationStore, and the same degrade-to-empty posture on
/// a malformed or unreadable file.
///
/// Unlike a publish-profile selection, losing this file changes behaviour rather than just
/// convenience: bindings silently stop firing. It still degrades rather than throwing, because the
/// alternative - refusing to open a solution over a corrupt sidecar file - is worse, and the
/// bindings are visible in Task Explorer for the user to notice and set again.
///
/// Bindings written here are not visible to Visual Studio, which keeps its own in a comment at the
/// top of the manifest. Reading that comment is a Phase 3 concern - it only appears in the
/// gulpfile.js/Gruntfile.js that phase adds - see docs/task-runner-design.md.
/// </summary>
public static class TaskBindingStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", "tasks.json");

    public static IReadOnlyList<TaskBinding> Load(string solutionDirectory)
    {
        var path = FilePath(solutionDirectory);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), SerializerOptions);
            return stored?.Bindings?
                .Where(b => !string.IsNullOrWhiteSpace(b.Manifest) && !string.IsNullOrWhiteSpace(b.Task))
                .Select(b => new TaskBinding(b.Manifest!, b.Task!, b.Kind))
                .ToList()
                ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(string solutionDirectory, IReadOnlyList<TaskBinding> bindings)
    {
        var path = FilePath(solutionDirectory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stored = new StoredFile(
                bindings.Select(b => new StoredBinding(b.ManifestRelativePath, b.TaskName, b.Kind)).ToList());
            File.WriteAllText(path, JsonSerializer.Serialize(stored, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or locked solution folder shouldn't turn ticking a menu item into an error.
        }
    }

    /// <summary>Solution-relative with forward slashes, so the stored file reads the same whichever
    /// machine wrote it. Falls back to the absolute path for a manifest outside the solution
    /// directory, which round-trips correctly even though it will not survive a move.</summary>
    public static string ToRelativePath(string solutionDirectory, string manifestPath)
    {
        try
        {
            var relative = Path.GetRelativePath(solutionDirectory, manifestPath);
            return Path.IsPathRooted(relative) ? manifestPath : relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return manifestPath;
        }
    }

    public static string ToAbsolutePath(string solutionDirectory, string manifestRelativePath)
    {
        var native = manifestRelativePath.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(native) ? native : Path.Combine(solutionDirectory, native));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return native;
        }
    }

    private sealed record StoredBinding(string? Manifest, string? Task, TaskBindingKind Kind);

    private sealed record StoredFile(List<StoredBinding> Bindings);
}
