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
///
/// Also owns the build bindings: which tasks run at which lifecycle point, persisted per solution.
/// MainWindowViewModel drives them via <see cref="RunBoundTasksAsync"/> at the right moments - this
/// pane knows what is bound, not when a build happens.
/// </summary>
public sealed partial class TaskExplorerPaneViewModel : ToolPaneViewModel
{
    /// <summary>
    /// How long a bound task gets before it is stopped and the build carries on without it.
    ///
    /// Bound tasks need a limit that a manually-run task does not: a manual run has a Stop button
    /// and a user watching it, while a bound one runs inside a build that would otherwise wait
    /// forever. `npm run watch` bound to Before Build is the obvious way to hit this, which is why
    /// binding a watch-looking task also warns.
    /// </summary>
    public static readonly TimeSpan BoundTaskTimeout = TimeSpan.FromMinutes(2);

    private readonly TaskRunService _taskRunService;

    private string? _solutionDirectory;
    private IReadOnlyList<string> _solutionFilePaths = [];
    private IReadOnlyList<TaskManifest> _manifests = [];
    private List<TaskBinding> _bindings = [];

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
        _bindings = solutionDirectory is null ? [] : TaskBindingStore.Load(solutionDirectory).ToList();
        return LoadManifestsAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadManifestsAsync();

    private async Task LoadManifestsAsync()
    {
        // Captured before the await so the background scan cannot see a half-applied newer solution.
        var solutionDirectory = _solutionDirectory;
        var solutionFilePaths = _solutionFilePaths;

        try
        {
            _manifests = await Task.Run(() => TaskManifestDiscovery.Discover(solutionDirectory, solutionFilePaths));
        }
        catch (Exception ex)
        {
            OutputLine?.Invoke($"Task discovery failed: {ex.Message}");
            return;
        }

        RebuildTree();
    }

    private void RebuildTree()
    {
        var selectedTaskName = SelectedNode?.Name;
        var selectedManifest = SelectedNode?.ManifestPath;

        Tree.Clear();
        foreach (var manifest in _manifests)
        {
            Tree.Add(TaskTreeNode.ForManifest(manifest, BindingsFor(manifest.Path)));
        }

        // Rebuilding drops the old node objects, so the selection has to be re-established against
        // the new ones or the Run button silently disables itself after every binding change.
        SelectedNode = Tree
            .SelectMany(manifest => manifest.Children)
            .FirstOrDefault(task => task.Name == selectedTaskName && task.ManifestPath == selectedManifest);

        RecomputeSummary();
        NotifyCommandsCanExecuteChanged();
    }

