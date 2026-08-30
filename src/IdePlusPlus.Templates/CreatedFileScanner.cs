namespace IdePlusPlus.Templates;

/// <summary>
/// Works out which files a template actually produced, by comparing the target directory before and
/// after.
///
/// <para>The alternatives don't survive contact: the template cache carries no primary-output list,
/// and <c>--dry-run</c>'s "File actions would have been taken:" summary is localized prose. A
/// directory diff also handles templates that emit more than one file - maui-page-xaml writes a
/// .xaml and a .xaml.cs - without needing to know which do.</para>
/// </summary>
public static class CreatedFileScanner
{
    /// <summary>Excluded because a project template's restore post-action fills obj/ with a dozen
    /// files that are not what anyone asked to create, and opening project.assets.json in the editor
    /// would be a strange way to finish New Project.</summary>
    private static readonly string[] BuildOutputDirectories = ["obj", "bin"];

    /// <summary>The files present now, for comparing against later. A directory that doesn't exist
    /// yet is empty rather than an error - for a new project it is the CLI that creates it.</summary>
    public static IReadOnlySet<string> Snapshot(string directory)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return files;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!IsBuildOutput(directory, path))
                {
                    files.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial snapshot only risks reporting an extra created file, which is a far better
            // failure than refusing to create anything.
        }

        return files;
    }

    /// <summary>What appeared since <paramref name="before"/>, sorted so the caller opens files in a
    /// stable order rather than the filesystem's.</summary>
    public static IReadOnlyList<string> NewFilesSince(string directory, IReadOnlySet<string> before) =>
        Snapshot(directory)
            .Where(path => !before.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsBuildOutput(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        // Any segment, not just the first: a solution-kind template creates several project
        // directories, each with its own obj/.
        return segments[..^1].Any(segment =>
            BuildOutputDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
