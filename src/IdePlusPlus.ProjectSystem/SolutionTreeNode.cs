using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>Two-way bound to the TreeView's own selection, so the IDE can reveal a node it just
    /// created rather than leaving the user to find it.</summary>
    [ObservableProperty]
    private bool isSelected;

    /// <summary>Two-way bound to the TreeView's expansion. Selecting a node is not enough on its own -
    /// WPF only realizes a container once its parents are expanded, so a selection set on a collapsed
    /// branch silently does nothing.</summary>
    [ObservableProperty]
    private bool isExpanded;

    /// <summary>
    /// The Windows target framework moniker to pass to `dotnet build/run -f` for multi-targeted
    /// projects (e.g. MAUI's TargetFrameworks="net10.0-android;net10.0-windows10.0.19041.0;..."),
    /// so Build/Run/Debug don't try to build every platform including mobile ones whose workloads
    /// likely aren't installed. Null for ordinary single-TargetFramework projects, which need no
    /// "-f" at all - existing behavior for every non-multi-targeted project is unchanged.
    /// </summary>
    public string? TargetFramework { get; init; }

    /// <summary>
    /// The project's evaluated $(RootNamespace), for working out what namespace a new file should
    /// get. Always set for a Project node in practice - the SDK defaults it to the project name when
    /// the .csproj doesn't say - and null for every other node kind.
    /// </summary>
    public string? RootNamespace { get; init; }

    /// <summary>True when TestProjectDetector identified this project as a test project at load
    /// time (via $(IsTestProject) or a known test-framework PackageReference) - best-effort, set
    /// once and never re-evaluated.</summary>
    public bool IsTestProject { get; init; }

    /// <summary>
    /// True when the user unloaded this project (see <see cref="UnloadedProjectsStore"/>). The node
    /// still exists in the tree so it can be reloaded, but it has no evaluated children and every
    /// solution-wide build is aimed at a filter that leaves it out.
    /// </summary>
    public bool IsUnloaded { get; init; }

    /// <summary>
    /// Why this project could not be evaluated (a broken .csproj, a missing import, a project whose SDK
    /// is not installed), or null when it loaded. Such a node is a placeholder with no children - one
    /// bad project no longer fails the whole solution load.
    /// </summary>
    public string? LoadError { get; init; }

    /// <summary>
    /// The project's path relative to the solution directory ('src\App\App.csproj'), which is how
    /// <see cref="UnloadedProjectsStore"/> and <see cref="SolutionBuildTarget"/> identify it - relative
    /// so the same entry works inside an Ultra worktree. Set for Project nodes, null for everything else.
    /// </summary>
    public string? SolutionRelativePath { get; init; }

    /// <summary>How the tree renders this node: the plain name, or the name with the unload/failure
    /// reason appended so the two placeholder kinds are legible without a tooltip.</summary>
    public string DisplayName => LoadError is not null
        ? $"{Name} (load failed)"
        : IsUnloaded
            ? $"{Name} (unloaded)"
            : Name;

    /// <summary>
    /// True when the Solution Explorer's "show unloaded projects" toggle is currently hiding this node.
    /// Set by the pane's filter pass, never by the loader, and observable so a DataTrigger can collapse
    /// the container without reloading the tree.
    /// </summary>
    [ObservableProperty]
    private bool isHiddenByFilter;

    /// <summary>Finds or creates the folder chain for a relative path's directory parts, returning the leaf folder to add files under.</summary>
    /// <param name="directory">The directory <paramref name="pathParts"/> is relative to - the
    /// project's own directory when called on a Project node. Given one, each folder created here
    /// gets a real <see cref="FullPath"/>, which is what lets a folder be the target of "Add New
    /// Item" and what gives plugin context-menu commands a path to act on. Omitted, folders have no
    /// path, which is how this behaved before.</param>
    public SolutionTreeNode GetOrCreateFolder(IEnumerable<string> pathParts, string? directory = null)
    {
        var current = this;
        var currentDirectory = directory;

        foreach (var part in pathParts)
        {
            currentDirectory = currentDirectory is null ? null : Path.Combine(currentDirectory, part);

            var existing = current.Children.FirstOrDefault(c => c.Kind == SolutionTreeNodeKind.Folder && c.Name == part);
            if (existing is null)
            {
                existing = new SolutionTreeNode(part, currentDirectory, SolutionTreeNodeKind.Folder);
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

    /// <summary>Sorts this node's children folders-before-files, then alphabetically, and recurses
    /// into each - called once after the whole tree is built (see SolutionLoader.LoadCore), since
    /// Children is otherwise populated in raw MSBuild item/glob declaration order, which reads as
    /// arbitrary on a large solution. ObservableCollection&lt;T&gt; has no in-place sort, so this
    /// reorders via Move rather than clear-and-re-add, which would raise a Reset instead of tracked
    /// per-item moves - order-independent either way for GetOrCreateFolder's name-based lookup.</summary>
    public void SortChildrenRecursively()
    {
        var sorted = Children
            .OrderBy(c => c.Kind == SolutionTreeNodeKind.File ? 1 : 0)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var currentIndex = Children.IndexOf(sorted[i]);
            if (currentIndex != i)
            {
                Children.Move(currentIndex, i);
            }
        }

        foreach (var child in Children)
        {
            child.SortChildrenRecursively();
        }
    }
}
