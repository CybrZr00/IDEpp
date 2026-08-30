namespace IdePlusPlus.Publish;

/// <summary>
/// Discovers, saves and deletes .pubxml files in a project's Properties/PublishProfiles folder -
/// the same location Visual Studio uses and the SDK's PublishProfile import looks in, so profiles
/// created here are usable from `dotnet publish -p:PublishProfile=x` and from VS, and profiles
/// created there show up here. Path resolution mirrors LaunchSettingsLoader, which reads
/// Properties/launchSettings.json out of the same project folder.
/// </summary>
public static class PublishProfileStore
{
    public const string Extension = ".pubxml";

    public static string? ProfilesDirectory(string projectPath) =>
        Path.GetDirectoryName(projectPath) is { } projectDirectory
            ? Path.Combine(projectDirectory, "Properties", "PublishProfiles")
            : null;

    /// <summary>Every readable profile in the project, name-sorted. Files that fail to parse are
    /// skipped rather than reported - one hand-mangled .pubxml shouldn't stop the dialog opening.</summary>
    public static IReadOnlyList<PublishProfile> Load(string projectPath)
    {
        if (ProfilesDirectory(projectPath) is not { } directory || !Directory.Exists(directory))
        {
            return [];
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*" + Extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return files
            .Select(PublishProfileReader.ParseFile)
            .OfType<PublishProfile>()
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Writes the profile to Properties/PublishProfiles/{Name}.pubxml and returns that path.
    ///
    /// A renamed profile keeps its old file's content as the base for the in-place edit (so comments
    /// and unmodelled properties survive the rename) and then the old file is removed.
    /// </summary>
    public static string Save(string projectPath, PublishProfile profile)
    {
        if (ValidateName(profile.Name) is { } nameError)
        {
            throw new ArgumentException(nameError, nameof(profile));
        }

        if (ProfilesDirectory(projectPath) is not { } directory)
        {
            throw new ArgumentException($"'{projectPath}' has no containing directory.", nameof(projectPath));
        }

        var targetPath = Path.Combine(directory, profile.Name + Extension);

        string? existingXml = null;
        var sourcePath = profile.FilePath ?? targetPath;
        if (File.Exists(sourcePath))
        {
            try
            {
                existingXml = File.ReadAllText(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable - generate a fresh document instead of failing the save outright.
            }
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(targetPath, PublishProfileWriter.ToXml(profile, existingXml));

        if (!PathsEqual(sourcePath, targetPath))
        {
            DeleteIfProfile(directory, sourcePath);
        }

        return targetPath;
    }

    public static void Delete(string projectPath, string name)
    {
        if (ProfilesDirectory(projectPath) is not { } directory || ValidateName(name) is not null)
        {
            return;
        }

        DeleteIfProfile(directory, Path.Combine(directory, name + Extension));
    }

    /// <summary>
    /// Returns why the name is unusable, or null if it's fine.
    ///
    /// A profile name isn't decoration - it becomes a file name and the value of
    /// `-p:PublishProfile=`. Letting a separator through would write outside the profiles folder.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A publish profile needs a name.";
        }

        if (name != name.Trim())
        {
            return "A publish profile name can't start or end with whitespace.";
        }

        if (name is "." or "..")
        {
            return $"'{name}' isn't a usable profile name.";
        }

        if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "A publish profile name can't contain path separators or the characters " +
                   @"< > : "" / \ | ? *";
        }

        return null;
    }

    private static void DeleteIfProfile(string directory, string path)
    {
        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            || !PathsEqual(Path.GetDirectoryName(path) ?? "", directory))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or already gone - the save itself succeeded, which is what matters.
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
