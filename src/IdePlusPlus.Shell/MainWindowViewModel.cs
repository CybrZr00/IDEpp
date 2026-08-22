using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Build;
using IdePlusPlus.Editor;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Run;
using IdePlusPlus.Shell.ToolPanes;
using Microsoft.Win32;

namespace IdePlusPlus.Shell;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly EditorWorkspace _editorWorkspace;
    private readonly BuildService _buildService;
    private readonly RunService _runService;
    private readonly Dispatcher _dispatcher;

    private string? _runnableProjectPath;

    public MainWindowViewModel(
        SolutionExplorerPaneViewModel solutionExplorer,
        OutputPaneViewModel output,
        CopilotChatPaneViewModel copilotChat,
        WelcomeDocumentViewModel welcome,
        EditorWorkspace editorWorkspace,
        BuildService buildService,
        RunService runService)
    {
        SolutionExplorer = solutionExplorer;
        Output = output;
        CopilotChat = copilotChat;
        Welcome = welcome;
        _editorWorkspace = editorWorkspace;
        _buildService = buildService;
        _runService = runService;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;
    }

    public SolutionExplorerPaneViewModel SolutionExplorer { get; }

    public OutputPaneViewModel Output { get; }

    public CopilotChatPaneViewModel CopilotChat { get; }

    public WelcomeDocumentViewModel Welcome { get; }

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
            FileName = document.FilePath is null ? "Untitled" : System.IO.Path.GetFileName(document.FilePath),
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
}
