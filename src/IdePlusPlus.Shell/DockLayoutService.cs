using System.IO;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;

namespace IdePlusPlus.Shell;

/// <summary>One row of the View > Tool Windows menu: a tool pane the shell knows about, and whether
/// it is currently showing.</summary>
public sealed record ToolWindowInfo(string ContentId, string Title, bool IsVisible);

/// <summary>
/// Owns everything about where the tool panes live: restoring and saving the user's layout, putting
/// back panes a saved layout predates, showing and hiding individual panes, and resetting the lot to
/// the layout MainWindow.xaml declares.
///
/// Must be constructed while DockingManager.Layout is still the tree InitializeComponent built, because
/// that is the tree it snapshots as "the default" - both as a list of core panes (used to re-add any a
/// saved layout.xml predates) and as serialized XML (what <see cref="Reset"/> loads).
/// </summary>
public sealed class DockLayoutService
{
    private readonly DockingManager _dockManager;
    private readonly Dictionary<string, object> _contentById;
    private readonly string _layoutFilePath;
    private readonly string _defaultLayoutXml;

    // Snapshot of every pane MainWindow.xaml declares (ContentId/Title/the ContentIds of its original
    // pane-mates). XmlLayoutSerializer.Deserialize replaces DockManager.Layout with a tree built from
    // the file, silently dropping any pane the file predates, so this is what AttachMissingCoreToolWindows
    // adds back from.
    private readonly List<(string ContentId, string Title, string[] SiblingContentIds)> _coreToolWindows;

    // Plugins register during LoadPlugins(), which runs before the Loaded-time Restore - and a pane added
    // to the XAML tree before Deserialize would be discarded with it. So they are only recorded until
    // Restore has run, then attached; later registrations attach straight away.
    private readonly List<(string ContentId, string Title)> _pluginToolWindows = [];
    private bool _restored;

    public DockLayoutService(DockingManager dockManager, Dictionary<string, object> contentById, string layoutFilePath)
    {
        _dockManager = dockManager;
        _contentById = contentById;
        _layoutFilePath = layoutFilePath;

        // Every LayoutAnchorable that exists right now is, by construction, one MainWindow.xaml declared
        // directly, so this is the complete set of core panes without filtering on CanClose. (CanClose
        // can't be used for that: DockingManager.LayoutItemContainerStyle writes CanClose=True back into
        // every model.)
        _coreToolWindows = _dockManager.Layout.Descendents().OfType<LayoutAnchorable>().ToList()
            .Select(a => (
                a.ContentId,
                a.Title,
                SiblingContentIds: ((LayoutAnchorablePane)a.Parent).Children.OfType<LayoutAnchorable>()
                    .Where(sibling => sibling != a)
                    .Select(sibling => sibling.ContentId)
                    .ToArray()))
            .ToList();

        using var writer = new StringWriter();
        new XmlLayoutSerializer(_dockManager).Serialize(writer);
        _defaultLayoutXml = writer.ToString();
    }

    /// <summary>Every tool pane (core and plugin), in the order the menu lists them.</summary>
    public IReadOnlyList<ToolWindowInfo> ToolWindows =>
        _coreToolWindows.Select(c => (c.ContentId, c.Title))
            .Concat(_pluginToolWindows)
            .Select(t => new ToolWindowInfo(t.ContentId, t.Title, FindAnchorable(t.ContentId) is { IsVisible: true }))
            .ToList();

    /// <summary>Called once the plugin's tool window content exists and is resolvable by ContentId.</summary>
    public void RegisterPluginToolWindow(string contentId, string title, object content)
    {
        _contentById[contentId] = content;
        _pluginToolWindows.Add((contentId, title));

        if (_restored)
        {
            AttachPluginToolWindow(contentId, title);
        }
    }

