using System.Text.Json;

namespace IdePlusPlus.Run;

/// <summary>
/// Reads Properties/launchSettings.json next to a project file. With no explicit profile name, picks
/// the first commandName: "Project" profile - the same one `dotnet run` uses by default when no
/// --launch-profile is given, so browser-launch/env-injection here match what a plain `dotnet run`
/// already does. Returns null for any project without a launchSettings.json (plain console apps,
/// class libraries, etc.) - Run/Debug behavior for those is completely unaffected.
/// </summary>
public static class LaunchSettingsLoader
{
    public static LaunchProfile? Load(string projectPath, string? profileName = null)
    {
        if (!TryReadProfiles(projectPath, out var profiles))
        {
            return null;
        }

        if (profileName is not null)
        {
            return profiles.TryGetProperty(profileName, out var namedProfile)
                ? ParseProfile(profileName, namedProfile)
                : null;
        }

        foreach (var profile in profiles.EnumerateObject())
        {
            if (profile.Value.TryGetProperty("commandName", out var commandName)
                && commandName.GetString() == "Project")
            {
                return ParseProfile(profile.Name, profile.Value);
            }
        }

        return null;
    }

    /// <summary>Lists every profile in launchSettings.json (name + commandName), unfiltered, for a
    /// profile-picker UI - unlike <see cref="Load"/>, this doesn't skip non-"Project" profiles.</summary>
    public static IReadOnlyList<(string Name, string CommandName)> LoadProfileNames(string projectPath)
    {
        if (!TryReadProfiles(projectPath, out var profiles))
        {
            return [];
        }

        var names = new List<(string, string)>();
        foreach (var profile in profiles.EnumerateObject())
        {
            var commandName = profile.Value.TryGetProperty("commandName", out var commandNameElement)
                ? commandNameElement.GetString() ?? ""
                : "";
            names.Add((profile.Name, commandName));
        }

        return names;
    }

    private static bool TryReadProfiles(string projectPath, out JsonElement profiles)
    {
        profiles = default;

        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (projectDirectory is null)
        {
            return false;
        }

        var launchSettingsPath = Path.Combine(projectDirectory, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettingsPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
            if (!document.RootElement.TryGetProperty("profiles", out var profilesElement))
            {
                return false;
            }

            // Clone so the JsonElement stays valid after `document` (and its underlying buffer) is disposed.
            profiles = profilesElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            // Malformed launchSettings.json - fall back to no profile rather than blocking Run/Debug.
            return false;
        }
    }

    private static LaunchProfile ParseProfile(string name, JsonElement element)
    {
        var commandName = element.TryGetProperty("commandName", out var commandNameElement)
            ? commandNameElement.GetString() ?? ""
            : "";
        var launchBrowser = element.TryGetProperty("launchBrowser", out var launchBrowserElement)
            && launchBrowserElement.GetBoolean();
        var applicationUrl = element.TryGetProperty("applicationUrl", out var urlElement)
            ? urlElement.GetString()
            : null;
        var commandLineArgs = element.TryGetProperty("commandLineArgs", out var argsElement)
            ? SplitArgs(argsElement.GetString())
            : [];

        var environmentVariables = new Dictionary<string, string>();
        if (element.TryGetProperty("environmentVariables", out var envVars))
        {
            foreach (var envVar in envVars.EnumerateObject())
            {
                if (envVar.Value.GetString() is { } value)
                {
                    environmentVariables[envVar.Name] = value;
                }
            }
        }

        // Mirrors what `dotnet run`/Visual Studio do under the hood: applicationUrl becomes
        // ASPNETCORE_URLS unless the profile already sets it explicitly. Debug launches the built
        // DLL directly (bypassing `dotnet run`, which would otherwise resolve this itself), so this
        // is what makes F5 bind to the same URL the profile configured.
        if (applicationUrl is not null && !environmentVariables.ContainsKey("ASPNETCORE_URLS"))
        {
            environmentVariables["ASPNETCORE_URLS"] = applicationUrl;
        }

        return new LaunchProfile(name, commandName, launchBrowser, applicationUrl, commandLineArgs, environmentVariables);
    }

    /// <summary>Splits a launchSettings.json commandLineArgs string into an argv list, matching the
    /// quoting rules `dotnet run`/Visual Studio use: whitespace-separated, double-quoted segments kept
    /// together (quotes themselves stripped), no escape-character support beyond that.</summary>
    private static List<string> SplitArgs(string? commandLineArgs)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLineArgs))
        {
            return result;
        }

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in commandLineArgs)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
