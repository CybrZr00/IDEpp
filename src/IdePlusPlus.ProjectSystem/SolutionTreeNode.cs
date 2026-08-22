using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.ProjectSystem;

public enum SolutionTreeNodeKind
{
    Solution,
    Project,
    Folder,
    File,
}

public sealed partial class SolutionTreeNode(string name, string? fullPath, SolutionTreeNodeKind kind) : ObservableObject
{
    public string Name { get; } = name;

    public string? FullPath { get; } = fullPath;

    public SolutionTreeNodeKind Kind { get; } = kind;

    public ObservableCollection<SolutionTreeNode> Children { get; } = [];

    /// <summary>True for the one Project node Run/Debug will act on - see MainWindowViewModel.SetStartupProject.</summary>
    [ObservableProperty]
    private bool isStartupProject;

    /// <summary>
    /// The Windows target framework moniker to pass to `dotnet build/run -f` for multi-targeted
    /// projects (e.g. MAUI's TargetFrameworks="net10.0-android;net10.0-windows10.0.19041.0;..."),
    /// so Build/Run/Debug don't try to build every platform including mobile ones whose workloads
    /// likely aren't installed. Null for ordinary single-TargetFramework projects, which need no
    /// "-f" at all - existing behavior for every non-multi-targeted project is unchanged.
    /// </summary>
    public string? TargetFramework { get; init; }

    /// <summary>Finds or creates the folder chain for a relative path's directory parts, returning the leaf folder to add files under.</summary>
    public SolutionTreeNode GetOrCreateFolder(IEnumerable<string> pathParts)
    {
        var current = this;
        foreach (var part in pathParts)
        {
            var existing = current.Children.FirstOrDefault(c => c.Kind == SolutionTreeNodeKind.Folder && c.Name == part);
            if (existing is null)
            {
                existing = new SolutionTreeNode(part, null, SolutionTreeNodeKind.Folder);
                current.Children.Add(existing);
            }

            current = existing;
        }

        return current;
    }

    /// <summary>This node and every descendant, depth-first.</summary>
    public IEnumerable<SolutionTreeNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }
}
