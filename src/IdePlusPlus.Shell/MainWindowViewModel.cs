using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Build;
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
    private readonly Dispatcher _dispatcher;

    private string? _runnableProjectPath;

    public MainWindowViewModel(
        SolutionExplorerPaneViewModel solutionExplorer,
        OutputPaneViewModel output,
        CopilotChatPaneViewModel copilotChat,
        WelcomeDocumentViewModel welcome,
        CallStackPaneViewModel callStack,
        LocalsPaneViewModel locals,
        EditorWorkspace editorWorkspace,
        BuildService buildService,
        RunService runService,
        DebugSession debugSession)
    {
        SolutionExplorer = solutionExplorer;
        Output = output;
        CopilotChat = copilotChat;
        Welcome = welcome;
        CallStack = callStack;
        Locals = locals;
        _editorWorkspace = editorWorkspace;
        _buildService = buildService;
        _runService = runService;
        _debugSession = debugSession;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        _debugSession.StateChanged += () => _dispatcher.Invoke(OnDebugStateChanged);
        _debugSession.OutputReceived += line => _dispatcher.Invoke(() => Output.AppendLine(line.TrimEnd('\r', '\n')));
    }

    public SolutionExplorerPaneViewModel SolutionExplorer { get; }

    public OutputPaneViewModel Output { get; }

    public CopilotChatPaneViewModel CopilotChat { get; }

    public WelcomeDocumentViewModel Welcome { get; }

    public CallStackPaneViewModel CallStack { get; }

    public LocalsPaneViewModel Locals { get; }

    public ObservableCollection<DocumentViewModel> OpenDocuments => _editorWorkspace.OpenDocuments;

    public ObservableCollection<string> RecentFiles => _editorWorkspace.RecentFiles;

    [ObservableProperty]
    private DocumentViewModel? activeDocument;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private string? currentSolutionPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool isRunning;

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

    public void OpenPath(string filePath) => ActiveDocument = _editorWorkspace.Open(filePath);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void Save() => SaveDocument(ActiveDocument!);

    private bool HasActiveDocument() => ActiveDocument is not null;

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void SaveAs() => SaveDocumentAs(ActiveDocument!);

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
    }

    [RelayCommand]
    private async Task OpenSolutionAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Solution files (*.slnx;*.sln)|*.slnx;*.sln" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Output.Clear();
        Output.AppendLine($"Loading {dialog.FileName}...");
        try
        {
            var root = await Task.Run(() => SolutionLoader.Load(dialog.FileName));
            SolutionExplorer.SetSolution(root);
            CurrentSolutionPath = dialog.FileName;
            _runnableProjectPath = root.Children.FirstOrDefault(c => c.Kind == SolutionTreeNodeKind.Project)?.FullPath;
            RunCommand.NotifyCanExecuteChanged();
            Output.AppendLine("Solution loaded.");
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Failed to load solution: {ex.Message}");
        }
    }

    public void OpenTreeNode(SolutionTreeNode node)
    {
        if (node.Kind == SolutionTreeNodeKind.File && node.FullPath is not null)
        {
            OpenPath(node.FullPath);
        }
    }

    private bool HasSolution() => CurrentSolutionPath is not null;

    [RelayCommand(CanExecute = nameof(HasSolution))]
    private async Task BuildAsync()
    {
        Output.Clear();
        Output.AppendLine($"Build started: {CurrentSolutionPath}");
        var exitCode = await _buildService.BuildAsync(
            CurrentSolutionPath!,
            line => _dispatcher.Invoke(() => Output.AppendLine(line)));
        Output.AppendLine($"Build finished with exit code {exitCode}.");
    }

    private bool CanRun() => _runnableProjectPath is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        Output.Clear();
        Output.AppendLine($"Running: {_runnableProjectPath}");
        IsRunning = true;
        _runService.Start(
            _runnableProjectPath!,
            line => _dispatcher.Invoke(() => Output.AppendLine(line)),
            exitCode => _dispatcher.Invoke(() =>
            {
                Output.AppendLine($"Process exited with code {exitCode}.");
                IsRunning = false;
            }));
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _runService.Stop();

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
        Output.AppendLine("Building for debug...");
        var dllPath = await ResolveOutputDllAsync(_runnableProjectPath!);
        if (dllPath is null)
        {
            Output.AppendLine("Build failed; cannot start debugging.");
            return;
        }

        Output.AppendLine($"Starting debugger: {dllPath}");
        try
        {
            await _debugSession.StartAsync(NetcoredbgPath, dllPath, Path.GetDirectoryName(_runnableProjectPath)!);
        }
        catch (Exception ex)
        {
            Output.AppendLine($"Failed to start debugging: {ex.Message}");
        }
    }

    private async Task<string?> ResolveOutputDllAsync(string projectPath)
    {
        var exitCode = await _buildService.BuildAsync(
            projectPath,
            line => _dispatcher.Invoke(() => Output.AppendLine(line)));
        if (exitCode != 0)
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var binDirectory = Path.Combine(projectDirectory, "bin", "Debug");
        if (!Directory.Exists(binDirectory))
        {
            return null;
        }

        return Directory
            .GetFiles(binDirectory, $"{projectName}.dll", SearchOption.AllDirectories)
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
