using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdePlusPlus.ProjectSystem;

/// <summary>
/// Persists the named, saveable set of <see cref="StartupConfiguration"/>s that back the Startup
/// Configuration Manager, at .ideplusplus/startup-configurations.json (same convention as
/// <see cref="StartupProjectsStore"/>). The first time a solution is opened after this feature ships,
/// <see cref="Load"/> migrates that solution's existing choice - from <see cref="StartupProjectsStore"/>
/// (which itself falls back to the older <see cref="StartupProjectStore"/>) - into a single "Default"
/// configuration with every entry set to Action.Start, writes it out, and never reads the legacy
/// stores again for that solution.
/// </summary>
public static class StartupConfigurationStore
{
    public const string DefaultConfigurationName = "Default";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", "startup-configurations.json");

    public static (IReadOnlyList<StartupConfiguration> Configurations, string ActiveName) Load(string solutionDirectory)
    {
        var path = FilePath(solutionDirectory);
        if (File.Exists(path))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), SerializerOptions);
                if (stored is { Configurations.Count: > 0 })
                {
                    var activeName = stored.Configurations.Any(c => c.Name == stored.ActiveName)
                        ? stored.ActiveName
                        : stored.Configurations[0].Name;
                    return (stored.Configurations, activeName);
                }
            }
            catch (JsonException)
            {
                // Malformed file - fall through to a fresh migration below rather than blocking Run/Debug.
            }
        }

        var legacyProjectPaths = StartupProjectsStore.Load(solutionDirectory);
        var defaultConfiguration = new StartupConfiguration(
            DefaultConfigurationName,
            legacyProjectPaths.Select(p => new StartupConfigurationEntry(p, StartupAction.Start, LaunchProfileName: null)).ToList());

        var configurations = new List<StartupConfiguration> { defaultConfiguration };
        Save(solutionDirectory, configurations, DefaultConfigurationName);
        return (configurations, DefaultConfigurationName);
    }

    public static void Save(string solutionDirectory, IReadOnlyList<StartupConfiguration> configurations, string activeName)
    {
        var path = FilePath(solutionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = new StoredFile(activeName, configurations.ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(stored, SerializerOptions));
    }

    private sealed record StoredFile(string ActiveName, List<StartupConfiguration> Configurations);
}
