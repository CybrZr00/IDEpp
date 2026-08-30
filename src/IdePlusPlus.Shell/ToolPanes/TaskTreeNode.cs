using System.IO;
using IdePlusPlus.Tasks;

namespace IdePlusPlus.Shell.ToolPanes;

public enum TaskTreeNodeKind
{
    Manifest,
    Task,
}

/// <summary>
/// Task Explorer's manifest&gt;task tree node. Unlike TestTreeNode this is deliberately not an
/// ObservableObject and its children are a plain list: nothing mutates a node after discovery
/// builds it (a task has no per-node result to apply the way a test does), and a refresh replaces
/// the whole tree rather than updating nodes in place.
/// </summary>
public sealed class TaskTreeNode(string name, TaskTreeNodeKind kind)
{
    public string Name { get; } = name;

    public TaskTreeNodeKind Kind { get; } = kind;

    /// <summary>Set on both kinds - the file this node came from (itself, for Manifest nodes).</summary>
    public required string ManifestPath { get; init; }

    public required NodePackageManager PackageManager { get; init; }

    /// <summary>Set on Task-kind leaves only; what <see cref="TaskRunService"/> is handed.</summary>
    public TaskDefinition? Definition { get; init; }

    /// <summary>Tooltip text: the full path for a manifest, the raw script text for a task.</summary>
    public string? Description { get; init; }

    public List<TaskTreeNode> Children { get; } = [];

    public static TaskTreeNode ForManifest(TaskManifest manifest)
    {
        var node = new TaskTreeNode(DisplayName(manifest.Path), TaskTreeNodeKind.Manifest)
        {
            ManifestPath = manifest.Path,
            PackageManager = manifest.PackageManager,
            Description = manifest.Path,
        };

        foreach (var task in manifest.Tasks)
        {
            node.Children.Add(new TaskTreeNode(task.Name, TaskTreeNodeKind.Task)
            {
                ManifestPath = manifest.Path,
                PackageManager = manifest.PackageManager,
                Definition = task,
                Description = task.Command,
            });
        }

        return node;
    }

    /// <summary>Qualified by its containing directory, since a solution with several manifests
    /// would otherwise show "package.json" several times over.</summary>
    private static string DisplayName(string manifestPath)
    {
        var directoryName = Path.GetFileName(Path.GetDirectoryName(manifestPath));
        return string.IsNullOrEmpty(directoryName)
            ? TaskManifestDiscovery.PackageJsonFileName
            : $"{directoryName}/{TaskManifestDiscovery.PackageJsonFileName}";
    }
}
