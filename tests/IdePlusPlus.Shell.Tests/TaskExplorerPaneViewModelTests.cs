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

        var pane = NewPane();
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

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, [deep]);

        pane.Tree.Count.ShouldBe(1);
        pane.Tree[0].Children.Select(c => c.Name).ShouldBe(["bundle"]);
    }

    [Fact]
    public async Task SetSolutionAsync_NoManifests_ReportsTheEmptyState()
    {
        using var solution = new ScratchSolutionDirectory();

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);

        pane.Tree.ShouldBeEmpty();
        pane.Summary.ShouldBe("No tasks found.");
    }

    /// <summary>Reloading has to replace the tree, not append to it.</summary>
    [Fact]
    public async Task SetSolutionAsync_CalledAgainForAnotherSolution_ReplacesTheTree()
    {
        using var first = new ScratchSolutionDirectory();
        first.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        using var second = new ScratchSolutionDirectory();
        second.WriteFile("package.json", """{ "scripts": { "lint": "eslint .", "serve": "vite" } }""");

        var pane = NewPane();
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

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);

        pane.RunSelectedCommand.CanExecute(null).ShouldBeFalse("nothing is selected yet");

        pane.SelectedNode = pane.Tree[0];
        pane.RunSelectedCommand.CanExecute(null).ShouldBeFalse("a manifest node is not runnable");

        pane.SelectedNode = pane.Tree[0].Children[0];
        pane.RunSelectedCommand.CanExecute(null).ShouldBeTrue("a task node is runnable");
    }

    [Fact]
    public async Task ToggleBinding_BindsThenUnbinds_AndPersists()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);
        pane.SelectedNode = pane.Tree[0].Children[0];

        pane.ToggleBindingCommand.Execute("BeforeBuild");

        TaskBindingStore.Load(solution.Path).ShouldBe([new TaskBinding("package.json", "build", TaskBindingKind.BeforeBuild)]);
        pane.Tree[0].Children[0].Bindings.ShouldBe([TaskBindingKind.BeforeBuild]);
        pane.Tree[0].Children[0].Label.ShouldBe("build  (Before Build)");

        pane.ToggleBindingCommand.Execute("BeforeBuild");

        TaskBindingStore.Load(solution.Path).ShouldBeEmpty();
        pane.Tree[0].Children[0].Bindings.ShouldBeEmpty();
    }

    /// <summary>The tree is rebuilt on every binding change, so the selection has to survive it or
    /// the second menu click silently does nothing.</summary>
    [Fact]
    public async Task ToggleBinding_KeepsTheSelectionAcrossTheRebuild()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc", "test": "jest" } }""");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);
        pane.SelectedNode = pane.Tree[0].Children[1];

        pane.ToggleBindingCommand.Execute("AfterBuild");

        pane.SelectedNode.ShouldNotBeNull();
        pane.SelectedNode.Name.ShouldBe("test");
        pane.RunSelectedCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task ToggleBinding_SupportsSeveralPointsForOneTask()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);
        pane.SelectedNode = pane.Tree[0].Children[0];

        pane.ToggleBindingCommand.Execute("BeforeBuild");
        pane.SelectedNode = pane.Tree[0].Children[0];
        pane.ToggleBindingCommand.Execute("AfterClean");

        pane.Tree[0].Children[0].Bindings.ShouldBe([TaskBindingKind.BeforeBuild, TaskBindingKind.AfterClean]);
    }

    [Fact]
    public async Task ToggleBinding_UnrecognizedKind_IsIgnored()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);
        pane.SelectedNode = pane.Tree[0].Children[0];

        Should.NotThrow(() => pane.ToggleBindingCommand.Execute("Whenever"));

        TaskBindingStore.Load(solution.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task ToggleBinding_IsNotAvailableForAManifestNode()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);
        pane.SelectedNode = pane.Tree[0];

        pane.ToggleBindingCommand.CanExecute("BeforeBuild").ShouldBeFalse();
    }

    [Fact]
    public async Task SetSolutionAsync_ReloadsExistingBindingsFromDisk()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");
        TaskBindingStore.Save(solution.Path, [new TaskBinding("package.json", "build", TaskBindingKind.BeforeBuild)]);

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);

        pane.Tree[0].Children[0].Bindings.ShouldBe([TaskBindingKind.BeforeBuild]);
        pane.Summary.ShouldBe("1 task in 1 manifest, 1 bound");
    }

    /// <summary>Binding a watch task is allowed - it is the user's call - but it has to say what
    /// will happen, since the failure mode is every build taking the timeout.</summary>
    [Fact]
    public async Task ToggleBinding_WarnsWhenBindingAWatchTask()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "watch": "tsc --watch" } }""");

        var output = new List<string>();
        var pane = NewPane();
        pane.OutputLine = output.Add;
        await pane.SetSolutionAsync(solution.Path, []);
        pane.SelectedNode = pane.Tree[0].Children[0];

        pane.ToggleBindingCommand.Execute("BeforeBuild");

        output.ShouldContain(line => line.Contains("looks like a watch or server task"));
    }

    [Fact]
    public async Task ToggleBinding_DoesNotWarnForAnOrdinaryTask()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        var output = new List<string>();
        var pane = NewPane();
        pane.OutputLine = output.Add;
        await pane.SetSolutionAsync(solution.Path, []);
        pane.SelectedNode = pane.Tree[0].Children[0];

        pane.ToggleBindingCommand.Execute("BeforeBuild");

        output.ShouldNotContain(line => line.Contains("looks like a watch"));
    }

    [Fact]
    public async Task RunBoundTasksAsync_NothingBound_IsANoOp()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);

        await Should.NotThrowAsync(() => pane.RunBoundTasksAsync(TaskBindingKind.BeforeBuild));
        pane.IsBusy.ShouldBeFalse();
    }

    /// <summary>
    /// A gulpfile must never be executed without approval, so an unapproved one is found and
    /// reported but not listed. This is the security-relevant assertion in this file: if it starts
    /// failing, opening a cloned repository runs its gulpfile.
    /// </summary>
    [Fact]
    public async Task SetSolutionAsync_GulpfileIsNotScannedWithoutApproval()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("gulpfile.js", "throw new Error('this must never run');");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);

        pane.Tree.ShouldBeEmpty();
        pane.Summary.ShouldBe("No tasks found. 1 gulp/grunt file(s) not scanned.");
        pane.ScanJavaScriptTasksCommand.CanExecute(null).ShouldBeTrue("the user should be offered the scan");
    }

    [Fact]
    public async Task SetSolutionAsync_NpmTasksStillListedAlongsideAnUnscannedGulpfile()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");
        solution.WriteFile("gulpfile.js", "throw new Error('this must never run');");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);

        pane.Tree[0].Children.Select(c => c.Name).ShouldBe(["build"]);
        pane.Summary.ShouldBe("1 task in 1 manifest 1 gulp/grunt file(s) not scanned.");
    }

    [Fact]
    public async Task ScanCommand_IsUnavailableWhenThereIsNoJavaScriptManifest()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");

        var pane = NewPane();
        await pane.SetSolutionAsync(solution.Path, []);

        pane.ScanJavaScriptTasksCommand.CanExecute(null).ShouldBeFalse();
    }

    /// <summary>A declined prompt must leave the solution unapproved, not approved-by-default.</summary>
    [Fact]
    public async Task ScanCommand_Declined_ListsNothingAndStaysOffered()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("gulpfile.js", "throw new Error('this must never run');");

        var discovery = NewDiscoveryService();
        discovery.TrustPrompt = _ => Task.FromResult(false);
        var pane = new TaskExplorerPaneViewModel(new TaskRunService(), discovery);
        await pane.SetSolutionAsync(solution.Path, []);

        await pane.ScanJavaScriptTasksCommand.ExecuteAsync(null);

        pane.Tree.ShouldBeEmpty();
        pane.ScanJavaScriptTasksCommand.CanExecute(null).ShouldBeTrue();
    }

    /// <summary>No prompt wired - a headless context - must approve nothing rather than everything.</summary>
    [Fact]
    public async Task ScanCommand_WithNoPromptWired_FailsClosed()
    {
        using var solution = new ScratchSolutionDirectory();
        solution.WriteFile("gulpfile.js", "throw new Error('this must never run');");

        var logged = new List<string>();
        var discovery = NewDiscoveryService();
        discovery.OnLog = logged.Add;
        var pane = new TaskExplorerPaneViewModel(new TaskRunService(), discovery);
        await pane.SetSolutionAsync(solution.Path, []);

        await pane.ScanJavaScriptTasksCommand.ExecuteAsync(null);

        pane.Tree.ShouldBeEmpty();
        logged.ShouldContain(line => line.Contains("has not been approved"));
    }

    private static JavaScriptTaskDiscoveryService NewDiscoveryService() =>
        new(new JavaScriptTaskTrustStore(
            Path.Combine(Path.GetTempPath(), $"ideplusplus-test-trust-{Guid.NewGuid():N}.json")));

    /// <summary>
    /// The trust store is pointed at a throwaway path so no test reads or writes the real one in
    /// %LOCALAPPDATA%. Nothing here approves a solution, so gulp/grunt discovery never runs and no
    /// test executes a JavaScript file.
    /// </summary>
    private static TaskExplorerPaneViewModel NewPane() => new(new TaskRunService(), NewDiscoveryService());

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