    private IReadOnlyList<TaskBinding> BindingsFor(string manifestPath)
    {
        if (_solutionDirectory is not { } solutionDirectory)
        {
            return [];
        }

        var relative = TaskBindingStore.ToRelativePath(solutionDirectory, manifestPath);
        return _bindings
            .Where(b => string.Equals(b.ManifestRelativePath, relative, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private bool CanRunSelected() => !IsBusy && SelectedNode is { Kind: TaskTreeNodeKind.Task };

    [RelayCommand(CanExecute = nameof(CanRunSelected))]
    private Task RunSelectedAsync() => RunAsync(SelectedNode!);

    /// <summary>Also the double-click entry point - see MainWindow.xaml.cs.</summary>
    public Task RunAsync(TaskTreeNode node) => RunOneAsync(node, timeout: null);

    /// <summary>
    /// Runs everything bound to <paramref name="kind"/>, one task at a time, each under
    /// <see cref="BoundTaskTimeout"/>. Never throws: a bound task that fails is reported and the
    /// build carries on, matching what Visual Studio does and keeping a broken lint config from
    /// making the IDE unable to build.
    /// </summary>
    public async Task RunBoundTasksAsync(TaskBindingKind kind)
    {
        foreach (var node in BoundTaskNodes(kind))
        {
            await RunOneAsync(node, BoundTaskTimeout);
        }
    }

    private IReadOnlyList<TaskTreeNode> BoundTaskNodes(TaskBindingKind kind) =>
        Tree.SelectMany(manifest => manifest.Children)
            .Where(task => task.Bindings.Contains(kind))
            .ToList();

    private async Task RunOneAsync(TaskTreeNode node, TimeSpan? timeout)
    {
        if (IsBusy || node.Definition is not { } task)
        {
            return;
        }

        IsBusy = true;
        using var timeoutSource = timeout is { } limit ? new CancellationTokenSource(limit) : null;
        try
        {
            var command = $"{node.PackageManager.ExecutableName()} run {task.Name}";
            OutputLine?.Invoke($"Task started ({command}): {node.ManifestPath}");

            Summary = $"Running {task.Name}...";
            var exitCode = await _taskRunService.RunAsync(
                task,
                node.PackageManager,
                line => OutputLine?.Invoke($"[{task.Name}] {line}"),
                timeoutSource?.Token ?? CancellationToken.None);

            OutputLine?.Invoke(timeoutSource is { IsCancellationRequested: true }
                ? $"Task stopped after {timeout!.Value.TotalMinutes:0} minutes without finishing: {command}"
                : $"Task finished with exit code {exitCode}: {command}");
        }
        catch (OperationCanceledException)
        {
            // The timeout fired before the process even started; TaskRunService has stopped it.
            OutputLine?.Invoke($"Task stopped before it finished: {task.Name}");
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

    private bool CanToggleBinding() => SelectedNode is { Kind: TaskTreeNodeKind.Task } && _solutionDirectory is not null;

    /// <summary>
    /// Binds or unbinds the selected task at the named lifecycle point. Takes the kind as a string
    /// because it comes from a XAML CommandParameter; an unrecognized one is ignored rather than
    /// throwing into a menu click.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleBinding))]
    private void ToggleBinding(string? kindName)
    {
        if (SelectedNode is not { Kind: TaskTreeNodeKind.Task } node
            || node.Definition is not { } task
            || _solutionDirectory is not { } solutionDirectory
            || !Enum.TryParse<TaskBindingKind>(kindName, out var kind))
        {
            return;
        }

        var relative = TaskBindingStore.ToRelativePath(solutionDirectory, node.ManifestPath);
        var existing = _bindings.FirstOrDefault(b =>
            string.Equals(b.ManifestRelativePath, relative, StringComparison.OrdinalIgnoreCase)
            && b.TaskName == task.Name
            && b.Kind == kind);

        if (existing is not null)
        {
            _bindings.Remove(existing);
            OutputLine?.Invoke($"'{task.Name}' no longer runs at {TaskTreeNode.DisplayName(kind)}.");
        }
        else
        {
            _bindings.Add(new TaskBinding(relative, task.Name, kind));
            OutputLine?.Invoke($"'{task.Name}' will now run at {TaskTreeNode.DisplayName(kind)}.");

            if (WatchTaskDetector.LooksLikeAWatchTask(task))
            {
                OutputLine?.Invoke(
                    $"    Note: '{task.Name}' looks like a watch or server task. A task that never exits will be " +
                    $"stopped after {BoundTaskTimeout.TotalMinutes:0} minutes, delaying every build by that long.");
            }
        }

        TaskBindingStore.Save(solutionDirectory, _bindings);
        RebuildTree();
    }

    private void RecomputeSummary()
    {
        var taskCount = Tree.Sum(manifest => manifest.Children.Count);
        if (taskCount == 0)
        {
            Summary = "No package.json scripts found.";
            return;
        }

        var boundCount = Tree.SelectMany(manifest => manifest.Children).Count(task => task.Bindings.Count > 0);
        var summary = $"{taskCount} {(taskCount == 1 ? "task" : "tasks")} in {Tree.Count} {(Tree.Count == 1 ? "manifest" : "manifests")}";
        Summary = boundCount == 0 ? summary : $"{summary}, {boundCount} bound";
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommandsCanExecuteChanged();

    partial void OnSelectedNodeChanged(TaskTreeNode? value) => NotifyCommandsCanExecuteChanged();

    private void NotifyCommandsCanExecuteChanged()
    {
        RunSelectedCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ToggleBindingCommand.NotifyCanExecuteChanged();
    }
}
