using Microsoft.Build.Locator;

namespace IdePlusPlus.ProjectSystem;

public static class SolutionLoader
{
    private static readonly object RegistrationLock = new();
    private static bool _msBuildRegistered;

    /// <summary>
    /// Must run before any code in the process touches a Microsoft.Build.* type — the CLR
    /// resolves a method's referenced assemblies when that method is first JIT'd, which for
    /// unrelated code can happen before this call if it isn't made early (e.g. as the first
    /// line of App.OnStartup). Safe to call more than once, and from more than one thread.
    ///
    /// <para>The thread-safety is not incidental. RegisterDefaults throws if MSBuild is already
    /// registered, and the previous check-then-set had no lock: two callers could both read the
    /// flag as false and the loser would take an InvalidOperationException. That is reachable
    /// wherever two threads can reach this at once - xUnit runs test classes in parallel, and more
    /// than one of them calls in here - and it fails intermittently, which is the worst way for it
    /// to fail.</para>
    ///
    /// <para>IsRegistered is checked as well as the local flag because registration is process-wide
    /// while the flag is per-class: another component (RoslynScratchSolutionFixture keeps its own
    /// equivalent bool) may have registered already, and this must not throw because of it.</para>
    /// </summary>
    public static void EnsureMSBuildRegistered()
    {
        if (Volatile.Read(ref _msBuildRegistered))
        {
            return;
        }

        lock (RegistrationLock)
        {
            if (_msBuildRegistered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            Volatile.Write(ref _msBuildRegistered, true);
        }
    }

    public static SolutionTreeNode Load(string solutionPath)
    {
        EnsureMSBuildRegistered();
        return LoadCore(solutionPath);
    }

    // Isolated in its own method (rather than inlined into Load) so its MSBuild type
    // references are only JIT-resolved once EnsureMSBuildRegistered has already run.
    private static SolutionTreeNode LoadCore(string solutionPath)
    {
        var solutionName = Path.GetFileNameWithoutExtension(solutionPath);
        var root = new SolutionTreeNode(solutionName, solutionPath, SolutionTreeNodeKind.Solution);

        var solutionFile = Microsoft.Build.Construction.SolutionFile.Parse(solutionPath);
        foreach (var projectInSolution in solutionFile.ProjectsInOrder)
        {
            if (projectInSolution.ProjectType != Microsoft.Build.Construction.SolutionProjectType.KnownToBeMSBuildFormat)
            {
                continue;
            }

            root.Children.Add(LoadProject(projectInSolution.ProjectName, projectInSolution.AbsolutePath));
        }

        return root;
    }

    private static SolutionTreeNode LoadProject(string projectName, string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;

        var evaluated = Microsoft.Build.Evaluation.Project.FromFile(
            projectPath,
            new Microsoft.Build.Definition.ProjectOptions());

        SolutionTreeNode projectNode;
        try
        {
            // Multi-targeted projects (MAUI's TargetFrameworks, plural) declare no single
            // $(TargetFramework) - reading the plural property here is safe even without mobile
            // workloads installed, since this is the "outer" cross-targeting evaluation, which
            // doesn't import any platform-specific SDK targets (those only load per-TFM, at build
            // time for a chosen $(TargetFramework)).
            var targetFrameworks = evaluated.GetPropertyValue("TargetFrameworks");
            var windowsTargetFramework = string.IsNullOrWhiteSpace(targetFrameworks)
                ? null
                : targetFrameworks
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(tfm => tfm.Contains("-windows", StringComparison.OrdinalIgnoreCase));

            var isTestProject = TestProjectDetector.IsTestProject(evaluated);

            projectNode = new SolutionTreeNode(projectName, projectPath, SolutionTreeNodeKind.Project)
            {
                TargetFramework = windowsTargetFramework,
                IsTestProject = isTestProject,
            };

            var visibleItemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Compile", "None", "Content", "Page", "ApplicationDefinition", "EmbeddedResource",
            };

            foreach (var item in evaluated.Items.Where(i => visibleItemTypes.Contains(i.ItemType)))
            {
                var relativePath = item.EvaluatedInclude.Replace('/', Path.DirectorySeparatorChar);
                var parts = relativePath.Split(Path.DirectorySeparatorChar);
                var fileName = parts[^1];
                var folder = projectNode.GetOrCreateFolder(parts[..^1]);

                if (!folder.Children.Any(c => c.Kind == SolutionTreeNodeKind.File && c.Name == fileName))
                {
                    var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
                    folder.Children.Add(new SolutionTreeNode(fileName, fullPath, SolutionTreeNodeKind.File));
                }
            }
        }
        finally
        {
            Microsoft.Build.Evaluation.ProjectCollection.GlobalProjectCollection.UnloadProject(evaluated);
        }

        return projectNode;
    }
}
