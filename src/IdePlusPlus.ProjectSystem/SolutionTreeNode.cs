using System.Collections.ObjectModel;

namespace IdePlusPlus.ProjectSystem;

public enum SolutionTreeNodeKind
{
    Solution,
    Project,
    Folder,
    File,
}

public sealed class SolutionTreeNode(string name, string? fullPath, SolutionTreeNodeKind kind)
{
    public string Name { get; } = name;

    public string? FullPath { get; } = fullPath;

    public SolutionTreeNodeKind Kind { get; } = kind;

    public ObservableCollection<SolutionTreeNode> Children { get; } = [];

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
