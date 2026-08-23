using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Debug;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.NuGet;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Run;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.Shell.Views;
using Microsoft.Win32;

namespace IdePlusPlus.Shell;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Public so SettingsViewModel can read/write the same key - mirrors how
    /// CopilotChatPaneViewModel.SystemPromptSettingKey is shared with SettingsViewModel. Defaults to
    /// on when unset (see the inverted TryParse idiom in LoadSolutionAsync).</summary>
    public const string RestoreOnSolutionLoadSettingKey = "RestoreNuGetOnSolutionLoad";

    private static readonly string NetcoredbgPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "tools", "netcoredbg", "netcoredbg", "netcoredbg.exe");

    private readonly EditorWorkspace _editorWorkspace;
    private readonly BuildService _buildService;
    private readonly RunService _runService;
    private readonly DebugSessionManager _debugSessionManager;
    private readonly ThemeService _themeService;
    private readonly McpServerManager _mcpServerManager;
    private readonly NuGetService _nuGetService;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly Dispatcher _dispatcher;

    private string? _runnableProjectPath;
    private string? _runnableTargetFramework;
    private LaunchProfile? _currentLaunchProfile;
    private bool _debugBrowserLaunched;
    private SolutionTreeNode? _solutionRoot;

    /// <summary>The checked-for-debug set of startup projects (F5 launches every one of these as its
    /// own <see cref="DebugSession"/>). Deliberately separate from <see cref="_runnableProjectPath"/>,
    /// which stays the single Run/F5-without-debugger target per the design's laterScope - Run does
    /// not become multi-project. Every solution that has never used the multi-select checkbox UI has
    /// exactly one entry here (mirroring today's single startup project), so the StartDebuggingAsync
    /// loop below executes identically in substance to the old single-project code for that case.</summary>
    private readonly List<string> _startupProjectPaths = [];

    public MainWindowViewModel(
        SolutionExplorerPaneViewModel solutionExplorer,
        OutputPaneViewModel output,
        ErrorListPaneViewModel errorList,
        CopilotChatPaneViewModel copilotChat,
        SourceControlPaneViewModel sourceControl,
        NuGetPaneViewModel nuGet,
        WelcomeDocumentViewModel welcome,
        CallStackPaneViewModel callStack,
        LocalsPaneViewModel locals,
        EditorWorkspace editorWorkspace,
        BuildService buildService,
        RunService runService,
        DebugSessionManager debugSessionManager,
        ThemeService themeService,
        McpServerManager mcpServerManager,
        NuGetService nuGetService,
        AppSettingsStore appSettingsStore)
    {
        SolutionExplorer = solutionExplorer;
        Output = output;
        ErrorList = errorList;
        CopilotChat = copilotChat;
        SourceControl = sourceControl;
        NuGet = nuGet;
        Welcome = welcome;
        CallStack = callStack;
        Locals = locals;
        _editorWorkspace = editorWorkspace;
        _buildService = buildService;
        _runService = runService;
        _debugSessionManager = debugSessionManager;
        _themeService = themeService;
        _mcpServerManager = mcpServerManager;
        _nuGetService = nuGetService;
        _appSettingsStore = appSettingsStore;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        isDarkTheme = _themeService.IsDark;
        _themeService.Changed += () => _dispatcher.Invoke(() => IsDarkTheme = _themeService.IsDark);

        isWordWrapEnabled = EditorSettings.WordWrap;
        EditorSettings.Changed += () => _dispatcher.Invoke(() => IsWordWrapEnabled = EditorSettings.WordWrap);

        _debugSessionManager.StateChanged += () => _dispatcher.Invoke(OnDebugStateChanged);
        _debugSessionManager.OutputReceived += (session, line) => _dispatcher.Invoke(() =>
        {
            var trimmed = line.TrimEnd('\r', '\n');

            // Single-session (today's only case) output stays byte-for-byte unchanged; only once a
            // second session is running does output get a "[DisplayName] " prefix to stay distinguishable.
            Output.AppendLine(_debugSessionManager.Sessions.Count > 1 ? $"[{session.DisplayName}] {trimmed}" : trimmed);

            if (!_debugBrowserLaunched && _currentLaunchProfile is { LaunchBrowser: true }
                && KestrelUrlDetector.TryExtractListeningUrl(trimmed) is { } url)
            {
                _debugBrowserLaunched = true;
                BrowserLauncher.Open(url);
            }
        });
        // SessionsChanged must also recompute IsDebugging/IsStoppedAtBreakpoint, not just StateChanged:
        // DebugSessionManager.StartAsync/AttachAsync fire the new session's own StateChanged (which
        // reaches OnDebugStateChanged via the subscription above) BEFORE the session is actually added
        // to Sessions/made active - so IsAnyDebugging would still read false at that point. Once
        // AddSession runs and raises SessionsChanged, this re-evaluates with the now-current state.
        _debugSessionManager.SessionsChanged += () => _dispatcher.Invoke(() =>
        {
            SyncDebugSessions();
            OnDebugStateChanged();
        });
        _debugSessionManager.ActiveSessionChanged += () => _dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(ActiveDebugSession));
            _ = RefreshCallStackAndLocalsAsync();
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

    public NuGetPaneViewModel NuGet { get; }

    public WelcomeDocumentViewModel Welcome { get; }

    public CallStackPaneViewModel CallStack { get; }

    public LocalsPaneViewModel Locals { get; }

    /// <summary>Every currently-running debug session (launched or attached) - MVP session-picker UI
    /// binds to this to let the user choose which one Call Stack/Locals/Continue/Step act on. Has
    /// exactly one entry (or zero) in the single-startup-project, no-attach case. This is a UI-thread
    /// mirror of <see cref="DebugSessionManager.Sessions"/> kept in sync via <c>SessionsChanged</c> -
    /// not a direct passthrough, because the manager mutates its own collection from whatever thread
    /// a session's background StateChanged event fires on, and a WPF-bound ObservableCollection can
    /// only be touched from the UI thread.</summary>
    public ObservableCollection<DebugSession> DebugSessions { get; } = [];

    /// <summary>Two-way bindable wrapper around <see cref="DebugSessionManager.ActiveSession"/> for
    /// the session-picker ComboBox. The actual PropertyChanged/refresh happens uniformly through the
    /// <c>ActiveSessionChanged</c> subscription below, whether the change originated here (user picks
    /// a session) or inside the manager itself (e.g. auto-picking a new active session after one ends).</summary>
    public DebugSession? ActiveDebugSession
    {
        get => _debugSessionManager.ActiveSession;
        set => _debugSessionManager.SetActiveSession(value);
    }

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
    private bool isWordWrapEnabled;

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
    private void ToggleWordWrap() => EditorSettings.ToggleWordWrap();

    [RelayCommand]
    private void ZoomIn() => EditorSettings.ZoomIn();

    [RelayCommand]
    private void ZoomOut() => EditorSettings.ZoomOut();

    [RelayCommand]
    private void ResetZoom() => EditorSettings.ResetZoom();

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

            // The checked-for-debug set is independent of the single Run/F5-without-debugger target
            // above. Falls back to just the single startup project when the new multi-select store is
            // empty (a solution that has never used the checkbox UI) - this is the guarantee that a
            // single-startup-project solution debug-launches its one project exactly as before.
            _startupProjectPaths.Clear();
            var savedStartupProjectPaths = solutionDirectory is not null ? StartupProjectsStore.Load(solutionDirectory) : [];
            if (savedStartupProjectPaths.Count > 0)
            {
                _startupProjectPaths.AddRange(savedStartupProjectPaths);
            }
            else if (_runnableProjectPath is not null)
            {
                _startupProjectPaths.Add(_runnableProjectPath);
            }

            RunCommand.NotifyCanExecuteChanged();
            StartDebuggingCommand.NotifyCanExecuteChanged();
            Output.AppendLine("Solution loaded.");

            // Default-on: only skipped if the user has explicitly turned it off in Settings.
            var restoreOnLoad = !bool.TryParse(_appSettingsStore.Get(RestoreOnSolutionLoadSettingKey), out var savedRestore) || savedRestore;
            if (restoreOnLoad)
            {
                _ = NuGetRestoreOnLoadAsync(solutionPath);
            }
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Failed to load solution: {ex.Message}");
        }
    }

    /// <summary>Fire-and-forget from LoadSolutionAsync - a restore failure or exception here must
    /// never break solution loading, just get logged to Output.</summary>
    private async Task NuGetRestoreOnLoadAsync(string solutionPath)
    {
        try
        {
            var (success, output) = await _nuGetService.RestoreAsync(solutionPath, line => _dispatcher.Invoke(() => Output.AppendLine(line)));
            Output.AppendLine(success ? "NuGet restore completed." : "NuGet restore failed.");
        }
        catch (Exception ex)
        {
            Output.AppendLine($"NuGet restore failed: {ex.Message}");
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

    /// <summary>Whether <paramref name="node"/> is in the checked-for-debug set - backs the "Include
    /// in Startup Projects" checkbox in the Solution Explorer Project node's context menu.</summary>
    public bool IsStartupProject(SolutionTreeNode node) =>
        node.FullPath is not null && _startupProjectPaths.Contains(node.FullPath);

    /// <summary>Adds or removes <paramref name="node"/> from the checked-for-debug set, persisting the
    /// choice for next time this solution opens. Independent of <see cref="SetStartupProject"/>'s
    /// single Run target - a project can be in the multi-project debug set without being the Run
    /// target, and vice versa.</summary>
    public void ToggleStartupProject(SolutionTreeNode node)
    {
        if (node.Kind != SolutionTreeNodeKind.Project || node.FullPath is null)
        {
            return;
        }

        if (!_startupProjectPaths.Remove(node.FullPath))
        {
            _startupProjectPaths.Add(node.FullPath);
        }

        StartDebuggingCommand.NotifyCanExecuteChanged();

        if (Path.GetDirectoryName(CurrentSolutionPath) is { } solutionDirectory)
        {
            StartupProjectsStore.Save(solutionDirectory, _startupProjectPaths);
        }
    }

    /// <summary>Called from the Solution Explorer Project node's "Manage NuGet Packages..." context
    /// menu item. There's no existing pattern anywhere in this codebase for programmatically
    /// activating/focusing an AvalonDock anchorable (ErrorList/SourceControl don't do it either) - so
    /// this is MVP-scoped to just populating the pane; the user clicks its tab manually.</summary>
    public void OpenNuGetPane(SolutionTreeNode node) => _ = NuGet.SetProjectAsync(node);

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
        IsDebugging = _debugSessionManager.IsAnyDebugging;
        IsStoppedAtBreakpoint = _debugSessionManager.IsActiveSessionStopped;
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

    /// <summary>Mirrors <see cref="DebugSessionManager.Sessions"/> into the UI-thread-owned
    /// <see cref="DebugSessions"/> collection - always invoked via <c>_dispatcher.Invoke</c>, never
    /// called directly off the manager's background-thread <c>SessionsChanged</c> event.</summary>
    private void SyncDebugSessions()
    {
        DebugSessions.Clear();
        foreach (var session in _debugSessionManager.Sessions)
        {
            DebugSessions.Add(session);
        }
    }

    private async Task RefreshCallStackAndLocalsAsync()
    {
        if (_debugSessionManager.ActiveSession is not { } activeSession)
        {
            CallStack.Frames.Clear();
            Locals.Variables.Clear();
            return;
        }

        var frames = await activeSession.GetStackTraceAsync();
        CallStack.Frames.Clear();
        foreach (var frame in frames)
        {
            CallStack.Frames.Add(frame);
        }

        Locals.Variables.Clear();
        if (frames.Count > 0)
        {
            var variables = await activeSession.GetLocalsAsync(frames[0].Id);
            foreach (var variable in variables)
            {
                Locals.Variables.Add(variable);
            }
        }
    }

    private bool CanStartDebugging() => _startupProjectPaths.Count > 0 && !IsDebugging;

    /// <summary>Launches every checked-for-debug startup project as its own <see cref="DebugSession"/>.
    /// A solution that has never opted into multi-project debug has exactly one entry in
    /// <see cref="_startupProjectPaths"/>, so this loop runs its body once and produces output
    /// text identical to the old single-project implementation. A project that fails to build is
    /// logged and skipped, not treated as fatal to the rest - each is an independent session.</summary>
    [RelayCommand(CanExecute = nameof(CanStartDebugging))]
    private async Task StartDebuggingAsync()
    {
        Output.Clear();
        ErrorList.Clear();

        var startupProjectPaths = _startupProjectPaths.ToList();
        var isSingleProject = startupProjectPaths.Count == 1;
        _debugBrowserLaunched = false;

        foreach (var projectPath in startupProjectPaths)
        {
            Output.AppendLine(isSingleProject ? "Building for debug..." : $"Building for debug: {projectPath}...");

            var targetFramework = _solutionRoot?.Flatten()
                .FirstOrDefault(n => n.Kind == SolutionTreeNodeKind.Project && n.FullPath == projectPath)
                ?.TargetFramework;

            var dllPath = await ResolveOutputDllAsync(projectPath, targetFramework);
            if (dllPath is null)
            {
                Output.AppendLine(isSingleProject
                    ? "Build failed; cannot start debugging."
                    : $"Build failed; cannot start debugging: {projectPath}");
                continue;
            }

            _currentLaunchProfile = LaunchSettingsLoader.Load(projectPath);

            Output.AppendLine($"Starting debugger: {dllPath}");
            try
            {
                await _debugSessionManager.StartAsync(
                    NetcoredbgPath,
                    dllPath,
                    Path.GetDirectoryName(projectPath)!,
                    _currentLaunchProfile?.EnvironmentVariables);
            }
            catch (Exception ex)
            {
                Output.AppendLine(isSingleProject
                    ? $"Failed to start debugging: {ex.Message}"
                    : $"Failed to start debugging {projectPath}: {ex.Message}");
            }
        }
    }

    /// <summary>Shows the Attach to Process dialog and, on confirmation, attaches a new
    /// <see cref="DebugSession"/> to the chosen process. An attach failure (e.g. insufficient
    /// privileges) is logged to Output the same way a failed StartAsync is, not left to throw.</summary>
    [RelayCommand]
    private async Task AttachToProcessAsync()
    {
        var dialogViewModel = new AttachToProcessDialogViewModel();
        var dialog = new AttachToProcessDialog(dialogViewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true || dialogViewModel.SelectedCandidate is not { } selectedCandidate)
        {
            return;
        }

        Output.AppendLine($"Attaching to process {selectedCandidate.ProcessId} ({selectedCandidate.ProcessName})...");
        try
        {
            await _debugSessionManager.AttachAsync(NetcoredbgPath, selectedCandidate.ProcessId);
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Failed to attach to process {selectedCandidate.ProcessId}: {ex.Message}");
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

    // Each of these acts on the active session only. The CanExecute guards (IsDebugging /
    // IsStoppedAtBreakpoint) should already prevent a click reaching here with no active session in
    // practice, but a session can end asynchronously between an enabled button and the click handler
    // running, so every one of these null-guards defensively anyway.

    [RelayCommand(CanExecute = nameof(IsDebugging))]
    private Task StopDebuggingAsync() =>
        _debugSessionManager.ActiveSession is { } session ? _debugSessionManager.StopAsync(session) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task ContinueDebuggingAsync() => _debugSessionManager.ActiveSession?.ContinueAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepOverAsync() => _debugSessionManager.ActiveSession?.StepOverAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepIntoAsync() => _debugSessionManager.ActiveSession?.StepIntoAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepOutAsync() => _debugSessionManager.ActiveSession?.StepOutAsync() ?? Task.CompletedTask;
}
