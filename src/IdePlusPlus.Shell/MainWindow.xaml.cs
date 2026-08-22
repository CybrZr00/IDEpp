using System.IO;
using System.Windows;
using AvalonDock.Serializer.Xml;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

public partial class MainWindow : Window
{
    private static readonly string LayoutFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus",
        "layout.xml");

    private readonly Dictionary<string, object> _contentById;

    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // LayoutAnchorable.Content is a plain CLR property, not a DependencyProperty, so it
        // can't be set via {Binding} in XAML — assign the pane view models here instead. The
        // window's DataTemplates (keyed by view model type) render each one.
        SolutionExplorerAnchorable.Content = viewModel.SolutionExplorer;
        OutputAnchorable.Content = viewModel.Output;
        CopilotChatAnchorable.Content = viewModel.CopilotChat;
        WelcomeDocument.Content = viewModel.Welcome;

        _contentById = new Dictionary<string, object>
        {
            [SolutionExplorerPaneViewModel.PaneContentId] = viewModel.SolutionExplorer,
            [OutputPaneViewModel.PaneContentId] = viewModel.Output,
            [CopilotChatPaneViewModel.PaneContentId] = viewModel.CopilotChat,
            [WelcomeDocumentViewModel.ContentId] = viewModel.Welcome,
        };

        Loaded += (_, _) => RestoreLayout();
        Closing += (_, _) => SaveLayout();
    }

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
}
