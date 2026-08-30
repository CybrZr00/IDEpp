namespace IdePlusPlus.Tasks;

/// <summary>
/// Resolves a Node CLI tool name ("npm", "pnpm", "yarn", later "gulp") to a full path to something
/// Windows can actually start.
///
/// This exists because handing the bare name to ProcessStartInfo does not work. With
/// UseShellExecute=false the name goes to CreateProcess, which appends <c>.exe</c> - and only
/// <c>.exe</c> - when searching PATH. Every one of these tools ships on Windows as a <c>.cmd</c>
/// shim (plus an extensionless shell script for Git Bash), so "npm" resolves to nothing and the
/// failure reads as "npm is not installed" on a machine where it plainly is. Resolving the real
/// file here, applying PATHEXT the way a shell would, is the fix.
///
/// node_modules\.bin is searched first, walking upwards: a repo that pins its own tooling expects
/// its local copy to win over whatever happens to be installed globally, and in a workspace the
/// .bin directory lives at the root rather than next to the manifest.
/// </summary>
public static class NodeToolLocator
{
    /// <summary>What cmd.exe falls back to when PATHEXT is unset.</summary>
    private const string DefaultPathExtensions = ".COM;.EXE;.BAT;.CMD";

    public static string? Locate(string toolName, string? startDirectory) =>
        Locate(
            toolName,
            startDirectory,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"));

    /// <summary>Overload taking PATH/PATHEXT explicitly - the environment is a global, so this is
    /// what the tests drive.</summary>
    public static string? Locate(string toolName, string? startDirectory, string? path, string? pathExt)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        var extensions = ParseExtensions(pathExt);
        return FindInNodeModules(toolName, startDirectory, extensions) ?? FindOnPath(toolName, path, extensions);
    }

    private static string? FindInNodeModules(string toolName, string? startDirectory, IReadOnlyList<string> extensions)
    {
        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var binDirectory = Path.Combine(directory, "node_modules", ".bin");
            if (FirstExisting(binDirectory, toolName, extensions) is { } found)
            {
                return found;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static string? FindOnPath(string toolName, string? path, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // PATH entries are sometimes quoted when they contain spaces; Path.Combine would keep
            // the quotes and never match.
            var directory = entry.Trim('"');
            if (directory.Length > 0 && FirstExisting(directory, toolName, extensions) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Extensionless matches are deliberately not considered: the extensionless "npm" next
    /// to npm.cmd is a shell script, which CreateProcess cannot start.</summary>
    private static string? FirstExisting(string directory, string toolName, IReadOnlyList<string> extensions)
    {
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(directory, toolName + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ParseExtensions(string? pathExt)
    {
        var value = string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExtensions : pathExt;
        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToList();
    }
}
