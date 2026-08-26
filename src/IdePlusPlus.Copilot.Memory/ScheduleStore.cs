using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdePlusPlus.Copilot.Memory;

/// <summary>
/// Persists the solution's scheduled automations at .ideplusplus/copilot/schedules.json - the same
/// per-solution config tier as mcp-servers.json's project-scoped counterpart. Schedules are
/// human-authored only (see ScheduledAutomationsPaneViewModel in IdePlusPlus.Shell) - there is
/// deliberately no agent-callable tool to create one, since unlike everything else the agent can do,
/// a schedule persists and repeats after the conversation that might have produced it ends.
/// </summary>
public sealed class ScheduleStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string SchedulesFilePath(string solutionDirectory) =>
        Path.Combine(ProjectMemoryStore.MemoryDirectory(solutionDirectory), "schedules.json");

    /// <summary>Where one schedule's run results are logged, mirroring
    /// ProjectMemoryStore.SaveSessionLog's sessions/*.md pattern. scheduleName is sanitized to a
    /// safe path segment here - it's user-typed (via the create-schedule form), not something a
    /// caller should have to pre-validate before it's safe to build a path from.</summary>
    public static string RunLogDirectory(string solutionDirectory, string scheduleName) =>
        Path.Combine(ProjectMemoryStore.MemoryDirectory(solutionDirectory), "schedules", SanitizeForPath(scheduleName));

    public IReadOnlyList<ScheduleDefinition> Load(string solutionDirectory)
    {
        var path = SchedulesFilePath(solutionDirectory);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ScheduleDefinition>>(File.ReadAllText(path), SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(string solutionDirectory, IReadOnlyList<ScheduleDefinition> schedules)
    {
        var path = SchedulesFilePath(solutionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(schedules, SerializerOptions));
    }

    public void SaveRunLog(string solutionDirectory, string scheduleName, string resultText)
    {
        var directory = RunLogDirectory(solutionDirectory, scheduleName);
        Directory.CreateDirectory(directory);

        var fileName = $"{DateTime.Now:yyyy-MM-dd_HHmmss}.md";
        File.WriteAllText(Path.Combine(directory, fileName), resultText);
    }

    private static string SanitizeForPath(string name)
    {
        var sanitized = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return sanitized.Length == 0 ? "_" : sanitized;
    }
}
