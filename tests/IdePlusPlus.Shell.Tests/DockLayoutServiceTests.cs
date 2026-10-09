using Xunit;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AvalonDock;
using AvalonDock.Layout;
using IdePlusPlus.Shell;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;

namespace IdePlusPlus.Shell.Tests;

public sealed class DockLayoutServiceTests : IDisposable
{
    private const string OutputId = OutputPaneViewModel.PaneContentId;
    private const string SolutionId = SolutionExplorerPaneViewModel.PaneContentId;
    private const string GitId = SourceControlPaneViewModel.PaneContentId;

    private readonly string _layoutFile = Path.Combine(Path.GetTempPath(), $"idepp-layout-{Guid.NewGuid():N}.xml");

    public void Dispose()
    {
        if (File.Exists(_layoutFile))
        {
            File.Delete(_layoutFile);
        }
    }

    [Fact]
    public void Toggle_hides_then_shows_a_pane() => OnSta(() =>
    {
        var (_, layout) = Create();

        layout.Toggle(OutputId);
        Visible(layout, OutputId).ShouldBeFalse();

        layout.Toggle(OutputId);
        Visible(layout, OutputId).ShouldBeTrue();
    });

    [Fact]
    public void Toggle_rebuilds_a_pane_that_was_closed_outright() => OnSta(() =>
    {
        var (dock, layout) = Create();
        var output = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == OutputId);
        output.Close();
        Visible(layout, OutputId).ShouldBeFalse();

        layout.Toggle(OutputId);

        Visible(layout, OutputId).ShouldBeTrue();
        Count(dock, OutputId).ShouldBe(1);
    });

    [Fact]
    public void A_hidden_pane_stays_hidden_across_save_and_restore_without_duplicating() => OnSta(() =>
    {
        var (_, layout) = Create();
        layout.Toggle(OutputId);
        layout.Save();

        var (dock2, layout2) = Create();
        layout2.Restore();

        Visible(layout2, OutputId).ShouldBeFalse();
        Count(dock2, OutputId).ShouldBe(1);
        Visible(layout2, GitId).ShouldBeTrue();
    });

    [Fact]
    public void Reset_brings_hidden_panes_back_and_restores_the_default_arrangement() => OnSta(() =>
    {
        var (dock, layout) = Create();
        layout.Toggle(OutputId);
        layout.Toggle(GitId);
        var git = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == GitId);
        var solutionPane = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == SolutionId).Parent;
        git.Hide(); // already hidden; Hide again must not throw

        layout.Reset();

        foreach (var toolWindow in layout.ToolWindows)
        {
            toolWindow.IsVisible.ShouldBeTrue(toolWindow.Title);
            Count(dock, toolWindow.ContentId).ShouldBe(1, toolWindow.Title);
        }

        // Git started life tabbed with Solution Explorer, and that is where it goes back.
        var gitAfter = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == GitId);
        var solutionAfter = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(a => a.ContentId == SolutionId);
        gitAfter.Parent.ShouldBeSameAs(solutionAfter.Parent);
        solutionPane.ShouldNotBeNull();
    });

    [Fact]
    public void Reset_keeps_open_documents() => OnSta(() =>
    {
        var (dock, layout) = Create();
        var documents = (ObservableCollection<object>)dock.DocumentsSource;
        var doc = new object();
        documents.Add(doc);
        dock.Layout.Descendents().OfType<LayoutDocument>().ShouldContain(d => ReferenceEquals(d.Content, doc));

        layout.Reset();

        dock.Layout.Descendents().OfType<LayoutDocument>().ShouldContain(d => ReferenceEquals(d.Content, doc));
    });

    [Fact]
    public void Plugin_pane_registered_after_restore_can_be_hidden_and_survives_reset() => OnSta(() =>
    {
        var (dock, layout) = Create();
        layout.Restore();
        layout.RegisterPluginToolWindow("Plugin_P_Tool", "Tool", new object());
        Visible(layout, "Plugin_P_Tool").ShouldBeTrue();

        layout.Toggle("Plugin_P_Tool");
        Visible(layout, "Plugin_P_Tool").ShouldBeFalse();

        layout.Reset();
        Visible(layout, "Plugin_P_Tool").ShouldBeTrue();
        Count(dock, "Plugin_P_Tool").ShouldBe(1);
    });

    [Fact]
    public void Activate_does_not_resurrect_a_hidden_pane() => OnSta(() =>
    {
        var (_, layout) = Create();
        layout.Toggle(GitId);

        layout.Activate(GitId);

        Visible(layout, GitId).ShouldBeFalse();
    });

    private static bool Visible(DockLayoutService layout, string contentId) =>
        layout.ToolWindows.Single(t => t.ContentId == contentId).IsVisible;

    private static int Count(DockingManager dock, string contentId) =>
        dock.Layout.Descendents().OfType<LayoutAnchorable>().Count(a => a.ContentId == contentId);

    /// <summary>Mirrors the shape of MainWindow.xaml: Solution Explorer + Git on the left, Output below
    /// the document area, documents bound through DocumentsSource.</summary>
    private (DockingManager Dock, DockLayoutService Layout) Create()
    {
        var content = new Dictionary<string, object>
        {
            [SolutionId] = new object(),
            [GitId] = new object(),
            [OutputId] = new object(),
        };

        var left = new LayoutAnchorablePane { DockWidth = new GridLength(220) };
        left.Children.Add(new LayoutAnchorable { ContentId = SolutionId, Title = "Solution Explorer", Content = content[SolutionId] });
        left.Children.Add(new LayoutAnchorable { ContentId = GitId, Title = "Git", Content = content[GitId] });

        var bottom = new LayoutAnchorablePane { DockHeight = new GridLength(180) };
        bottom.Children.Add(new LayoutAnchorable { ContentId = OutputId, Title = "Output", Content = content[OutputId] });

        var docs = new LayoutDocumentPane();
        var center = new LayoutPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        center.Children.Add(docs);
        center.Children.Add(bottom);

        var root = new LayoutPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        root.Children.Add(left);
        root.Children.Add(center);

        var dock = new DockingManager
        {
            Layout = new LayoutRoot { RootPanel = root },
            DocumentsSource = new ObservableCollection<object>(),
        };

        return (dock, new DockLayoutService(dock, content, _layoutFile));
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
