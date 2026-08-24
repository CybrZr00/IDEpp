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

    /// <summary>Every core (non-plugin, non-document) anchorable pane, with the ContentId of the pane
    /// it should be re-homed alongside if a saved layout.xml doesn't mention it at all - see
    /// EnsureCorePanesPresent's doc comment for why that can happen. Mirrors the actual XAML grouping:
    /// Solution Explorer/Git/NuGet on the left, Output/Error List/Tests/Call Stack/Locals at the
    /// bottom-center, Copilot Chat on the right.</summary>
    private static readonly (string ContentId, string Title, string PreferredNeighborContentId)[] CoreAnchorablePanes =
    [
        (SolutionExplorerPaneViewModel.PaneContentId, "Solution Explorer", SolutionExplorerPaneViewModel.PaneContentId),
        (SourceControlPaneViewModel.PaneContentId, "Git", SolutionExplorerPaneViewModel.PaneContentId),
        (NuGetPaneViewModel.PaneContentId, "NuGet", SolutionExplorerPaneViewModel.PaneContentId),
        (OutputPaneViewModel.PaneContentId, "Output", OutputPaneViewModel.PaneContentId),
        (ErrorListPaneViewModel.PaneContentId, "Error List", OutputPaneViewModel.PaneContentId),
        (TestExplorerPaneViewModel.PaneContentId, "Tests", OutputPaneViewModel.PaneContentId),
        (CallStackPaneViewModel.PaneContentId, "Call Stack", OutputPaneViewModel.PaneContentId),
        (LocalsPaneViewModel.PaneContentId, "Locals", OutputPaneViewModel.PaneContentId),
        (CopilotChatPaneViewModel.PaneContentId, "Copilot Chat", CopilotChatPaneViewModel.PaneContentId),
    ];

    private readonly Dictionary<string, object> _contentById;
    private readonly SecureSettingsStore _secureSettingsStore;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly McpServerManager _mcpServerManager;
    private readonly McpServerConfigStore _mcpServerConfigStore;
    private readonly DeepSeekFimClient _deepSeekFimClient;
    private readonly PluginHost _pluginHost;

    // Registering a LayoutAnchorable for a plugin's tool window has to wait until after
    // RestoreLayout() has run (see AttachPendingPluginToolWindows' doc comment) - plugins register
    // during LoadPlugins(), which App.xaml.cs calls after this constructor returns but before
    // Show(), i.e. always before RestoreLayout's Loaded handler fires. Buffered here in the
    // meantime rather than touching DockManager.Layout immediately.
    private readonly List<(string ContentId, string Title, object Content)> _pendingPluginToolWindows = [];
    private bool _layoutRestored;

    // Snapshot of every pane MainWindow.xaml declares (ContentId/Title/the ContentIds of its original
    // pane-mates), captured right after InitializeComponent - before RestoreLayout's Deserialize can
    // replace DockManager.Layout with an older saved tree that predates one of these panes. Used by
    // AttachMissingCoreToolWindows to add back whichever of them Deserialize dropped, the same way
    // AttachPendingPluginToolWindows already does for plugin panes.
    private readonly List<(string ContentId, string Title, string[] SiblingContentIds)> _coreToolWindows;

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
        OutputAnchorable.Content = viewModel.Output;
        ErrorListAnchorable.Content = viewModel.ErrorList;
        TestExplorerAnchorable.Content = viewModel.TestExplorer;
        CallStackAnchorable.Content = viewModel.CallStack;
        LocalsAnchorable.Content = viewModel.Locals;
        CopilotChatAnchorable.Content = viewModel.CopilotChat;
        WelcomeDocument.Content = viewModel.Welcome;

        _contentById = new Dictionary<string, object>
        {
            [SolutionExplorerPaneViewModel.PaneContentId] = viewModel.SolutionExplorer,
            [SourceControlPaneViewModel.PaneContentId] = viewModel.SourceControl,
            [NuGetPaneViewModel.PaneContentId] = viewModel.NuGet,
            [OutputPaneViewModel.PaneContentId] = viewModel.Output,
            [ErrorListPaneViewModel.PaneContentId] = viewModel.ErrorList,
            [TestExplorerPaneViewModel.PaneContentId] = viewModel.TestExplorer,
            [CallStackPaneViewModel.PaneContentId] = viewModel.CallStack,
            [LocalsPaneViewModel.PaneContentId] = viewModel.Locals,
            [CopilotChatPaneViewModel.PaneContentId] = viewModel.CopilotChat,
            [WelcomeDocumentViewModel.ContentId] = viewModel.Welcome,
        };

        // Every LayoutAnchorable that exists right after InitializeComponent is, by construction,
        // one MainWindow.xaml declared directly (nothing else - plugins included - has registered
        // anything yet at this point in the constructor), so this is the complete set of "core"
        // panes without needing to filter on CanClose. CanClose can't be used for that filter anyway:
        // DockingManager.LayoutItemContainerStyle (below) sets CanClose="True" on every LayoutItem
        // wrapper and writes it back into the underlying model, so by the time anything reads
        // a.CanClose here it's already true for every anchorable regardless of what XAML declared.
        _coreToolWindows = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().ToList()
            .Select(a => (
                a.ContentId,
                a.Title,
                SiblingContentIds: ((LayoutAnchorablePane)a.Parent).Children.OfType<LayoutAnchorable>()
                    .Where(sibling => sibling != a)
                    .Select(sibling => sibling.ContentId)
                    .ToArray()))
            .ToList();

        ApplyDockingTheme(themeService.IsDark);
        themeService.Changed += () => ApplyDockingTheme(themeService.IsDark);

        // The Git pane is easy to miss tabbed behind Solution Explorer - bring it to the front
        // automatically when a newly opened solution's repo actually has something to show. Looked
        // up fresh by ContentId, not via the SourceControlAnchorable field: RestoreLayout can replace
        // DockManager.Layout with an entirely different tree (see its doc comment), which would leave
        // that XAML x:Name field pointing at a stale, detached object.
        viewModel.SourceControl.PendingChangesDetected += () =>
        {
            if (DockManager.Layout.Descendents().OfType<LayoutAnchorable>()
                .FirstOrDefault(a => a.ContentId == SourceControlPaneViewModel.PaneContentId) is { } anchorable)
            {
                anchorable.IsActive = true;
            }
        };

        // Attaches every tool window a plugin has registered by the time this runs, plus (via the
        // event) any registered afterward - see OnPluginToolWindowRegistered's doc comment for why
        // this can't just add straight into the XAML-declared pane.
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

        Loaded += (_, _) => RestoreLayout();
        Loaded += (_, _) => AttachMissingCoreToolWindows();
        Loaded += (_, _) => AttachPendingPluginToolWindows();
        Loaded += (_, _) => RestoreWindowBounds();
        Closing += (_, _) => SaveLayout();
        Closing += (_, _) => SaveWindowBounds();
        // Closing mid-stream abandons RunAssistantTurnsAsync before its finally runs, so capture
        // whatever the conversation has reached rather than losing it.
        Closing += (_, _) => viewModel.CopilotChat.PersistChatHistory();
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
    /// IPluginHost.RegisterToolWindow's contract) and either attaches it immediately or defers it -
    /// see <see cref="AttachPendingPluginToolWindows"/> for why deferring matters. Also makes the
    /// content resolvable by ContentId for RestoreLayout's LayoutSerializationCallback, so a saved
    /// layout that already had this exact pane (same plugin, same title, from a previous session)
    /// restores it in whatever position/floating state the user last left it in.
    /// </summary>
    private void OnPluginToolWindowRegistered(PluginToolWindow toolWindow)
    {
        var contentId = $"Plugin_{toolWindow.PluginName}_{toolWindow.Title}";
        var content = toolWindow.Factory();
        _contentById[contentId] = content;

        if (_layoutRestored)
        {
            AttachPluginToolWindowIfNotAlreadyPresent(contentId, toolWindow.Title, content);
        }
        else
        {
            _pendingPluginToolWindows.Add((contentId, toolWindow.Title, content));
        }
    }

    /// <summary>
    /// Attaches every tool window buffered since construction, now that RestoreLayout has run (with
    /// or without an actual saved-layout file - either way DockManager.Layout now holds whatever the
    /// window's real, final layout tree is). This can't happen earlier: when a saved layout.xml
    /// exists, XmlLayoutSerializer.Deserialize replaces DockManager.Layout with an entirely new tree
    /// built from that file, which would silently discard a LayoutAnchorable added to the
    /// XAML-declared tree beforehand.
    /// </summary>
    private void AttachPendingPluginToolWindows()
    {
        _layoutRestored = true;

        foreach (var (contentId, title, content) in _pendingPluginToolWindows)
        {
            AttachPluginToolWindowIfNotAlreadyPresent(contentId, title, content);
        }

        _pendingPluginToolWindows.Clear();
    }

    private void AttachPluginToolWindowIfNotAlreadyPresent(string contentId, string title, object content)
    {
        // A saved layout that already mentioned this exact ContentId was already reconstructed by
        // RestoreLayout's callback above (which resolved it via _contentById, populated by
        // OnPluginToolWindowRegistered before this runs) - adding a second LayoutAnchorable with the
        // same ContentId would duplicate the pane.
        if (DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Any(a => a.ContentId == contentId))
        {
            return;
        }

        if (FindSolutionExplorerPane() is not { } homePane)
        {
            return; // Defensive only - the Solution Explorer pane this anchors to always exists.
        }

        homePane.Children.Add(new LayoutAnchorable
        {
            Title = title,
            ContentId = contentId,
            // Unlike every core IDE pane (all CanClose="False" in MainWindow.xaml), a plugin's tool
            // window is the user's to close - there's no "reopen" command for it yet (that's the
            // same deferred scope as reloading a plugin at runtime), but closing it is harmless:
            // ContentId/content stay tracked in _contentById for as long as the app runs, and a
            // fresh launch re-registers it from scratch anyway.
            CanClose = true,
            Content = content,
        });
    }

    /// <summary>
    /// Re-adds whichever XAML-declared, CanClose="False" panes (Git, NuGet, Error List, ...)
    /// RestoreLayout's Deserialize dropped because a saved layout.xml from before that pane existed
    /// doesn't mention its ContentId - the same silent-discard RestoreLayout's own doc comment
    /// describes, which AttachPendingPluginToolWindows already works around for plugin panes but
    /// which nothing previously did for the app's own core panes. Without this, a pane added to
    /// MainWindow.xaml after a user's first run stays permanently invisible - not closed, just never
    /// reconstructed - with no in-app way to bring it back short of deleting layout.xml by hand.
    /// </summary>
    private void AttachMissingCoreToolWindows()
    {
        var present = DockManager.Layout.Descendents().OfType<LayoutAnchorable>()
            .Select(a => a.ContentId)
            .ToHashSet();

        foreach (var (contentId, title, siblingContentIds) in _coreToolWindows)
        {
            if (present.Contains(contentId))
            {
                continue;
            }

            // Prefer re-docking next to whichever of its original pane-mates survived the restore
            // (e.g. Git/NuGet back next to Solution Explorer), falling back to Solution Explorer's
            // pane if every one of them was also dropped.
            var homePane = siblingContentIds
                .Select(sibling => DockManager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .FirstOrDefault(a => a.ContentId == sibling)?.Parent as LayoutAnchorablePane)
                .FirstOrDefault(pane => pane is not null)
                ?? FindSolutionExplorerPane();

            if (homePane is null)
            {
                continue; // Defensive only - the Solution Explorer pane this anchors to always exists.
            }

            homePane.Children.Add(new LayoutAnchorable
            {
                Title = title,
                ContentId = contentId,
                CanClose = false,
                Content = _contentById[contentId],
            });

            present.Add(contentId);
        }
    }

    /// <summary>
    /// Finds the pane currently hosting Solution Explorer - a core pane that always exists - and
    /// uses it as the attachment point for a newly-registered plugin tool window or a dropped core
    /// pane with no surviving pane-mate, rather than reaching for the XAML-declared pane element
    /// directly: after RestoreLayout runs, DockingManager.Layout may be an entirely different tree
    /// than the one InitializeComponent built (see AttachPendingPluginToolWindows), which would leave
    /// a XAML x:Name field pointing at a detached, invisible pane. Looking it up fresh by Solution
    /// Explorer's ContentId works correctly either way, and also respects the user having moved
    /// Solution Explorer elsewhere.
    /// </summary>
    private LayoutAnchorablePane? FindSolutionExplorerPane() =>
        DockManager.Layout.Descendents().OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == SolutionExplorerPaneViewModel.PaneContentId)?.Parent as LayoutAnchorablePane;

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

    private void RestoreLayout()
    {
        if (File.Exists(LayoutFilePath))
        {
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

        EnsureCorePanesPresent();
    }

    /// <summary>
    /// Re-adds any core anchorable pane missing from the just-restored layout - happens when a saved
    /// layout.xml predates that pane's introduction (e.g. Git, NuGet, Error List, and Tests were all
    /// added after some users' first layout.xml was written), which XmlLayoutSerializer.Deserialize's
    /// wholesale tree replacement would otherwise silently drop forever: there is no in-app "reset
    /// layout" command, so without this the only fix is deleting layout.xml by hand. A no-op when no
    /// saved layout existed at all (DockManager.Layout is still the full default XAML tree) or every
    /// core pane was already present. Mirrors AttachPluginToolWindowIfNotAlreadyPresent's identical
    /// problem for plugin tool windows.
    /// </summary>
    private void EnsureCorePanesPresent()
    {
        var anchorablesByContentId = DockManager.Layout.Descendents().OfType<LayoutAnchorable>()
            .Where(a => a.ContentId is not null)
            .ToDictionary(a => a.ContentId!, a => a);

        foreach (var (contentId, title, preferredNeighborContentId) in CoreAnchorablePanes)
        {
            if (anchorablesByContentId.ContainsKey(contentId) || !_contentById.TryGetValue(contentId, out var content))
            {
                continue;
            }

            // The pane this one normally sits alongside may be missing too (e.g. a very old or
            // hand-edited layout.xml) - fall back to Solution Explorer's pane, the one core
            // anchorable guaranteed to exist in any layout the app itself ever saved.
            var homePane = (anchorablesByContentId.TryGetValue(preferredNeighborContentId, out var neighbor)
                ? neighbor.Parent as LayoutAnchorablePane
                : null) ?? FindPluginToolWindowHomePane();

            if (homePane is null)
            {
                continue; // defensive only
            }

            var restored = new LayoutAnchorable
            {
                Title = title,
                ContentId = contentId,
                CanClose = false,
                Content = content,
            };
            homePane.Children.Add(restored);
            anchorablesByContentId[contentId] = restored; // so a later entry in the loop can neighbor off this one too
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
        }

        // Folder nodes have no FullPath in this tree (see SolutionTreeNode.GetOrCreateFolder) - a
        // plugin command can't be offered a meaningful path for one, so it's skipped for those,
        // same as it would be for any node kind a plugin's canExecute itself rejects.
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
