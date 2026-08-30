using System.Text.Json;

namespace IdePlusPlus.Tasks;

/// <summary>The parts of a package.json IDE++ reads. Both members are always non-null/absent rather
/// than throwing - see <see cref="PackageJsonReader"/>.</summary>
public sealed record PackageJsonContents(IReadOnlyList<TaskDefinition> Scripts, string? PackageManagerField);

/// <summary>
/// Reads the two things Task Explorer cares about out of a package.json: its <c>scripts</c> object,
/// and the corepack <c>packageManager</c> field. Mirrors LaunchSettingsLoader's posture - a missing,
/// unreadable or malformed file returns null rather than throwing, so one broken package.json
/// somewhere in a solution cannot take Task Explorer down for every other manifest.
///
/// Deliberately a pure JSON parse: nothing here executes the manifest. That is what lets npm-script
/// discovery run automatically on solution load, unlike gulp/grunt discovery, which has to run the
/// manifest to enumerate its tasks (see docs/task-runner-design.md).
/// </summary>
public static class PackageJsonReader
{
    public static PackageJsonContents? Read(string packageJsonPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(packageJsonPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            // A package.json whose root is anything but an object is malformed as far as npm is
            // concerned; treat it the same as unparseable rather than reading properties off it.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new PackageJsonContents(ReadScripts(root, packageJsonPath), ReadPackageManagerField(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<TaskDefinition> ReadScripts(JsonElement root, string packageJsonPath)
    {
        var scripts = new List<TaskDefinition>();
        if (!root.TryGetProperty("scripts", out var scriptsElement) || scriptsElement.ValueKind != JsonValueKind.Object)
        {
            return scripts;
        }

        foreach (var script in scriptsElement.EnumerateObject())
        {
            // npm itself ignores non-string script values; listing one as runnable would offer a
            // task that cannot start.
            if (script.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            scripts.Add(new TaskDefinition(
                TaskManifestKind.NpmScripts, packageJsonPath, script.Name, script.Value.GetString()));
        }

        return scripts;
    }

    private static string? ReadPackageManagerField(JsonElement root) =>
        root.TryGetProperty("packageManager", out var packageManager) && packageManager.ValueKind == JsonValueKind.String
            ? packageManager.GetString()
            : null;
}
