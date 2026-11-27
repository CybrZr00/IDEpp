using Serilog;

namespace IdePlusPlus.ProjectSystem;

/// <summary>
/// Records an empty folder in a .csproj the way Visual Studio does - a <c>&lt;Folder
/// Include="Rel\Path\" /&gt;</c> item - so it survives a reload. A directory on its own is invisible
/// to the SDK's default globs (they match files), which is why "Add New Folder" that only did
/// Directory.CreateDirectory would lose the folder the next time the solution was opened.
/// </summary>
public static class ProjectFolderWriter
{
    /// <summary>
    /// Adds <paramref name="folderRelativePath"/> (relative to the project file's own directory) to the
    /// project as a Folder item. Returns true only when the file was actually changed; false when the
    /// folder was already listed, the path is not a plain relative one, or the project could not be
    /// read/written - in which case the file is left as it was.
    /// </summary>
    public static bool TryAddFolder(string projectPath, string folderRelativePath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(folderRelativePath))
        {
            return false;
        }

        var normalized = UnloadedProjectsStore.NormalizeRelativePath(folderRelativePath);
        if (normalized.Length == 0 || !UnloadedProjectsStore.IsValidRelativePath(normalized))
        {
            return false;
        }

        if (!File.Exists(projectPath))
        {
            return false;
        }

        SolutionLoader.EnsureMSBuildRegistered();

        try
        {
            return TryAddFolderCore(projectPath, normalized);
        }
        catch (Exception ex)
        {
            // Read-only, locked by another editor, or not XML we can parse: the caller still creates the
            // directory (so the folder is usable now) and logs this, rather than failing the action.
            Log.Warning(ex, "Could not add folder {Folder} to project {Project}.", normalized, projectPath);
            return false;
        }
    }

    /// <summary>Segment form of <see cref="TryAddFolder(string, string)"/> - what the tree's
    /// GetOrCreateFolder gives you.</summary>
    public static bool TryAddFolder(string projectPath, IReadOnlyList<string> folderSegments)
    {
        if (folderSegments is null || folderSegments.Count == 0)
        {
            return false;
        }

        return TryAddFolder(projectPath, string.Join('\\', folderSegments.Where(s => !string.IsNullOrWhiteSpace(s))));
    }

    // Isolated in its own method for the same reason as SolutionLoader.LoadCore: its MSBuild type
    // references must not be JIT-resolved before EnsureMSBuildRegistered has run.
    private static bool TryAddFolderCore(string projectPath, string normalizedFolder)
    {
        // Its own collection rather than the global one: the global collection is process-wide mutable
        // state and a save here must not race an evaluation elsewhere in the IDE.
        using var projectCollection = new Microsoft.Build.Evaluation.ProjectCollection();

        var root = Microsoft.Build.Construction.ProjectRootElement.Open(
            projectPath, projectCollection, preserveFormatting: true);

        if (HasFolderItem(root, normalizedFolder))
        {
            return false;
        }

        // A new ItemGroup at the end of the file keeps every existing item, group and comment exactly
        // where it was - preserveFormatting only protects formatting of the elements it does not add.
        var itemGroup = root.AddItemGroup();
        itemGroup.AddItem("Folder", normalizedFolder + "\\");

        root.Save();
        return true;
    }

    private static bool HasFolderItem(Microsoft.Build.Construction.ProjectRootElement root, string normalizedFolder) =>
        root.Items.Any(item =>
            item.ItemType == "Folder" &&
            string.Equals(
                UnloadedProjectsStore.NormalizeRelativePath(item.Include),
                normalizedFolder,
                StringComparison.OrdinalIgnoreCase));
}
