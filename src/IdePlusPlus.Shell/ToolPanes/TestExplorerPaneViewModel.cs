using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.TestRunner;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Project&gt;Class&gt;Method test tree backed by TestDiscoveryService/TestRunService, mirroring
/// SourceControlPaneViewModel's IsBusy-gated RelayCommand shape. Discovery is triggered externally
/// (after a successful Build, and best-effort after solution load) rather than owned here - see
/// MainWindowViewModel.BuildAsync/LoadSolutionAsync.
/// </summary>
public sealed partial class TestExplorerPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "TestExplorer";

    private readonly TestDiscoveryService _testDiscoveryService;
    private readonly TestRunService _testRunService;

    /// <summary>O(1) result lookup after a run - keyed by TestCase/TestResult's FullyQualifiedName.</summary>
    private readonly Dictionary<string, TestTreeNode> _leavesByFullyQualifiedName = new();

    public TestExplorerPaneViewModel(TestDiscoveryService testDiscoveryService, TestRunService testRunService)
        : base(PaneContentId, "Tests")
    {
        _testDiscoveryService = testDiscoveryService;
        _testRunService = testRunService;
    }

    public ObservableCollection<TestTreeNode> Tree { get; } = [];

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? summary;

    [ObservableProperty]
    private TestTreeNode? selectedNode;

    /// <summary>Discovers (or re-discovers) tests for each given test project, one project at a time -
    /// mirroring how BuildService runs one verb at a time rather than parallelizing across projects.
    /// A project's existing subtree is replaced, not duplicated, when discovery re-runs.</summary>
    public async Task DiscoverAsync(IReadOnlyList<SolutionTreeNode> testProjects)
    {
        foreach (var project in testProjects)
        {
            if (project.FullPath is not { } projectPath)
            {
                continue;
            }

            IReadOnlyList<TestCase> cases;
            try
            {
                cases = await _testDiscoveryService.DiscoverAsync(projectPath);
            }
            catch (Exception)
            {
                continue; // best-effort - one project's discovery failing shouldn't drop the rest
            }

            RemoveExistingProjectNode(projectPath);

            var projectNode = new TestTreeNode(Path.GetFileNameWithoutExtension(projectPath), TestTreeNodeKind.Project)
            {
                ProjectPath = projectPath,
            };

            foreach (var byClass in cases.GroupBy(c => c.ClassName))
            {
                var classNode = new TestTreeNode(byClass.Key, TestTreeNodeKind.Class) { ProjectPath = projectPath };
                foreach (var testCase in byClass)
                {
                    var methodNode = new TestTreeNode(testCase.MethodName, TestTreeNodeKind.Method)
                    {
                        ProjectPath = projectPath,
                        FullyQualifiedName = testCase.FullyQualifiedName,
                    };
                    classNode.Children.Add(methodNode);
                    _leavesByFullyQualifiedName[testCase.FullyQualifiedName] = methodNode;
                }

                projectNode.Children.Add(classNode);
            }

            Tree.Add(projectNode);
        }

        RecomputeSummary();
        NotifyRunCommandsCanExecuteChanged();
    }

    private void RemoveExistingProjectNode(string projectPath)
    {
        var existing = Tree.FirstOrDefault(n => n.ProjectPath == projectPath);
        if (existing is null)
        {
            return;
        }

        foreach (var leaf in existing.Flatten().Where(n => n.Kind == TestTreeNodeKind.Method && n.FullyQualifiedName is not null))
        {
            _leavesByFullyQualifiedName.Remove(leaf.FullyQualifiedName!);
        }

        Tree.Remove(existing);
    }

    private bool CanRun() => !IsBusy && Tree.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAllAsync() => RunLeavesAsync(AllLeaves());

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunSelectedAsync()
    {
        if (SelectedNode is not { } node)
        {
            return Task.CompletedTask;
        }

        var leaves = node.Kind == TestTreeNodeKind.Method
            ? [node]
            : node.Flatten().Where(n => n.Kind == TestTreeNodeKind.Method);

        return RunLeavesAsync(leaves);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunFailedAsync() => RunLeavesAsync(AllLeaves().Where(n => n.Outcome == TestOutcome.Failed));

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _testRunService.Cancel();

    private IEnumerable<TestTreeNode> AllLeaves() =>
        Tree.SelectMany(project => project.Flatten()).Where(n => n.Kind == TestTreeNodeKind.Method);

    /// <summary>Groups the given leaves back by owning project (kept from discovery, not walked from
    /// the tree - TestTreeNode has no parent pointer) and runs each project's filtered set
    /// sequentially, IsBusy-gated so overlapping runs cannot start.</summary>
    private async Task RunLeavesAsync(IEnumerable<TestTreeNode> leaves)
    {
        var groups = leaves
            .Where(n => n.ProjectPath is not null && n.FullyQualifiedName is not null)
            .GroupBy(n => n.ProjectPath!)
            .ToList();

        if (groups.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            foreach (var group in groups)
            {
                var fullyQualifiedNames = group.Select(n => n.FullyQualifiedName!).ToList();

                IReadOnlyList<TestResult> results;
                try
                {
                    results = await _testRunService.RunAsync(group.Key, fullyQualifiedNames, _ => { });
                }
                catch (Exception)
                {
                    continue; // one project's run failing shouldn't stop the rest
                }

                foreach (var result in results)
                {
                    if (_leavesByFullyQualifiedName.TryGetValue(result.FullyQualifiedName, out var leaf))
                    {
                        leaf.ApplyResult(result);
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        RecomputeSummary();
    }

    private void RecomputeSummary()
    {
        var leaves = AllLeaves().ToList();
        if (leaves.Count == 0)
        {
            Summary = "No tests discovered.";
            return;
        }

        var passed = leaves.Count(n => n.Outcome == TestOutcome.Passed);
        var failed = leaves.Count(n => n.Outcome == TestOutcome.Failed);
        var skipped = leaves.Count(n => n.Outcome == TestOutcome.Skipped);
        var notRun = leaves.Count(n => n.Outcome == TestOutcome.NotRun);
        Summary = $"{passed} passed, {failed} failed, {skipped} skipped, {notRun} not run";
    }

    partial void OnIsBusyChanged(bool value) => NotifyRunCommandsCanExecuteChanged();

    private void NotifyRunCommandsCanExecuteChanged()
    {
        RunAllCommand.NotifyCanExecuteChanged();
        RunSelectedCommand.NotifyCanExecuteChanged();
        RunFailedCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
