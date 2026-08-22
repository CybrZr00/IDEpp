using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Debug;
using IdePlusPlus.Editor;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Run;
using IdePlusPlus.Shell.ToolPanes;
using Microsoft.Win32;

namespace IdePlusPlus.Shell;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly string NetcoredbgPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "tools", "netcoredbg", "netcoredbg", "netcoredbg.exe");

    private readonly EditorWorkspace _editorWorkspace;
    private readonly BuildService _buildService;
    private readonly RunService _runService;
    private readonly DebugSession _debugSession;
    private readonly ThemeService _themeService;
    private readonly McpServerManager _mcpServerManager;
    private readonly Dispatcher _dispatcher;

    private string? _runnableProjectPath;
    private string? _runnableTargetFramework;
    private LaunchProfile? _currentLaunchProfile;
    private bool _debugBrowserLaunched;
    private SolutionTreeNode? _solutionRoot;

    public MainWindowViewModel(
        SolutionExplorerPaneViewModel solutionExplorer,
        OutputPaneViewModel output,
        ErrorListPaneViewModel errorList,
        CopilotChatPaneViewModel copilotChat,
        SourceControlPaneViewModel sourceControl,
        WelcomeDocumentViewModel welcome,
        CallStackPaneViewModel callStack,
        LocalsPaneViewModel locals,
        EditorWorkspace editorWorkspace,
        BuildService buildService,
        RunService runService,
        DebugSession debugSession,
        ThemeService themeService,
        McpServerManager mcpServerManager)
    {
        SolutionExplorer = solutionExplorer;
        Output = output;
        ErrorList = errorList;
        CopilotChat = copilotChat;
        SourceControl = sourceControl;
        Welcome = welcome;
        CallStack = callStack;
        Locals = locals;
        _editorWorkspace = editorWorkspace;
        _buildService = buildService;
        _runService = runService;
        _debugSession = debugSession;
        _themeService = themeService;
        _mcpServerManager = mcpServerManager;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        isDarkTheme = _themeService.IsDark;
        _themeService.Changed += () => _dispatcher.Invoke(() => IsDarkTheme = _themeService.IsDark);

        _debugSession.StateChanged += () => _dispatcher.Invoke(OnDebugStateChanged);
        _debugSession.OutputReceived += line => _dispatcher.Invoke(() =>
        {
            var trimmed = line.TrimEnd('\r', '\n');
            Output.AppendLine(trimmed);

            if (!_debugBrowserLaunched && _currentLaunchProfile is { LaunchBrowser: true }
                && KestrelUrlDetector.TryExtractListeningUrl(trimmed) is { } url)
            {
                _debugBrowserLaunched = true;
                BrowserLauncher.Open(url);
            }
        });

        EditorServices.CaretPosition.Changed += () => _dispatcher.Invoke(() =>
            CursorPositionText = $"Ln {EditorServices.CaretPosition.Line}, Col {EditorServices.CaretPosition.Column}");
    }

    public SolutionExplorerPaneViewModel SolutionExplorer { get; }

    public OutputPaneViewModel Output { get; }

    public ErrorListPaneViewModel ErrorList { get; }

    public ObservableCollection<PluginMenuCommand> PluginCommands { get; } = [];

    public void AddPluginCommand(string header, Action execute) =>
        _dispatcher.Invoke(() => PluginCommands.Add(new PluginMenuCommand(header, execute)));

    public CopilotChatPaneViewModel CopilotChat { get; }

    public SourceControlPaneViewModel SourceControl { get; }

    public WelcomeDocumentViewModel Welcome { get; }

    public CallStackPaneViewModel CallStack { get; }

    public LocalsPaneViewModel Locals { get; }

    public ObservableCollection<DocumentViewModel> OpenDocuments => _editorWorkspace.OpenDocuments;

    public ObservableCollection<string> RecentFiles => _editorWorkspace.RecentFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private DocumentViewModel? activeDocument;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RebuildCommand))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string? currentSolutionPath;

    public string WindowTitle
    {
        get
        {
            var parts = new List<string>();
            if (ActiveDocument is { } document)
            {
                parts.Add(document.Title.TrimEnd('*'));
            }

            if (CurrentSolutionPath is { } path)
            {
                parts.Add(Path.GetFileNameWithoutExtension(path));
            }

            parts.Add("IDE++");
            return string.Join(" - ", parts);
        }
    }

    public ObservableCollection<string> RecentSolutions { get; } = new(RecentSolutionsStore.Load());

    public IReadOnlyList<string> AvailableConfigurations { get; } = ["Debug", "Release"];

    [ObservableProperty]
    private string configuration = "Debug";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool isRunning;

    [ObservableProperty]
    private bool isDarkTheme;

    [ObservableProperty]
    private string cursorPositionText = "Ln 1, Col 1";

    public string StatusBarText
    {
        get
        {
            if (IsStoppedAtBreakpoint)
            {
                return "Stopped at breakpoint";
            }

            if (IsDebugging)
            {
                return "Debugging";
            }

            return IsRunning ? "Running" : "Ready";
        }
    }

    [RelayCommand]
    private void ToggleTheme() => _themeService.Toggle();

    [RelayCommand]
    private void NewDocument() => ActiveDocument = _editorWorkspace.NewDocument();

    [RelayCommand]
    private void OpenDocument()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "All files (*.*)|*.*|C# files (*.cs)|*.cs|XAML/XML files (*.xaml;*.xml)|*.xaml;*.xml|JSON files (*.json)|*.json|Markdown files (*.md)|*.md",
        };

        if (dialog.ShowDialog() == true)
        {
            OpenPath(dialog.FileName);
        }
    }

    [RelayCommand]
    private void OpenRecentFile(string filePath) => OpenPath(filePath);

    public void OpenPath(string filePath)
    {
        var document = _editorWorkspace.Open(filePath);
        if (document is not null)
        {
            ActiveDocument = document;
        }
        else
        {
            Output.AppendLine($"Couldn't open '{filePath}' - it may have been moved, renamed, or deleted.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void Save() => SaveDocument(ActiveDocument!);

    private bool HasActiveDocument() => ActiveDocument is not null;

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void SaveAs() => SaveDocumentAs(ActiveDocument!);

    private bool HasOpenDocuments() => OpenDocuments.Count > 0;

    [RelayCommand(CanExecute = nameof(HasOpenDocuments))]
    private void SaveAll()
    {
        foreach (var document in OpenDocuments.Where(d => d.IsDirty).ToList())
        {
            SaveDocument(document);
        }
    }

    /// <summary>Called from the "Go to Line..." dialog's code-behind once the user confirms a line
    /// number - not a RelayCommand since it's never bound directly from XAML.</summary>
    public void GoToLine(int line)
    {
        if (ActiveDocument is { } document && line > 0)
        {
            EditorServices.GoToLine.Request(document, line);
        }
    }

    public void SaveDocument(DocumentViewModel document)
    {
        if (document.FilePath is null)
        {
            SaveDocumentAs(document);
        }
        else
        {
            _editorWorkspace.Save(document);
        }
    }

    public void SaveDocumentAs(DocumentViewModel document)
    {
        var dialog = new SaveFileDialog
        {
            FileName = document.FilePath is null ? "Untitled" : Path.GetFileName(document.FilePath),
        };

        if (dialog.ShowDialog() == true)
        {
            _editorWorkspace.SaveAs(document, dialog.FileName);
        }
    }

    public void CloseDocument(DocumentViewModel document) => _editorWorkspace.Close(document);

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        CopilotChat.SetActiveDocument(value);
    }

    [RelayCommand]
    private async Task OpenSolutionAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Solution files (*.slnx;*.sln)|*.slnx;*.sln" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await LoadSolutionAsync(dialog.FileName);
    }

    [RelayCommand]
    private Task OpenRecentSolutionAsync(string solutionPath) => LoadSolutionAsync(solutionPath);

    /// <summary>Reopens whatever solution was open last, if it still exists - called once from
    /// App.xaml.cs after the main window is shown.</summary>
    public async Task TryReopenLastSolutionOnStartupAsync()
    {
        if (RecentSolutionsStore.Load().FirstOrDefault() is { } lastSolutionPath && File.Exists(lastSolutionPath))
        {
            await LoadSolutionAsync(lastSolutionPath);
        }
    }

    public async Task LoadSolutionAsync(string solutionPath)
    {
        Output.Clear();
        Output.AppendLine($"Loading {solutionPath}...");
        try
        {
            var root = await Task.Run(() => SolutionLoader.Load(solutionPath));
            SolutionExplorer.SetSolution(root);
            CurrentSolutionPath = solutionPath;
            _solutionRoot = root;

            var recentSolutions = RecentSolutionsStore.Touch(solutionPath);
            RecentSolutions.Clear();
            foreach (var recentSolutionPath in recentSolutions)
            {
                RecentSolutions.Add(recentSolutionPath);
            }

            var solutionDirectory = Path.GetDirectoryName(solutionPath);
            CopilotChat.SetSolutionDirectory(solutionDirectory);
            _ = SourceControl.SetRepositoryDirectoryAsync(solutionDirectory);

            // Picks up a project-local .ideplusplus/copilot/mcp-servers.json, if the solution has one.
            _mcpServerManager.SetSolutionDirectory(solutionDirectory);
            _ = _mcpServerManager.ReconnectAllAsync();
            CopilotChat.SetIndexableFiles(root.Flatten()
                .Where(n => n.Kind == SolutionTreeNodeKind.File && n.FullPath is not null)
                .Select(n => n.FullPath!)
                .ToList());

            var projects = root.Flatten().Where(n => n.Kind == SolutionTreeNodeKind.Project).ToList();
            var savedStartupPath = solutionDirectory is not null ? StartupProjectStore.Load(solutionDirectory) : null;
            var startupProject = projects.FirstOrDefault(p => p.FullPath == savedStartupPath) ?? projects.FirstOrDefault();

            if (startupProject is not null)
            {
                startupProject.IsStartupProject = true;
            }

            _runnableProjectPath = startupProject?.FullPath;
            _runnableTargetFramework = startupProject?.TargetFramework;
            RunCommand.NotifyCanExecuteChanged();
            StartDebuggingCommand.NotifyCanExecuteChanged();
            Output.AppendLine("Solution loaded.");
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Failed to load solution: {ex.Message}");
        }
    }

    /// <summary>Marks a project as the one Run/Debug act on, persisting the choice for next time this solution opens.</summary>
    public void SetStartupProject(SolutionTreeNode node)
    {
        if (node.Kind != SolutionTreeNodeKind.Project || node.FullPath is null || _solutionRoot is null)
        {
            return;
        }

        foreach (var project in _solutionRoot.Flatten().Where(n => n.Kind == SolutionTreeNodeKind.Project))
        {
            project.IsStartupProject = ReferenceEquals(project, node);
        }

        _runnableProjectPath = node.FullPath;
        _runnableTargetFramework = node.TargetFramework;
        RunCommand.NotifyCanExecuteChanged();
        StartDebuggingCommand.NotifyCanExecuteChanged();

        if (Path.GetDirectoryName(CurrentSolutionPath) is { } solutionDirectory)
        {
            StartupProjectStore.Save(solutionDirectory, node.FullPath);
        }

        Output.AppendLine($"Startup project set to {node.Name}.");
    }

    public void OpenTreeNode(SolutionTreeNode node)
    {
        if (node.Kind == SolutionTreeNodeKind.File && node.FullPath is not null)
        {
            OpenPath(node.FullPath);
        }
    }

    /// <summary>Diagnostic.FilePath is exactly as MSBuild reported it - relative to the solution's
    /// directory unless the project used an absolute path itself.</summary>
    public void OpenDiagnostic(BuildDiagnostic diagnostic)
    {
        var baseDirectory = Path.GetDirectoryName(CurrentSolutionPath) ?? Directory.GetCurrentDirectory();
        var fullPath = Path.IsPathRooted(diagnostic.FilePath)
            ? diagnostic.FilePath
            : Path.GetFullPath(Path.Combine(baseDirectory, diagnostic.FilePath));

        OpenPath(fullPath);
    }

    private bool HasSolution() => CurrentSolutionPath is not null;

    private void OnBuildOutputLine(string line) => _dispatcher.Invoke(() =>
    {
        Output.AppendLine(line);
        ErrorList.AddIfDiagnostic(line);
    });

    [RelayCommand(CanExecute = nameof(HasSolution))]
    private async Task BuildAsync()
    {
        Output.Clear();
        ErrorList.Clear();
        Output.AppendLine($"Build started ({Configuration}): {CurrentSolutionPath}");
        var exitCode = await _buildService.BuildAsync(CurrentSolutionPath!, OnBuildOutputLine, configuration: Configuration);
        Output.AppendLine($"Build finished with exit code {exitCode}.");
    }

    [RelayCommand(CanExecute = nameof(HasSolution))]
    private async Task CleanAsync()
    {
        Output.Clear();
        ErrorList.Clear();
        Output.AppendLine($"Clean started ({Configuration}): {CurrentSolutionPath}");
        var exitCode = await _buildService.CleanAsync(CurrentSolutionPath!, OnBuildOutputLine, configuration: Configuration);
        Output.AppendLine($"Clean finished with exit code {exitCode}.");
    }

    [RelayCommand(CanExecute = nameof(HasSolution))]
    private async Task RebuildAsync()
    {
        Output.Clear();
        ErrorList.Clear();
        Output.AppendLine($"Rebuild started ({Configuration}): {CurrentSolutionPath}");
        var exitCode = await _buildService.RebuildAsync(CurrentSolutionPath!, OnBuildOutputLine, configuration: Configuration);
        Output.AppendLine($"Rebuild finished with exit code {exitCode}.");
    }

    private bool CanRun() => _runnableProjectPath is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        Output.Clear();
        Output.AppendLine($"Running ({Configuration}): {_runnableProjectPath}");
        IsRunning = true;
        _runService.Start(
            _runnableProjectPath!,
            line => _dispatcher.Invoke(() => Output.AppendLine(line)),
            exitCode => _dispatcher.Invoke(() =>
            {
                Output.AppendLine($"Process exited with code {exitCode}.");
                IsRunning = false;
            }),
            _runnableTargetFramework,
            Configuration);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _runService.Stop();

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusBarText));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDebuggingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopDebuggingCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueDebuggingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepOverCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepIntoCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepOutCommand))]
    private bool isDebugging;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueDebuggingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepOverCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepIntoCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepOutCommand))]
    private bool isStoppedAtBreakpoint;

    private void OnDebugStateChanged()
    {
        IsDebugging = _debugSession.IsActive;
        IsStoppedAtBreakpoint = _debugSession.IsStopped;
        OnPropertyChanged(nameof(StatusBarText));

        if (IsStoppedAtBreakpoint)
        {
            _ = RefreshCallStackAndLocalsAsync();
        }
        else
        {
            CallStack.Frames.Clear();
            Locals.Variables.Clear();
        }
    }

    private async Task RefreshCallStackAndLocalsAsync()
    {
        var frames = await _debugSession.GetStackTraceAsync();
        CallStack.Frames.Clear();
        foreach (var frame in frames)
        {
            CallStack.Frames.Add(frame);
        }

        Locals.Variables.Clear();
        if (frames.Count > 0)
        {
            var variables = await _debugSession.GetLocalsAsync(frames[0].Id);
            foreach (var variable in variables)
            {
                Locals.Variables.Add(variable);
            }
        }
    }

    private bool CanStartDebugging() => _runnableProjectPath is not null && !IsDebugging;

    [RelayCommand(CanExecute = nameof(CanStartDebugging))]
    private async Task StartDebuggingAsync()
    {
        Output.Clear();
        ErrorList.Clear();
        Output.AppendLine("Building for debug...");
        var dllPath = await ResolveOutputDllAsync(_runnableProjectPath!, _runnableTargetFramework);
        if (dllPath is null)
        {
            Output.AppendLine("Build failed; cannot start debugging.");
            return;
        }

        _currentLaunchProfile = LaunchSettingsLoader.Load(_runnableProjectPath!);
        _debugBrowserLaunched = false;

        Output.AppendLine($"Starting debugger: {dllPath}");
        try
        {
            await _debugSession.StartAsync(
                NetcoredbgPath,
                dllPath,
                Path.GetDirectoryName(_runnableProjectPath)!,
                _currentLaunchProfile?.EnvironmentVariables);
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Failed to start debugging: {ex.Message}");
        }
    }

    private async Task<string?> ResolveOutputDllAsync(string projectPath, string? targetFramework)
    {
        var exitCode = await _buildService.BuildAsync(projectPath, OnBuildOutputLine, targetFramework, Configuration);
        if (exitCode != 0)
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var binDirectory = Path.Combine(projectDirectory, "bin", Configuration);

        // For multi-targeted projects, narrow the search to the TFM that was actually built -
        // bin/Debug can otherwise hold stale output from a previously-built TFM (e.g. a mobile
        // one) that could win the "newest file" search below by coincidence.
        var searchDirectory = targetFramework is not null ? Path.Combine(binDirectory, targetFramework) : binDirectory;
        if (!Directory.Exists(searchDirectory))
        {
            searchDirectory = binDirectory;
        }

        if (!Directory.Exists(searchDirectory))
        {
            return null;
        }

        return Directory
            .GetFiles(searchDirectory, $"{projectName}.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(IsDebugging))]
    private Task StopDebuggingAsync() => _debugSession.StopAsync();

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task ContinueDebuggingAsync() => _debugSession.ContinueAsync();

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepOverAsync() => _debugSession.StepOverAsync();

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepIntoAsync() => _debugSession.StepIntoAsync();

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepOutAsync() => _debugSession.StepOutAsync();
}