    public void Restore()
    {
        if (File.Exists(_layoutFilePath))
        {
            try
            {
                Deserialize(serializer => serializer.Deserialize(_layoutFilePath));
            }
            catch (Exception)
            {
                // Corrupt/incompatible layout file from a previous version - keep the default XAML layout.
            }
        }

        AttachMissingToolWindows();
        _restored = true;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_layoutFilePath)!);
        new XmlLayoutSerializer(_dockManager).Serialize(_layoutFilePath);
    }

    /// <summary>Throws away the current arrangement (and any hidden state) for the one MainWindow.xaml
    /// declares. Open documents are not part of the layout file, so they stay open.</summary>
    public void Reset()
    {
        Deserialize(serializer =>
        {
            using var reader = new StringReader(_defaultLayoutXml);
            serializer.Deserialize(reader);
        });

        AttachMissingToolWindows();
    }

    /// <summary>Hides a visible tool pane, or brings a hidden (or closed) one back and focuses it.</summary>
    public void Toggle(string contentId)
    {
        if (FindAnchorable(contentId) is not { } anchorable)
        {
            // Closed outright rather than hidden: nothing left to show, so rebuild it.
            AttachMissingToolWindows();
            Activate(contentId);
            return;
        }

        if (anchorable.IsVisible)
        {
            anchorable.Hide();
        }
        else
        {
            anchorable.Show();
            anchorable.IsActive = true;
        }
    }

    /// <summary>Brings a pane to the front of its tab group - but only if it is showing; this never
    /// resurrects a pane the user hid.</summary>
    public void Activate(string contentId)
    {
        if (FindAnchorable(contentId) is { IsVisible: true } anchorable)
        {
            anchorable.IsActive = true;
        }
    }

    private void Deserialize(Action<XmlLayoutSerializer> deserialize)
    {
        var serializer = new XmlLayoutSerializer(_dockManager);
        serializer.LayoutSerializationCallback += (_, args) =>
        {
            if (args.Model.ContentId is { } contentId && _contentById.TryGetValue(contentId, out var content))
            {
                args.Content = content;
            }
            else
            {
                // Unknown content (e.g. a document pane from a previous session, or a stale
                // ContentId from an older build) - drop it rather than restoring a broken pane.
                args.Cancel = true;
            }
        };

        deserialize(serializer);
    }

    /// <summary>Descendents() includes hidden panes (they stay in the tree, flagged not visible), so
    /// "found" means present, not showing - check IsVisible for that.</summary>
    private LayoutAnchorable? FindAnchorable(string contentId) =>
        _dockManager.Layout.Descendents().OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);

    /// <summary>Re-adds whichever core and plugin panes are not in the layout at all - because a saved
    /// layout.xml from before the pane existed doesn't mention it, because Reset replaced the tree, or
    /// because the user closed it outright.</summary>
    private void AttachMissingToolWindows()
    {
        foreach (var (contentId, title, siblingContentIds) in _coreToolWindows)
        {
            if (FindAnchorable(contentId) is not null)
            {
                continue;
            }

            // Prefer re-docking next to whichever of its original pane-mates is still showing
            // (e.g. Git/NuGet back next to Solution Explorer).
            var homePane = siblingContentIds
                .Select(sibling => FindAnchorable(sibling)?.Parent as LayoutAnchorablePane)
                .FirstOrDefault(pane => pane is not null)
                ?? FindHomePane();

            homePane.Children.Add(new LayoutAnchorable
            {
                Title = title,
                ContentId = contentId,
                CanClose = false,
                Content = _contentById[contentId],
            });
        }

        foreach (var (contentId, title) in _pluginToolWindows)
        {
            AttachPluginToolWindow(contentId, title);
        }
    }

    private void AttachPluginToolWindow(string contentId, string title)
    {
        // A saved layout that already mentioned this ContentId was reconstructed by Deserialize's
        // callback; a second LayoutAnchorable with the same ContentId would duplicate the pane.
        if (FindAnchorable(contentId) is not null)
        {
            return;
        }

        FindHomePane().Children.Add(new LayoutAnchorable
        {
            Title = title,
            ContentId = contentId,
            // Unlike the core panes, a plugin's tool window is the user's to close; the View > Tool
            // Windows menu brings it back.
            CanClose = true,
            Content = _contentById[contentId],
        });
    }

    /// <summary>The pane panes with nowhere better to go are added to: the one hosting Solution Explorer
    /// if that is showing (looked up fresh, never via a XAML x:Name field, which goes stale when
    /// Deserialize swaps the tree), otherwise any anchorable pane, otherwise a new one.</summary>
    private LayoutAnchorablePane FindHomePane()
    {
        if (FindAnchorable(ToolPanes.SolutionExplorerPaneViewModel.PaneContentId)?.Parent is LayoutAnchorablePane solutionExplorerPane)
        {
            return solutionExplorerPane;
        }

        if (_dockManager.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault() is { } anyPane)
        {
            return anyPane;
        }

        // The user hid every pane, and an emptied pane is garbage-collected out of the tree.
        var pane = new LayoutAnchorablePane { DockWidth = new System.Windows.GridLength(220) };
        _dockManager.Layout.RootPanel.Children.Insert(0, pane);
        return pane;
    }
}
