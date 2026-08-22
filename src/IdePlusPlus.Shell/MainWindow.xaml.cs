using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.Shell.Views;

namespace IdePlusPlus.Shell;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand GoToLineCommand = new();

    private static readonly string LayoutFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus",
        "layout.xml");

    private static readonly string WindowBoundsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus",
        "window.json");

    private readonly Dictionary<string, object> _contentById;
    private readonly SecureSettingsStore _secureSettingsStore;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly McpServerManager _mcpServerManager;
    private readonly McpServerConfigStore _mcpServerConfigStore;
    private readonly DeepSeekFimClient _deepSeekFimClient;

    public MainWindow(
        MainWindowViewModel viewModel,
        ThemeService themeService,
        SecureSettingsStore secureSettingsStore,
        AppSettingsStore appSettingsStore,
        McpServerManager mcpServerManager,
        McpServerConfigStore mcpServerConfigStore,
        DeepSeekFimClient deepSeekFimClient)
    {
        _secureSettingsStore = secureSettingsStore;
        _appSettingsStore = appSettingsStore;
        _mcpServerManager = mcpServerManager;
        _mcpServerConfigStore = mcpServerConfigStore;
        _deepSeekFimClient = deepSeekFimClient;

        DataContext = viewModel;
        InitializeComponent();

        // LayoutAnchorable.Content is a plain CLR property, not a DependencyProperty, so it
        // can't be set via {Binding} in XAML — assign the pane view models here instead. The
        // window's DataTemplates (keyed by view model type) render each one.
        SolutionExplorerAnchorable.Content = viewModel.SolutionExplorer;
        SourceControlAnchorable.Content = viewModel.SourceControl;
        OutputAnchorable.Content = viewModel.Output;
        ErrorListAnchorable.Content = viewModel.ErrorList;
        CallStackAnchorable.Content = viewModel.CallStack;
        LocalsAnchorable.Content = viewModel.Locals;
        CopilotChatAnchorable.Content = viewModel.CopilotChat;
        WelcomeDocument.Content = viewModel.Welcome;

        _contentById = new Dictionary<string, object>
        {
            [SolutionExplorerPaneViewModel.PaneContentId] = viewModel.SolutionExplorer,
            [SourceControlPaneViewModel.PaneContentId] = viewModel.SourceControl,
            [OutputPaneViewModel.PaneContentId] = viewModel.Output,
            [ErrorListPaneViewModel.PaneContentId] = viewModel.ErrorList,
            [CallStackPaneViewModel.PaneContentId] = viewModel.CallStack,
            [LocalsPaneViewModel.PaneContentId] = viewModel.Locals,
            [CopilotChatPaneViewModel.PaneContentId] = viewModel.CopilotChat,
            [WelcomeDocumentViewModel.ContentId] = viewModel.Welcome,
        };

        ApplyDockingTheme(themeService.IsDark);
        themeService.Changed += () => ApplyDockingTheme(themeService.IsDark);

        Loaded += (_, _) => RestoreLayout();
        Loaded += (_, _) => RestoreWindowBounds();
        Closing += (_, _) => SaveLayout();
        Closing += (_, _) => SaveWindowBounds();
        Closing += (_, _) => viewModel.StopDebuggingCommand.Execute(null); // best effort; avoids orphaning netcoredbg
    }

    private sealed record WindowBounds(double Left, double Top, double Width, double Height, bool IsMaximized);

    private void RestoreWindowBounds()
    {
        if (!File.Exists(WindowBoundsFilePath))
        {
            return;
        }

        try
        {
            var bounds = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(WindowBoundsFilePath));
            if (bounds is not { Width: > 0, Height: > 0 })
            {
                return;
            }

            Width = bounds.Width;
            Height = bounds.Height;

            // Clamp rather than trust the saved position outright - a monitor that was unplugged
            // since the last session shouldn't be able to place the window fully off-screen.
            Left = Math.Clamp(bounds.Left, 0, Math.Max(0, SystemParameters.VirtualScreenWidth - 100));
            Top = Math.Clamp(bounds.Top, 0, Math.Max(0, SystemParameters.VirtualScreenHeight - 100));

            if (bounds.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch (Exception)
        {
            // Corrupt/incompatible file from a previous version - keep the default XAML size/position.
        }
    }

    private void SaveWindowBounds()
    {
        var directory = Path.GetDirectoryName(WindowBoundsFilePath)!;
        Directory.CreateDirectory(directory);

        var bounds = new WindowBounds(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height, WindowState == WindowState.Maximized);
        File.WriteAllText(WindowBoundsFilePath, JsonSerializer.Serialize(bounds));
    }

    private void ApplyDockingTheme(bool dark) =>
        DockManager.Theme = dark ? new Vs2013DarkTheme() : new Vs2013LightTheme();

    private void RestoreLayout()
    {
        if (!File.Exists(LayoutFilePath))
        {
            return;
        }

        var serializer = new XmlLayoutSerializer(DockManager);
        serializer.LayoutSerializationCallback += (_, args) =>
        {
            if (args.Model.ContentId is { } contentId && _contentById.TryGetValue(contentId, out var content))
            {
                args.Content = content;
            }
            else
            {
                // Unknown content (e.g. a document pane from a previous session, or a stale
                // ContentId from an older build) — drop it rather than restoring a broken pane.
                args.Cancel = true;
            }
        };

        try
        {
            serializer.Deserialize(LayoutFilePath);
        }
        catch (Exception)
        {
            // Corrupt/incompatible layout file from a previous version — keep the default XAML layout.
        }
    }

    private void SaveLayout()
    {
        var directory = Path.GetDirectoryName(LayoutFilePath)!;
        Directory.CreateDirectory(directory);

        var serializer = new XmlLayoutSerializer(DockManager);
        serializer.Serialize(LayoutFilePath);
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var viewModel = (MainWindowViewModel)DataContext;
        foreach (var path in paths.Where(File.Exists))
        {
            var extension = Path.GetExtension(path);
            if (extension is ".sln" or ".slnx")
            {
                _ = viewModel.LoadSolutionAsync(path);
            }
            else
            {
                viewModel.OpenPath(path);
            }
        }
    }

    private void DockManager_OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
    {
        if (e.Document.Content is not DocumentViewModel document || !document.IsDirty)
        {
            return;
        }

        if (!ConfirmCloseDirtyDocument(document))
        {
            e.Cancel = true;
        }
    }

    /// <summary>Returns whether it's OK to proceed with closing a dirty document - false for an
    /// explicit Cancel, or for a Yes whose Save-As (an untitled document) the user canceled, which
    /// would otherwise silently discard the changes.</summary>
    private bool ConfirmCloseDirtyDocument(DocumentViewModel document)
    {
        var result = MessageBox.Show(
            this,
            $"Save changes to {document.Title.TrimEnd('*')}?",
            "IDE++",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        switch (result)
        {
            case MessageBoxResult.Cancel:
                return false;
            case MessageBoxResult.Yes:
                ((MainWindowViewModel)DataContext).SaveDocument(document);
                return !document.IsDirty;
            default:
                return true;
        }
    }

    private void CloseAllMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var viewModel = (MainWindowViewModel)DataContext;
        foreach (var document in viewModel.OpenDocuments.ToList())
        {
            if (document.IsDirty && !ConfirmCloseDirtyDocument(document))
            {
                break; // Cancel, or a Save-As the user backed out of - stop closing further documents
            }

            viewModel.CloseDocument(document);
        }
    }

    private void DockManager_OnDocumentClosed(object? sender, DocumentClosedEventArgs e)
    {
        if (e.Document.Content is DocumentViewModel document)
        {
            ((MainWindowViewModel)DataContext).CloseDocument(document);
        }
    }

    private void SolutionExplorerTree_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: SolutionTreeNode node })
        {
            ((MainWindowViewModel)DataContext).OpenTreeNode(node);
        }
    }

    // Built and shown in code-behind rather than as a XAML ContextMenu: a ContextMenu's popup isn't
    // part of the visual tree its PlacementTarget lives in, so a RelativeSource binding from inside
    // it can't reach the window's MainWindowViewModel to invoke SetStartupProject.
    private void SolutionExplorerTree_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: SolutionTreeNode { Kind: SolutionTreeNodeKind.Project } node } element)
        {
            return;
        }

        var viewModel = (MainWindowViewModel)DataContext;
        var menuItem = new MenuItem { Header = "Set as Startup Project" };
        menuItem.Click += (_, _) => viewModel.SetStartupProject(node);

        var contextMenu = new ContextMenu { PlacementTarget = element };
        contextMenu.Items.Add(menuItem);
        contextMenu.IsOpen = true;
    }

    private void OutputTextBox_OnTextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();

    private void ErrorListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: BuildDiagnostic diagnostic })
        {
            ((MainWindowViewModel)DataContext).OpenDiagnostic(diagnostic);
        }
    }

    private void SettingsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var settingsViewModel = new SettingsViewModel(_secureSettingsStore, _appSettingsStore, _mcpServerManager, _mcpServerConfigStore, _deepSeekFimClient);
        var window = new SettingsWindow(settingsViewModel) { Owner = this };
        window.ShowDialog();
    }

    private void AboutMenuItem_OnClick(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void GoToLineCommand_OnCanExecute(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = ((MainWindowViewModel)DataContext).ActiveDocument is not null;

    private void GoToLineCommand_OnExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new GoToLineWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.LineNumber is { } line)
        {
            ((MainWindowViewModel)DataContext).GoToLine(line);
        }
    }
}
