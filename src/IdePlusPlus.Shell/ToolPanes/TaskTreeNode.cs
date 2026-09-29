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
/// builds it (a task has no per-node result to apply the way a test does), and both a refresh and a
/// binding change replace the whole tree rather than updating nodes in place.
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

    /// <summary>The lifecycle points this task is bound to, if any. Empty for manifest nodes.</summary>
    public IReadOnlyList<TaskBindingKind> Bindings { get; init; } = [];

    /// <summary>What the tree shows: the name, plus any bindings, since a binding that is invisible
    /// is a binding the user will forget they set.</summary>
    public string Label => Bindings.Count == 0
        ? Name
        : $"{Name}  ({string.Join(", ", Bindings.Select(DisplayName))})";

    public List<TaskTreeNode> Children { get; } = [];

    public static TaskTreeNode ForManifest(TaskManifest manifest, IReadOnlyList<TaskBinding> bindingsForManifest)
    {
        var node = new TaskTreeNode(ManifestDisplayName(manifest.Path), TaskTreeNodeKind.Manifest)
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
                Bindings = bindingsForManifest
                    .Where(b => b.TaskName == task.Name)
                    .Select(b => b.Kind)
                    .Distinct()
                    .OrderBy(kind => kind)
                    .ToList(),
            });
        }

        return node;
    }

    /// <summary>"Before Build" rather than "BeforeBuild" - this is menu and label text.</summary>
    public static string DisplayName(TaskBindingKind kind) => kind switch
    {
        TaskBindingKind.BeforeBuild => "Before Build",
        TaskBindingKind.AfterBuild => "After Build",
        TaskBindingKind.BeforeClean => "Before Clean",
        _ => "After Clean",
    };

    /// <summary>Qualified by its containing directory, since a solution with several manifests
    /// would otherwise show "package.json" several times over.</summary>
    private static string ManifestDisplayName(string manifestPath)
    {
        var fileName = Path.GetFileName(manifestPath);
        var directoryName = Path.GetFileName(Path.GetDirectoryName(manifestPath));
        return string.IsNullOrEmpty(directoryName) ? fileName : $"{directoryName}/{fileName}";
    }
}
