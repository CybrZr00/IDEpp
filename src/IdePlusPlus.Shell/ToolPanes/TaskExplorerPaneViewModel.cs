using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Tasks;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Manifest&gt;task tree over the solution's package.json scripts, mirroring
/// TestExplorerPaneViewModel's IsBusy-gated RelayCommand shape. Discovery inputs are pushed in by
/// MainWindowViewModel on solution load (this pane does not know how a solution is loaded); the
/// scan itself runs here so the Refresh button can re-run it without a round trip.
/// </summary>
public sealed partial class TaskExplorerPaneViewModel : ToolPaneViewModel
{
    private readonly TaskRunService _taskRunService;

    private string? _solutionDirectory;
    private IReadOnlyList<string> _solutionFilePaths = [];

    public const string PaneContentId = "TaskRunner";

    public TaskExplorerPaneViewModel(TaskRunService taskRunService)
        : base(PaneContentId, "Task Runner") =>
        _taskRunService = taskRunService;

    /// <summary>
    /// Where task output goes - set once by MainWindowViewModel, which owns the Output pane and
    /// does the dispatching. Task output arrives on whatever thread the process's
    /// OutputDataReceived fires on, so this is invoked off the UI thread and the handler must
    /// marshal.
    /// </summary>
    public Action<string>? OutputLine { get; set; }

    public ObservableCollection<TaskTreeNode> Tree { get; } = [];

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? summary = "No solution loaded.";

    [ObservableProperty]
    private TaskTreeNode? selectedNode;

    public Task SetSolutionAsync(string? solutionDirectory, IReadOnlyList<string> solutionFilePaths)
    {
        _solutionDirectory = solutionDirectory;
        _solutionFilePaths = solutionFilePaths;
        return LoadManifestsAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadManifestsAsync();

    private async Task LoadManifestsAsync()
    {
        // Captured before the await so the background scan cannot see a half-applied newer solution.
        var solutionDirectory = _solutionDirectory;
        var solutionFilePaths = _solutionFilePaths;

        IReadOnlyList<TaskManifest> manifests;
        try
        {
            manifests = await Task.Run(() => TaskManifestDiscovery.Discover(solutionDirectory, solutionFilePaths));
        }
        catch (Exception ex)
        {
            OutputLine?.Invoke($"Task discovery failed: {ex.Message}");
            return;
        }

        Tree.Clear();
        foreach (var manifest in manifests)
        {
            Tree.Add(TaskTreeNode.ForManifest(manifest));
        }

        RecomputeSummary();
        NotifyCommandsCanExecuteChanged();
    }

    private bool CanRunSelected() => !IsBusy && SelectedNode is { Kind: TaskTreeNodeKind.Task };

    [RelayCommand(CanExecute = nameof(CanRunSelected))]
    private Task RunSelectedAsync() => RunAsync(SelectedNode!);

    /// <summary>Also the double-click entry point - see MainWindow.xaml.cs.</summary>
    public async Task RunAsync(TaskTreeNode node)
    {
        if (IsBusy || node.Definition is not { } task)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var command = $"{node.PackageManager.ExecutableName()} run {task.Name}";
            OutputLine?.Invoke($"Task started ({command}): {node.ManifestPath}");

            Summary = $"Running {task.Name}...";
            var exitCode = await _taskRunService.RunAsync(
                task, node.PackageManager, line => OutputLine?.Invoke($"[{task.Name}] {line}"));

            OutputLine?.Invoke($"Task finished with exit code {exitCode}: {command}");
        }
        catch (InvalidOperationException ex)
        {
            // Same wording shape as MainWindowViewModel's build-verb guard.
            OutputLine?.Invoke($"Task skipped - {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            RecomputeSummary();
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _taskRunService.Cancel();

    private void RecomputeSummary()
    {
        var taskCount = Tree.Sum(manifest => manifest.Children.Count);
        Summary = taskCount == 0
            ? "No package.json scripts found."
            : $"{taskCount} {(taskCount == 1 ? "task" : "tasks")} in {Tree.Count} {(Tree.Count == 1 ? "manifest" : "manifests")}";
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommandsCanExecuteChanged();

    partial void OnSelectedNodeChanged(TaskTreeNode? value) => NotifyCommandsCanExecuteChanged();

    private void NotifyCommandsCanExecuteChanged()
    {
        RunSelectedCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
