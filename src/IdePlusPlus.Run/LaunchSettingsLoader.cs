using System.Text.Json;

namespace IdePlusPlus.Run;

/// <summary>
/// Reads Properties/launchSettings.json next to a project file and picks the first commandName:
/// "Project" profile - the same one `dotnet run` uses by default when no --launch-profile is given,
/// so browser-launch/env-injection here match what a plain `dotnet run` already does. Returns null
/// for any project without a launchSettings.json (plain console apps, class libraries, etc.) -
/// Run/Debug behavior for those is completely unaffected.
/// </summary>
public static class LaunchSettingsLoader
{
    public static LaunchProfile? Load(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (projectDirectory is null)
        {
            return null;
        }

        var launchSettingsPath = Path.Combine(projectDirectory, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettingsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
            if (!document.RootElement.TryGetProperty("profiles", out var profiles))
            {
                return null;
            }

            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("commandName", out var commandName)
                    && commandName.GetString() == "Project")
                {
                    return ParseProfile(profile.Name, profile.Value);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed launchSettings.json - fall back to no profile rather than blocking Run/Debug.
        }

        return null;
    }

    private static LaunchProfile ParseProfile(string name, JsonElement element)
    {
        var launchBrowser = element.TryGetProperty("launchBrowser", out var launchBrowserElement)
            && launchBrowserElement.GetBoolean();
        var applicationUrl = element.TryGetProperty("applicationUrl", out var urlElement)
            ? urlElement.GetString()
            : null;

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

        return new LaunchProfile(name, launchBrowser, applicationUrl, environmentVariables);
    }
}
