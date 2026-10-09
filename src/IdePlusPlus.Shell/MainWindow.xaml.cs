using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.PluginContracts;
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
    private readonly PluginHost _pluginHost;

    // Plugins register tool windows during LoadPlugins(), which App.xaml.cs calls after this
    // constructor returns but before Show(), i.e. always before the Loaded-time layout restore -
    // DockLayoutService holds them until that has run.
    private readonly DockLayoutService _layout;

    public MainWindow(
        MainWindowViewModel viewModel,
        ThemeService themeService,
        SecureSettingsStore secureSettingsStore,
        AppSettingsStore appSettingsStore,
        McpServerManager mcpServerManager,
        McpServerConfigStore mcpServerConfigStore,
        DeepSeekFimClient deepSeekFimClient,
        PluginHost pluginHost,
        PluginToolWindowRegistry pluginToolWindowRegistry,
        PluginKeyBindingRegistry pluginKeyBindingRegistry)
    {
        _secureSettingsStore = secureSettingsStore;
        _appSettingsStore = appSettingsStore;
        _mcpServerManager = mcpServerManager;
        _mcpServerConfigStore = mcpServerConfigStore;
        _deepSeekFimClient = deepSeekFimClient;
        _pluginHost = pluginHost;

        DataContext = viewModel;
        InitializeComponent();

        // LayoutAnchorable.Content is a plain CLR property, not a DependencyProperty, so it
        // can't be set via {Binding} in XAML — assign the pane view models here instead. The
        // window's DataTemplates (keyed by view model type) render each one.
        SolutionExplorerAnchorable.Content = viewModel.SolutionExplorer;
        SourceControlAnchorable.Content = viewModel.SourceControl;
        NuGetAnchorable.Content = viewModel.NuGet;
        ConnectionsExplorerAnchorable.Content = viewModel.ConnectionsExplorer;
        OutputAnchorable.Content = viewModel.Output;
        ErrorListAnchorable.Content = viewModel.ErrorList;
        TestExplorerAnchorable.Content = viewModel.TestExplorer;
        TaskRunnerAnchorable.Content = viewModel.TaskExplorer;
        CallStackAnchorable.Content = viewModel.CallStack;
        LocalsAnchorable.Content = viewModel.Locals;
        CopilotChatAnchorable.Content = viewModel.CopilotChat;
        ScheduledAutomationsAnchorable.Content = viewModel.ScheduledAutomations;
        WelcomeDocument.Content = viewModel.Welcome;

        _contentById = new Dictionary<string, object>
        {
            [SolutionExplorerPaneViewModel.PaneContentId] = viewModel.SolutionExplorer,
            [SourceControlPaneViewModel.PaneContentId] = viewModel.SourceControl,
            [NuGetPaneViewModel.PaneContentId] = viewModel.NuGet,
            [ConnectionsExplorerPaneViewModel.PaneContentId] = viewModel.ConnectionsExplorer,
            [OutputPaneViewModel.PaneContentId] = viewModel.Output,
            [ErrorListPaneViewModel.PaneContentId] = viewModel.ErrorList,
            [TestExplorerPaneViewModel.PaneContentId] = viewModel.TestExplorer,
            [TaskExplorerPaneViewModel.PaneContentId] = viewModel.TaskExplorer,
            [CallStackPaneViewModel.PaneContentId] = viewModel.CallStack,
            [LocalsPaneViewModel.PaneContentId] = viewModel.Locals,
            [CopilotChatPaneViewModel.PaneContentId] = viewModel.CopilotChat,
            [ScheduledAutomationsPaneViewModel.PaneContentId] = viewModel.ScheduledAutomations,
            [WelcomeDocumentViewModel.ContentId] = viewModel.Welcome,
        };

        // Must happen now, while DockManager.Layout is still the tree InitializeComponent built -
        // that tree is what the service snapshots as the default layout.
        _layout = new DockLayoutService(DockManager, _contentById, LayoutFilePath);

        ApplyDockingTheme(themeService.IsDark);
        themeService.Changed += () => ApplyDockingTheme(themeService.IsDark);

        // The Git pane is easy to miss tabbed behind Solution Explorer - bring it to the front
        // automatically when a newly opened solution's repo actually has something to show. Looked
        // up fresh by ContentId, not via the SourceControlAnchorable field: restoring or resetting the
        // layout replaces DockManager.Layout with a different tree, which would leave that XAML
        // x:Name field pointing at a stale, detached object. A Git pane the user hid stays hidden.
        viewModel.SourceControl.PendingChangesDetected += () => _layout.Activate(SourceControlPaneViewModel.PaneContentId);

        // Hands over every tool window a plugin has registered by the time this runs, plus (via the
        // event) any registered afterward - see OnPluginToolWindowRegistered's doc comment.
        foreach (var toolWindow in pluginToolWindowRegistry.ToolWindows)
        {
            OnPluginToolWindowRegistered(toolWindow);
        }

        pluginToolWindowRegistry.Registered += OnPluginToolWindowRegistered;

        // Window.InputBindings is a plain runtime collection - unlike tool windows, there's no
        // layout-serialization ordering concern, so these can be added as soon as they're known.
        foreach (var binding in pluginKeyBindingRegistry.Bindings)
        {
            AddPluginKeyBinding(binding);
        }

        pluginKeyBindingRegistry.Registered += AddPluginKeyBinding;

        Loaded += (_, _) => _layout.Restore();
        Loaded += (_, _) => RestoreWindowBounds();
        Closing += (_, _) => _layout.Save();
        Closing += (_, _) => SaveWindowBounds();
        // Closing mid-stream abandons RunAssistantTurnsAsync before its finally runs, so capture
        // whatever the conversation has reached rather than losing it.
        Closing += (_, _) => viewModel.CopilotChat.PersistChatHistory();
        viewModel.ConfirmCloseDirtyDocument = ConfirmCloseDirtyDocument;
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

    /// <summary>
    /// Records a plugin tool window's content (calling its factory exactly once, per
    /// IPluginHost.RegisterToolWindow's contract) and hands it to the layout service, which makes it
    /// resolvable by ContentId - so a saved layout that already had this exact pane (same plugin, same
    /// title, from a previous session) restores it in whatever position/floating/hidden state the user
    /// last left it - and attaches it once the saved layout has been restored.
    /// </summary>
    private void OnPluginToolWindowRegistered(PluginToolWindow toolWindow) =>
        _layout.RegisterPluginToolWindow(
            $"Plugin_{toolWindow.PluginName}_{toolWindow.Title}",
            toolWindow.Title,
            toolWindow.Factory());

    private void AddPluginKeyBinding(PluginKeyBinding binding)
    {
        // PluginKeyBindingRegistry.TryRegister already validated gestureText parses and doesn't
        // collide with a reserved gesture before firing Registered/adding to Bindings - this can't
        // fail in practice, but a KeyGestureConverter round-trip is cheap insurance against a future
        // change to that validation.
        if (new KeyGestureConverter().ConvertFromString(binding.GestureText) is not KeyGesture gesture)
        {
            return;
        }

        // binding.Execute is already wrapped by PluginHost.RegisterKeyBinding in the same
        // try/catch-and-log-to-Output pattern as RunPluginCommand - a throwing plugin handler can't
        // reach here unhandled.
        InputBindings.Add(new KeyBinding(new RelayCommand(binding.Execute), gesture));
    }

    // Rebuilt on every open rather than bound: the set changes as plugins register, and the check
    // state comes from the live dock layout, which has no change notification to bind to.
    private void ToolWindowsMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != ToolWindowsMenuItem)
        {
            return; // A nested submenu opening bubbles up here too.
        }

        ToolWindowsMenuItem.Items.Clear();
        foreach (var toolWindow in _layout.ToolWindows)
        {
            var menuItem = new MenuItem
            {
                // Doubled so a plugin title containing '_' isn't read as an access-key marker.
                Header = toolWindow.Title.Replace("_", "__"),
                IsCheckable = true,
                IsChecked = toolWindow.IsVisible,
            };
            var contentId = toolWindow.ContentId;
            menuItem.Click += (_, _) => _layout.Toggle(contentId);
            ToolWindowsMenuItem.Items.Add(menuItem);
        }
    }

    private void ResetLayoutMenuItem_OnClick(object sender, RoutedEventArgs e) => _layout.Reset();

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>A dropped solution is opened first, and anything dropped with it only once it has:
    /// opening a different solution closes every open document, which would otherwise include the
    /// files that came in the same drop. Only the last solution in a drop is opened - each would
    /// replace the one before.</summary>
    private async void MainWindow_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var viewModel = (MainWindowViewModel)DataContext;
        var existing = paths.Where(File.Exists).ToList();
        static bool IsSolution(string path) => Path.GetExtension(path) is ".sln" or ".slnx";

        if (existing.LastOrDefault(IsSolution) is { } solutionPath)
        {
            await viewModel.LoadSolutionAsync(solutionPath);
        }

        foreach (var path in existing.Where(path => !IsSolution(path)))
        {
            viewModel.OpenPath(path);
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
    // it can't reach the window's MainWindowViewModel to invoke SetStartupProject. Handles any node
    // kind now (previously only Project) so a plugin's context-menu command can appear on
    // File/Folder/Solution nodes too - the three core items above stay Project-only exactly as before.
    private void SolutionExplorerTree_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: SolutionTreeNode node } element)
        {
            return;
        }

        var viewModel = (MainWindowViewModel)DataContext;
        var contextMenu = new ContextMenu { PlacementTarget = element };

        if (node.Kind == SolutionTreeNodeKind.Solution)
        {
            var newProjectMenuItem = new MenuItem { Header = "Add New Project..." };
            newProjectMenuItem.Click += (_, _) => viewModel.NewProjectCommand.Execute(null);
            contextMenu.Items.Add(newProjectMenuItem);
        }

        // Project, folder and file nodes all stand for a directory an item can go in - see
        // MainWindowViewModel.DirectoryForNode. The node is selected first so the command's own
        // target resolution picks it up, which is also what a right-click implies visually.
        if (node.Kind is SolutionTreeNodeKind.Project or SolutionTreeNodeKind.Folder or SolutionTreeNodeKind.File)
        {
            var newItemMenuItem = new MenuItem { Header = "Add New Item..." };
            newItemMenuItem.Click += (_, _) =>
            {
                node.IsSelected = true;
                viewModel.NewItemCommand.Execute(null);
            };
            contextMenu.Items.Add(newItemMenuItem);
        }

        if (node.Kind == SolutionTreeNodeKind.Project)
        {
            var menuItem = new MenuItem { Header = "Set as Startup Project" };
            menuItem.Click += (_, _) => viewModel.SetStartupProject(node);
            contextMenu.Items.Add(menuItem);

            var startupProjectsMenuItem = new MenuItem
            {
                Header = "Include in Startup Projects",
                IsCheckable = true,
                IsChecked = viewModel.IsStartupProject(node),
            };
            startupProjectsMenuItem.Click += (_, _) => viewModel.ToggleStartupProject(node);
            contextMenu.Items.Add(startupProjectsMenuItem);

            var nuGetMenuItem = new MenuItem { Header = "Manage NuGet Packages..." };
            nuGetMenuItem.Click += (_, _) => viewModel.OpenNuGetPane(node);
            contextMenu.Items.Add(nuGetMenuItem);

            var publishMenuItem = new MenuItem { Header = "Publish..." };
            publishMenuItem.Click += (_, _) => viewModel.OpenPublishDialog(node);
            contextMenu.Items.Add(publishMenuItem);
        }

        // Folder nodes carry a real path now (see SolutionTreeNode.GetOrCreateFolder), so plugin
        // commands are offered on them too rather than being silently skipped for want of one.
        if (node.FullPath is { } fullPath)
        {
            var context = new SolutionExplorerContextMenuContext(fullPath, node.Kind == SolutionTreeNodeKind.Folder);
            var pluginItems = _pluginHost.SolutionExplorerContextMenuCommands.Where(c => c.CanExecute(context)).ToList();

            if (pluginItems.Count > 0)
            {
                if (contextMenu.Items.Count > 0)
                {
                    contextMenu.Items.Add(new Separator());
                }

                foreach (var command in pluginItems)
                {
                    var pluginMenuItem = new MenuItem { Header = command.Header };
                    pluginMenuItem.Click += (_, _) => command.Execute(context);
                    contextMenu.Items.Add(pluginMenuItem);
                }
            }
        }

        if (contextMenu.Items.Count == 0)
        {
            return;
        }

        contextMenu.IsOpen = true;
    }

    /// <summary>
    /// Rebuilds the item-template shortlist between the two placeholder separators each time the
    /// menu opens, so a template package installed mid-session shows up without a restart.
    /// </summary>
    private void NewMenuItem_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        var viewModel = (MainWindowViewModel)DataContext;
        var start = NewMenuItem.Items.IndexOf(NewMenuShortlistStart);
        var end = NewMenuItem.Items.IndexOf(NewMenuShortlistEnd);

        for (var i = end - 1; i > start; i--)
        {
            NewMenuItem.Items.RemoveAt(i);
        }

        var insertAt = start + 1;
        foreach (var shortcut in viewModel.NewItemShortcuts)
        {
            var menuItem = new MenuItem
            {
                Header = shortcut.Header,
                Command = viewModel.NewItemCommand,
                CommandParameter = shortcut.ShortName,
            };
            NewMenuItem.Items.Insert(insertAt++, menuItem);
        }

        // With no solution open there is nothing between them, and two separators in a row reads as
        // a rendering bug rather than an empty section.
        NewMenuShortlistStart.Visibility = viewModel.NewItemShortcuts.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        NewMenuShortlistEnd.Visibility = NewMenuShortlistStart.Visibility;
    }

    private void OutputTextBox_OnTextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();

    private void ErrorListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: BuildDiagnostic diagnostic })
        {
            ((MainWindowViewModel)DataContext).OpenDiagnostic(diagnostic);
        }
    }

    private void TestTreeView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: TestTreeNode { Kind: TestTreeNodeKind.Method } node } && node.SourceFilePath is not null)
        {
            ((MainWindowViewModel)DataContext).OpenTestFailure(node);
        }
    }

    // TreeView.SelectedItem has no setter (unlike ListBox/ComboBox's SelectedItem elsewhere in this
    // file), so it can't be bound TwoWay from XAML - mirror it into the view model from the event instead.
    private void TestTreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        ((MainWindowViewModel)DataContext).TestExplorer.SelectedNode = e.NewValue as TestTreeNode;

    /// <summary>Double-click runs a task, and opens the package.json behind a manifest node - the
    /// same "act on what was double-clicked" split the Test Explorer tree uses.</summary>
    private void TaskTreeView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: TaskTreeNode node })
        {
            return;
        }

        var viewModel = (MainWindowViewModel)DataContext;
        if (node.Kind == TaskTreeNodeKind.Task)
        {
            _ = viewModel.TaskExplorer.RunAsync(node);
        }
        else
        {
            viewModel.OpenTaskManifest(node);
        }
    }

    private void TaskTreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        ((MainWindowViewModel)DataContext).TaskExplorer.SelectedNode = e.NewValue as TaskTreeNode;

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
