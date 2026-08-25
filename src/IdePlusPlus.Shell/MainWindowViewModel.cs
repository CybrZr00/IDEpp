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
using IdePlusPlus.Roslyn;
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
    private readonly RoslynWorkspaceService _roslynWorkspaceService;
    private readonly Dispatcher _dispatcher;

    private LaunchProfile? _currentLaunchProfile;
    private bool _debugBrowserLaunched;
    private SolutionTreeNode? _solutionRoot;

    /// <summary>Every named startup configuration saved for the current solution (see
    /// <see cref="StartupConfigurationStore"/>) - what the Startup Configuration Manager dialog edits.
    /// Run/Start Debugging always act on <see cref="ActiveConfiguration"/>, never on this list directly.</summary>
    private List<StartupConfiguration> _startupConfigurations = [];

    private string? _activeConfigurationName;

    /// <summary>Extra <see cref="RunService"/> instances for this launch's Action.StartWithoutDebugging
    /// entries - separate from the DI-singleton <see cref="_runService"/> (which only ever tracks the
    /// single quick-Run/Stop toolbar target) because more than one of these can run concurrently
    /// alongside each other and alongside debug sessions. Entries remove themselves on process exit;
    /// see StartDebuggingAsync/StopDebuggingAsync.</summary>
    private readonly List<RunService> _multiProjectRunServices = [];

    private StartupConfiguration? ActiveConfiguration => _startupConfigurations.FirstOrDefault(c => c.Name == _activeConfigurationName);

    public MainWindowViewModel(
        SolutionExplorerPaneViewModel solutionExplorer,
        OutputPaneViewModel output,
        ErrorListPaneViewModel errorList,
        CopilotChatPaneViewModel copilotChat,
        SourceControlPaneViewModel sourceControl,
        NuGetPaneViewModel nuGet,
        TestExplorerPaneViewModel testExplorer,
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
        AppSettingsStore appSettingsStore,
        RoslynWorkspaceService roslynWorkspaceService)
    {
        SolutionExplorer = solutionExplorer;
        Output = output;
        ErrorList = errorList;
        CopilotChat = copilotChat;
        SourceControl = sourceControl;
        NuGet = nuGet;
        TestExplorer = testExplorer;
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
        _roslynWorkspaceService = roslynWorkspaceService;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        currentTheme = _themeService.Current;
        _themeService.Changed += () => _dispatcher.Invoke(() => CurrentTheme = _themeService.Current);

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

        // Fires on whatever background thread RoslynWorkspaceService finished recomputing
        // diagnostics on - dispatched to the UI thread before touching the ObservableCollection,
        // same as every other cross-thread subscription above.
        _roslynWorkspaceService.DiagnosticsChanged += (filePath, diagnostics) =>
            _dispatcher.Invoke(() => ErrorList.UpsertLiveDiagnostics(filePath, diagnostics));
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

    public TestExplorerPaneViewModel TestExplorer { get; }

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

    /// <summary>Names of every saved startup configuration for the current solution, for the
    /// status-bar quick-switch ComboBox - kept in sync with <see cref="_startupConfigurations"/> by
    /// <see cref="SyncStartupConfigurationNames"/>.</summary>
    public ObservableCollection<string> StartupConfigurationNames { get; } = [];

    /// <summary>Two-way bindable wrapper around <see cref="_activeConfigurationName"/> for the
    /// status-bar quick-switch ComboBox - a hand-written property (not [ObservableProperty]) because
    /// switching configurations has side effects (persist, rebold Solution Explorer, re-evaluate
    /// Run/Start Debugging) beyond just raising PropertyChanged.</summary>
    public string? ActiveStartupConfigurationName
    {
        get => _activeConfigurationName;
        set
        {
            if (value is null || value == _activeConfigurationName || _startupConfigurations.All(c => c.Name != value))
            {
                return;
            }

            _activeConfigurationName = value;
            OnPropertyChanged();

            if (Path.GetDirectoryName(CurrentSolutionPath) is { } solutionDirectory)
            {
                StartupConfigurationStore.Save(solutionDirectory, _startupConfigurations, _activeConfigurationName);
            }

            if (_solutionRoot is not null)
            {
                foreach (var project in _solutionRoot.Flatten().Where(n => n.Kind == SolutionTreeNodeKind.Project))
                {
                    project.IsStartupProject = IsStartupProject(project);
                }
            }

            RunCommand.NotifyCanExecuteChanged();
            StartDebuggingCommand.NotifyCanExecuteChanged();
        }
    }

    private void SyncStartupConfigurationNames()
    {
        StartupConfigurationNames.Clear();
        foreach (var configurationName in _startupConfigurations.Select(c => c.Name))
        {
            StartupConfigurationNames.Add(configurationName);
        }

        OnPropertyChanged(nameof(ActiveStartupConfigurationName));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool isRunning;

    [ObservableProperty]
    private AppTheme currentTheme;

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
    private void SetTheme(AppTheme theme) => _themeService.SetTheme(theme);

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

            if (solutionDirectory is not null)
            {
                var (configurations, activeName) = StartupConfigurationStore.Load(solutionDirectory);
                _startupConfigurations = configurations.ToList();
                _activeConfigurationName = activeName;
            }
            else
            {
                _startupConfigurations = [new StartupConfiguration(StartupConfigurationStore.DefaultConfigurationName, [])];
                _activeConfigurationName = StartupConfigurationStore.DefaultConfigurationName;
            }

            SyncStartupConfigurationNames();

            // A solution that has never had a startup project set anywhere (no migrated legacy store
            // either) defaults to the first project, the same way Visual Studio defaults an
            // unconfigured multi-project solution's startup project.
            if (ActiveConfiguration is { Entries.Count: 0 } && projects.FirstOrDefault() is { FullPath: { } firstProjectPath })
            {
                ReplaceActiveConfiguration(ActiveConfiguration with
                {
                    Entries = [new StartupConfigurationEntry(firstProjectPath, StartupAction.Start, LaunchProfileName: null)],
                });
            }

            foreach (var project in projects)
            {
                project.IsStartupProject = project.FullPath is not null
                    && ActiveConfiguration?.Entries.Any(e => e.ProjectPath == project.FullPath && e.Action != StartupAction.None) == true;
            }

            RunCommand.NotifyCanExecuteChanged();
            StartDebuggingCommand.NotifyCanExecuteChanged();
            Output.AppendLine("Solution loaded.");

            // Best-effort: a solution opened in a fresh session has never been built by this
            // session, but may already have build output on disk from a prior one - so discovery
            // might still find real tests immediately. Not gated on success (see DiscoverTestsAsync).
            _ = DiscoverTestsAsync();

            // Default-on: only skipped if the user has explicitly turned it off in Settings.
            var restoreOnLoad = !bool.TryParse(_appSettingsStore.Get(RestoreOnSolutionLoadSettingKey), out var savedRestore) || savedRestore;

            // The Roslyn workspace load is deliberately sequenced to start only after the restore
            // attempt (successful or not) finishes, not in parallel with it - loading an unrestored
            // project into MSBuildWorkspace tends to produce a wall of false CS0246-style errors for
            // every missing package reference, which would make the Error List momentarily useless.
            // Still entirely fire-and-forget from here: neither path blocks solution load itself.
            if (restoreOnLoad)
            {
                _ = NuGetRestoreThenLoadRoslynWorkspaceAsync(solutionPath);
            }
            else
            {
                _ = LoadRoslynWorkspaceAsync(solutionPath);
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

    /// <summary>Awaits the NuGet restore attempt (regardless of its outcome) before starting the
    /// Roslyn workspace load - see the sequencing comment in LoadSolutionAsync for why.</summary>
    private async Task NuGetRestoreThenLoadRoslynWorkspaceAsync(string solutionPath)
    {
        await NuGetRestoreOnLoadAsync(solutionPath);
        await LoadRoslynWorkspaceAsync(solutionPath);
    }

    /// <summary>Fire-and-forget from LoadSolutionAsync (or chained after the restore attempt) - a
    /// Roslyn load failure (e.g. the version-skew MissingMethodException/FileLoadException risk
    /// described in the design doc, or simply a solution MSBuildWorkspace can't parse) must never
    /// break the rest of solution loading, just get logged to Output like the NuGet restore failure
    /// path above. WorkspaceFailed (per-project load problems) is intentionally not surfaced here -
    /// it fires far too often for perfectly normal reasons (an unrestored or non-SDK-style project)
    /// to log every occurrence to Output without drowning out everything else.</summary>
    private async Task LoadRoslynWorkspaceAsync(string solutionPath)
    {
        try
        {
            await _roslynWorkspaceService.LoadSolutionAsync(solutionPath);
            Output.AppendLine("Roslyn workspace loaded (IntelliSense/diagnostics ready).");
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Roslyn workspace load failed (IntelliSense/diagnostics may be unavailable): {ex.Message}");
        }
    }

    /// <summary>Persists <paramref name="updated"/> as the active configuration (replacing it by name
    /// in <see cref="_startupConfigurations"/>) and saves the whole set, mirroring how every mutator
    /// below is really just "replace the active configuration's entries, then save".</summary>
    private void ReplaceActiveConfiguration(StartupConfiguration updated)
    {
        var index = _startupConfigurations.FindIndex(c => c.Name == _activeConfigurationName);
        if (index >= 0)
        {
            _startupConfigurations[index] = updated;
        }
        else
        {
            _startupConfigurations.Add(updated);
            _activeConfigurationName = updated.Name;
        }

        SyncStartupConfigurationNames();

        if (Path.GetDirectoryName(CurrentSolutionPath) is { } solutionDirectory)
        {
            StartupConfigurationStore.Save(solutionDirectory, _startupConfigurations, _activeConfigurationName!);
        }
    }

    /// <summary>Mirrors Visual Studio's "Set as Startup Project": switches the active configuration to
    /// a single Start entry for <paramref name="node"/>, discarding whatever other entries it had -
    /// i.e. this always collapses back to single-project mode. Use the Startup Configuration Manager
    /// (or the "Include in Startup Projects" checkbox, <see cref="ToggleStartupProject"/>) to build a
    /// multi-project set instead.</summary>
    public void SetStartupProject(SolutionTreeNode node)
    {
        if (node.Kind != SolutionTreeNodeKind.Project || node.FullPath is null || _solutionRoot is null || ActiveConfiguration is not { } active)
        {
            return;
        }

        ReplaceActiveConfiguration(active with
        {
            Entries = [new StartupConfigurationEntry(node.FullPath, StartupAction.Start, LaunchProfileName: null)],
        });

        foreach (var project in _solutionRoot.Flatten().Where(n => n.Kind == SolutionTreeNodeKind.Project))
        {
            project.IsStartupProject = ReferenceEquals(project, node);
        }

        RunCommand.NotifyCanExecuteChanged();
        StartDebuggingCommand.NotifyCanExecuteChanged();

        Output.AppendLine($"Startup project set to {node.Name}.");
    }

    /// <summary>Whether <paramref name="node"/> has a non-None Action in the active configuration -
    /// backs the "Include in Startup Projects" checkbox in the Solution Explorer Project node's
    /// context menu.</summary>
    public bool IsStartupProject(SolutionTreeNode node) =>
        node.FullPath is not null && ActiveConfiguration?.Entries.Any(e => e.ProjectPath == node.FullPath && e.Action != StartupAction.None) == true;

    /// <summary>Toggles <paramref name="node"/> between Action.None and Action.Start in the active
    /// configuration (adding a new entry if it has none yet), persisting the choice. Independent of
    /// <see cref="SetStartupProject"/>, which instead collapses to a single-project configuration.</summary>
    public void ToggleStartupProject(SolutionTreeNode node)
    {
        if (node.Kind != SolutionTreeNodeKind.Project || node.FullPath is null || ActiveConfiguration is not { } active)
        {
            return;
        }

        var entries = active.Entries.ToList();
        var existingIndex = entries.FindIndex(e => e.ProjectPath == node.FullPath);
        if (existingIndex < 0)
        {
            entries.Add(new StartupConfigurationEntry(node.FullPath, StartupAction.Start, LaunchProfileName: null));
        }
        else
        {
            var isIncluded = entries[existingIndex].Action != StartupAction.None;
            entries[existingIndex] = entries[existingIndex] with { Action = isIncluded ? StartupAction.None : StartupAction.Start };
        }

        ReplaceActiveConfiguration(active with { Entries = entries });
        node.IsStartupProject = IsStartupProject(node);
        StartDebuggingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shows the Startup Configuration Manager dialog and, on OK, persists whatever
    /// configurations/active-selection the user ended up with.</summary>
    [RelayCommand]
    private void OpenStartupConfigurationManager()
    {
        if (_solutionRoot is null)
        {
            return;
        }

        var solutionProjects = _solutionRoot.Flatten()
            .Where(n => n.Kind == SolutionTreeNodeKind.Project && n.FullPath is not null)
            .Select(n => (Path: n.FullPath!, n.Name))
            .ToList();

        var dialogViewModel = new StartupConfigurationManagerViewModel(solutionProjects, _startupConfigurations, _activeConfigurationName ?? StartupConfigurationStore.DefaultConfigurationName);
        var dialog = new StartupConfigurationManagerDialog(dialogViewModel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _startupConfigurations = dialog.ResultConfigurations.ToList();
        _activeConfigurationName = dialog.ResultActiveConfigurationName;
        SyncStartupConfigurationNames();

        if (Path.GetDirectoryName(CurrentSolutionPath) is { } solutionDirectory)
        {
            StartupConfigurationStore.Save(solutionDirectory, _startupConfigurations, _activeConfigurationName);
        }

        foreach (var project in _solutionRoot.Flatten().Where(n => n.Kind == SolutionTreeNodeKind.Project))
        {
            project.IsStartupProject = IsStartupProject(project);
        }

        RunCommand.NotifyCanExecuteChanged();
        StartDebuggingCommand.NotifyCanExecuteChanged();
        Output.AppendLine($"Active startup configuration: {_activeConfigurationName}.");
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

    /// <summary>TestTreeNode.SourceFilePath is best-effort text scraped from a failure's stack trace -
    /// mirrors OpenDiagnostic's shape exactly, resolving a possibly-relative path against the solution
    /// directory. A node with no recognizable location (SourceFilePath is null) is a graceful no-op.</summary>
    public void OpenTestFailure(TestTreeNode node)
    {
        if (node.SourceFilePath is null)
        {
            return;
        }

        var baseDirectory = Path.GetDirectoryName(CurrentSolutionPath) ?? Directory.GetCurrentDirectory();
        var fullPath = Path.IsPathRooted(node.SourceFilePath)
            ? node.SourceFilePath
            : Path.GetFullPath(Path.Combine(baseDirectory, node.SourceFilePath));

        OpenPath(fullPath);
    }

    /// <summary>Fire-and-forget from BuildAsync/LoadSolutionAsync - a discovery failure must never
    /// break build/load result reporting, just get logged to Output.</summary>
    private async Task DiscoverTestsAsync()
    {
        try
        {
            var testProjects = (_solutionRoot?.Flatten() ?? [])
                .Where(n => n.Kind == SolutionTreeNodeKind.Project && n.IsTestProject)
                .ToList();
            await TestExplorer.DiscoverAsync(testProjects);
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Test discovery failed: {ex.Message}");
        }
    }

    private bool HasSolution() => CurrentSolutionPath is not null;

    private void OnBuildOutputLine(string line) => _dispatcher.Invoke(() =>
    {
        Output.AppendLine(line);
        ErrorList.AddIfDiagnostic(line);
    });

    /// <summary>
    /// Shared shape for Build/Clean/Rebuild, and the place a refusal lands.
    ///
    /// BuildService now declines to start a second dotnet build over the same obj/bin while one is
    /// running - previously nothing stopped a double-click (or the copilot's build tool racing the
    /// toolbar) from spawning two. These commands are fire-and-forget AsyncRelayCommands, so an
    /// unhandled refusal would become an unobserved task exception surfacing whenever the faulted
    /// Task happened to be collected. It belongs in the Output pane instead.
    ///
    /// The already-running check comes before Output.Clear() so a refused second build doesn't wipe
    /// the running one's output; the catch covers losing the race between the two.
    /// </summary>
    private async Task<int?> RunBuildVerbAsync(string label, Func<Task<int>> run)
    {
        if (_buildService.IsRunning)
        {
            Output.AppendLine($"{label} skipped - a build is already running.");
            return null;
        }

        Output.Clear();
        ErrorList.ClearBuildDiagnostics();
        Output.AppendLine($"{label} started ({Configuration}): {CurrentSolutionPath}");

        try
        {
            var exitCode = await run();
            Output.AppendLine($"{label} finished with exit code {exitCode}.");
            return exitCode;
        }
        catch (InvalidOperationException ex)
        {
            Output.AppendLine($"{label} skipped - {ex.Message}");
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSolution))]
    private async Task BuildAsync()
    {
        var exitCode = await RunBuildVerbAsync(
            "Build",
            () => _buildService.BuildAsync(CurrentSolutionPath!, OnBuildOutputLine, configuration: Configuration));

        if (exitCode == 0)
        {
            // Fire-and-forget: a successful build is the reliable trigger for (re)discovering
            // tests (a fresh clone has never been built, so solution-load alone would show none).
            _ = DiscoverTestsAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSolution))]
    private Task CleanAsync() =>
        RunBuildVerbAsync(
            "Clean",
            () => _buildService.CleanAsync(CurrentSolutionPath!, OnBuildOutputLine, configuration: Configuration));

    [RelayCommand(CanExecute = nameof(HasSolution))]
    private Task RebuildAsync() =>
        RunBuildVerbAsync(
            "Rebuild",
            () => _buildService.RebuildAsync(CurrentSolutionPath!, OnBuildOutputLine, configuration: Configuration));

    /// <summary>The quick-Run/Stop toolbar button targets the first non-None entry in the active
    /// configuration (Start or StartWithoutDebugging - Run never attaches a debugger regardless of
    /// which Action the entry has). A solution with a single Start entry (the common case) behaves
    /// exactly as before this feature existed.</summary>
    private StartupConfigurationEntry? RunnableEntry =>
        ActiveConfiguration?.Entries.FirstOrDefault(e => e.Action != StartupAction.None);

    private bool CanRun() => RunnableEntry is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (RunnableEntry is not { } entry)
        {
            return;
        }

        var targetFramework = _solutionRoot?.Flatten()
            .FirstOrDefault(n => n.Kind == SolutionTreeNodeKind.Project && n.FullPath == entry.ProjectPath)
            ?.TargetFramework;

        Output.Clear();
        Output.AppendLine($"Running ({Configuration}): {entry.ProjectPath}");
        IsRunning = true;
        _runService.Start(
            entry.ProjectPath,
            line => _dispatcher.Invoke(() => Output.AppendLine(line)),
            exitCode => _dispatcher.Invoke(() =>
            {
                Output.AppendLine($"Process exited with code {exitCode}.");
                IsRunning = false;
            }),
            targetFramework,
            Configuration,
            entry.LaunchProfileName);
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

    private bool CanStartDebugging() =>
        ActiveConfiguration?.Entries.Any(e => e.Action != StartupAction.None) == true
        && !IsDebugging
        && _multiProjectRunServices.Count == 0;

    private bool CanStopDebugging() => IsDebugging || _multiProjectRunServices.Count > 0;

    /// <summary>Launches every non-None entry in the active configuration. Action.Start entries each
    /// get their own <see cref="DebugSession"/> (built, then launched under netcoredbg).
    /// Action.StartWithoutDebugging entries run via a dedicated <see cref="RunService"/> instance (see
    /// <see cref="StartWithoutDebugging"/>) instead of the debug path. A solution with a single Start
    /// entry (the common case - every solution before this feature existed) produces output identical
    /// to the old single-project implementation. An entry that fails to build/start is logged and
    /// skipped, not treated as fatal to the rest - each is independent.</summary>
    [RelayCommand(CanExecute = nameof(CanStartDebugging))]
    private async Task StartDebuggingAsync()
    {
        Output.Clear();
        ErrorList.ClearBuildDiagnostics();

        var entries = ActiveConfiguration?.Entries.Where(e => e.Action != StartupAction.None).ToList() ?? [];
        var isSingleEntry = entries.Count == 1;
        _debugBrowserLaunched = false;

        foreach (var entry in entries)
        {
            var targetFramework = _solutionRoot?.Flatten()
                .FirstOrDefault(n => n.Kind == SolutionTreeNodeKind.Project && n.FullPath == entry.ProjectPath)
                ?.TargetFramework;

            if (entry.Action == StartupAction.StartWithoutDebugging)
            {
                StartWithoutDebugging(entry, targetFramework, isSingleEntry);
                continue;
            }

            Output.AppendLine(isSingleEntry ? "Building for debug..." : $"Building for debug: {entry.ProjectPath}...");

            var dllPath = await ResolveOutputDllAsync(entry.ProjectPath, targetFramework);
            if (dllPath is null)
            {
                Output.AppendLine(isSingleEntry
                    ? "Build failed; cannot start debugging."
                    : $"Build failed; cannot start debugging: {entry.ProjectPath}");
                continue;
            }

            _currentLaunchProfile = LaunchSettingsLoader.Load(entry.ProjectPath, entry.LaunchProfileName);

            Output.AppendLine($"Starting debugger: {dllPath}");
            try
            {
                await _debugSessionManager.StartAsync(
                    NetcoredbgPath,
                    dllPath,
                    Path.GetDirectoryName(entry.ProjectPath)!,
                    _currentLaunchProfile?.EnvironmentVariables,
                    _currentLaunchProfile?.CommandLineArgs);
            }
            catch (Exception ex)
            {
                Output.AppendLine(isSingleEntry
                    ? $"Failed to start debugging: {ex.Message}"
                    : $"Failed to start debugging {entry.ProjectPath}: {ex.Message}");
            }
        }
    }

    /// <summary>Runs one Action.StartWithoutDebugging entry from a multi-entry launch via its own
    /// <see cref="RunService"/> instance, tracked in <see cref="_multiProjectRunServices"/> and stopped
    /// from <see cref="StopDebuggingAsync"/> - not through the DI-singleton <see cref="_runService"/>
    /// used by the quick-Run toolbar button, since that one only ever tracks a single process and more
    /// than one of these can run concurrently (alongside each other and alongside debug sessions).</summary>
    private void StartWithoutDebugging(StartupConfigurationEntry entry, string? targetFramework, bool isSingleEntry)
    {
        var projectName = Path.GetFileNameWithoutExtension(entry.ProjectPath);
        Output.AppendLine(isSingleEntry ? "Running (no debugger)..." : $"Running (no debugger): {entry.ProjectPath}...");

        var runService = new RunService();
        _multiProjectRunServices.Add(runService);
        StartDebuggingCommand.NotifyCanExecuteChanged();
        StopDebuggingCommand.NotifyCanExecuteChanged();

        void Cleanup()
        {
            _multiProjectRunServices.Remove(runService);
            StartDebuggingCommand.NotifyCanExecuteChanged();
            StopDebuggingCommand.NotifyCanExecuteChanged();
        }

        try
        {
            runService.Start(
                entry.ProjectPath,
                line => _dispatcher.Invoke(() => Output.AppendLine(isSingleEntry ? line : $"[{projectName}] {line}")),
                exitCode => _dispatcher.Invoke(() =>
                {
                    Output.AppendLine(isSingleEntry
                        ? $"Process exited with code {exitCode}."
                        : $"[{projectName}] Process exited with code {exitCode}.");
                    Cleanup();
                }),
                targetFramework,
                Configuration,
                entry.LaunchProfileName);
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Failed to run {entry.ProjectPath}: {ex.Message}");
            Cleanup();
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
        int exitCode;
        try
        {
            exitCode = await _buildService.BuildAsync(projectPath, OnBuildOutputLine, targetFramework, Configuration);
        }
        catch (InvalidOperationException ex)
        {
            Output.AppendLine($"Cannot build {Path.GetFileName(projectPath)} - {ex.Message}");
            return null;
        }

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

    // Continue/StepOver/StepInto/StepOut act on the active session only - a click reaching here with
    // no active session in practice should already be prevented by the IsStoppedAtBreakpoint
    // CanExecute guard, but a session can end asynchronously between an enabled button and the click
    // handler running, so the null-guard is defensive anyway.

    /// <summary>Stops everything the active configuration's last Start Debugging launched: the active
    /// debug session (Continue/Step* only ever act on that one, but a multi-project launch's *other*
    /// debug sessions are left running - deliberately out of scope for this pass, same as before this
    /// feature existed) and every Action.StartWithoutDebugging process from that same launch.</summary>
    [RelayCommand(CanExecute = nameof(CanStopDebugging))]
    private async Task StopDebuggingAsync()
    {
        if (_debugSessionManager.ActiveSession is { } session)
        {
            await _debugSessionManager.StopAsync(session);
        }

        foreach (var runService in _multiProjectRunServices.ToList())
        {
            runService.Stop();
        }
    }

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task ContinueDebuggingAsync() => _debugSessionManager.ActiveSession?.ContinueAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepOverAsync() => _debugSessionManager.ActiveSession?.StepOverAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepIntoAsync() => _debugSessionManager.ActiveSession?.StepIntoAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsStoppedAtBreakpoint))]
    private Task StepOutAsync() => _debugSessionManager.ActiveSession?.StepOutAsync() ?? Task.CompletedTask;
}
