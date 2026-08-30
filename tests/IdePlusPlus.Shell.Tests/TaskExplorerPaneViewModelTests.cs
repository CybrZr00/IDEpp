using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.Tasks;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// Covers the pane's own half of solution load - the step MainWindowViewModel.DiscoverTasksAsync
/// calls into. No WPF dependency: the pane is deliberately dispatcher-free, with output marshalling
/// left to whoever sets OutputLine.
/// </summary>
public class TaskExplorerPaneViewModelTests
{
    [Fact]
    public async Task SetSolutionAsync_BuildsAManifestTreeFromTheSolutionDirectory()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """
            { "scripts": { "build": "tsc", "test": "jest" } }
            """);

        var pane = new TaskExplorerPaneViewModel(new TaskRunService());
        await pane.SetSolutionAsync(solution.Path, []);

        pane.Tree.Count.ShouldBe(1);
        pane.Tree[0].Kind.ShouldBe(TaskTreeNodeKind.Manifest);
        pane.Tree[0].Children.Select(c => c.Name).ShouldBe(["build", "test"]);
        pane.Tree[0].Children[0].Definition.ShouldNotBeNull();
        pane.Summary.ShouldBe("2 tasks in 1 manifest");
    }

    [Fact]
    public async Task SetSolutionAsync_AlsoTakesManifestsFromTheSolutionTree()
    {
        using var solution = new ScratchSolutionDirectory();

        // Deeper than the shallow scan reaches, so it can only arrive via the tree paths - the same
        // list MainWindowViewModel hands over from SolutionLoader's file nodes.
        var deep = solution.WriteFile("src/Web/Client/package.json", """{ "scripts": { "bundle": "vite build" } }""");

        var pane = new TaskExplorerPaneViewModel(new TaskRunService());
        await pane.SetSolutionAsync(solution.Path, [deep]);

        pane.Tree.Count.ShouldBe(1);
        pane.Tree[0].Children.Select(c => c.Name).ShouldBe(["bundle"]);
    }

    [Fact]
    public async Task SetSolutionAsync_NoManifests_ReportsTheEmptyState()
    {
        using var solution = new ScratchSolutionDirectory();

        var pane = new TaskExplorerPaneViewModel(new TaskRunService());
        await pane.SetSolutionAsync(solution.Path, []);

        pane.Tree.ShouldBeEmpty();
        pane.Summary.ShouldBe("No package.json scripts found.");
    }

    /// <summary>Reloading has to replace the tree, not append to it.</summary>
    [Fact]
    public async Task SetSolutionAsync_CalledAgainForAnotherSolution_ReplacesTheTree()
    {
        using var first = new ScratchSolutionDirectory();
        first.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        using var second = new ScratchSolutionDirectory();
        second.WriteFile("package.json", """{ "scripts": { "lint": "eslint .", "serve": "vite" } }""");

        var pane = new TaskExplorerPaneViewModel(new TaskRunService());
        await pane.SetSolutionAsync(first.Path, []);
        await pane.SetSolutionAsync(second.Path, []);

        pane.Tree.Count.ShouldBe(1);
        pane.Tree[0].Children.Select(c => c.Name).ShouldBe(["lint", "serve"]);
    }

    [Fact]
    public async Task RunSelected_IsOnlyEnabledForATaskNode()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        var pane = new TaskExplorerPaneViewModel(new TaskRunService());
        await pane.SetSolutionAsync(solution.Path, []);

        pane.RunSelectedCommand.CanExecute(null).ShouldBeFalse("nothing is selected yet");

        pane.SelectedNode = pane.Tree[0];
        pane.RunSelectedCommand.CanExecute(null).ShouldBeFalse("a manifest node is not runnable");

        pane.SelectedNode = pane.Tree[0].Children[0];
        pane.RunSelectedCommand.CanExecute(null).ShouldBeTrue("a task node is runnable");
    }

    private sealed class ScratchSolutionDirectory : IDisposable
    {
        public ScratchSolutionDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ideplusplus-taskpane-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
