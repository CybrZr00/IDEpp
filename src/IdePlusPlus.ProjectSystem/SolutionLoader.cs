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
        return LoadCore(solutionPath, unloadedRelativePaths: null);
    }

    /// <summary>
    /// Loads the solution's project/file tree, treating <paramref name="unloadedRelativePaths"/>
    /// (solution-relative, see <see cref="UnloadedProjectsStore"/>) as placeholders: those projects get
    /// a Project node with no children and are <b>never evaluated</b>, so nothing their SDK/workload
    /// would import is touched - a solution with a MAUI Android project whose workload is missing still
    /// opens, with that project unloaded.
    ///
    /// <para>A project that is loaded but fails to evaluate becomes a <see cref="SolutionTreeNode.LoadError"/>
    /// node rather than failing the whole load.</para>
    /// </summary>
    public static SolutionTreeNode Load(string solutionPath, IReadOnlySet<string>? unloadedRelativePaths = null)
    {
        EnsureMSBuildRegistered();
        return LoadCore(solutionPath, unloadedRelativePaths);
    }

    // Isolated in its own method (rather than inlined into Load) so its MSBuild type
    // references are only JIT-resolved once EnsureMSBuildRegistered has already run.
    private static SolutionTreeNode LoadCore(string solutionPath, IReadOnlySet<string>? unloadedRelativePaths)
    {
        var solutionName = Path.GetFileNameWithoutExtension(solutionPath);
        var root = new SolutionTreeNode(solutionName, solutionPath, SolutionTreeNodeKind.Solution);

        // Normalised here rather than trusting the caller's set: the pane may hand over a plain
        // HashSet<string> built with the default comparer, and matching is case- and
        // separator-insensitive by contract either way.
        var unloaded = UnloadedProjectsStore.CreateSet(unloadedRelativePaths);

        // Its own collection rather than ProjectCollection.GlobalProjectCollection. The global one
        // is process-wide mutable state: two loads at once, or a load racing anything else that
        // evaluates a project, mutate the same object. Scoping it to this call also removes the
        // per-project UnloadProject dance - disposing the collection releases everything in it.
        using var projectCollection = new Microsoft.Build.Evaluation.ProjectCollection();

        var solutionFile = Microsoft.Build.Construction.SolutionFile.Parse(solutionPath);
        foreach (var projectInSolution in solutionFile.ProjectsInOrder)
        {
            if (projectInSolution.ProjectType != Microsoft.Build.Construction.SolutionProjectType.KnownToBeMSBuildFormat)
            {
                continue;
            }

            var solutionRelativePath = UnloadedProjectsStore.NormalizeRelativePath(projectInSolution.RelativePath);

            if (unloaded.Contains(solutionRelativePath))
            {
                // The whole point: no Project.FromFile, so nothing in the project's SDK or workload is
                // imported and a project this machine cannot evaluate costs the tree nothing.
                root.Children.Add(new SolutionTreeNode(
                    projectInSolution.ProjectName, projectInSolution.AbsolutePath, SolutionTreeNodeKind.Project)
                {
                    IsUnloaded = true,
                    SolutionRelativePath = solutionRelativePath,
                });

                continue;
            }

            try
            {
                var projectNode = LoadProject(
                    projectInSolution.ProjectName,
                    projectInSolution.AbsolutePath,
                    solutionRelativePath,
                    projectCollection);
                root.Children.Add(projectNode);
            }
            catch (Exception ex) when (IsProjectLoadFailure(ex))
            {
                // One broken .csproj (invalid XML, a missing Import, an SDK this machine does not have)
                // used to fail the entire solution load. It is now a placeholder that says what is wrong.
                Serilog.Log.Warning(
                    ex, "Could not load project {Project} - showing it as failed.", projectInSolution.AbsolutePath);

                root.Children.Add(new SolutionTreeNode(
                    projectInSolution.ProjectName, projectInSolution.AbsolutePath, SolutionTreeNodeKind.Project)
                {
                    LoadError = DescribeLoadError(ex),
                    SolutionRelativePath = solutionRelativePath,
                });
            }
        }

        root.SortChildrenRecursively();
        return root;
    }

    private static bool IsProjectLoadFailure(Exception ex) =>
        ex is Microsoft.Build.Exceptions.InvalidProjectFileException
            or IOException
            or UnauthorizedAccessException
            or System.Xml.XmlException
            or NotSupportedException
            or ArgumentException;

    private static string DescribeLoadError(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

    private static SolutionTreeNode LoadProject(
        string projectName,
        string projectPath,
        string solutionRelativePath,
        Microsoft.Build.Evaluation.ProjectCollection projectCollection)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;

        var evaluated = Microsoft.Build.Evaluation.Project.FromFile(
            projectPath,
            new Microsoft.Build.Definition.ProjectOptions { ProjectCollection = projectCollection });

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

        var projectNode = new SolutionTreeNode(projectName, projectPath, SolutionTreeNodeKind.Project)
        {
            TargetFramework = windowsTargetFramework,
            IsTestProject = isTestProject,
            SolutionRelativePath = solutionRelativePath,
            RootNamespace = evaluated.GetPropertyValue("RootNamespace") is { Length: > 0 } rootNamespace
                ? rootNamespace
                : projectName,
        };

        var visibleItemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Compile", "None", "Content", "Page", "ApplicationDefinition", "EmbeddedResource",
        };

        foreach (var item in evaluated.Items)
        {
            if (item.ItemType.Equals("Folder", StringComparison.OrdinalIgnoreCase))
            {
                // An empty folder only exists in the project because ProjectFolderWriter recorded it as
                // <Folder Include="Assets\Icons\" />. Its include is a directory, not a file: adding it
                // as one would create a folder chain plus a File node named "".
                var folderParts = item.EvaluatedInclude
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

                if (folderParts.Length > 0)
                {
                    projectNode.GetOrCreateFolder(folderParts, projectDirectory);
                }

                continue;
            }

            if (!visibleItemTypes.Contains(item.ItemType))
            {
                continue;
            }

            var relativePath = item.EvaluatedInclude.Replace('/', Path.DirectorySeparatorChar);
            var parts = relativePath.Split(Path.DirectorySeparatorChar);
            var fileName = parts[^1];
            var folder = projectNode.GetOrCreateFolder(parts[..^1], projectDirectory);

            if (!folder.Children.Any(c => c.Kind == SolutionTreeNodeKind.File && c.Name == fileName))
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
                folder.Children.Add(new SolutionTreeNode(fileName, fullPath, SolutionTreeNodeKind.File));
            }
        }

        return projectNode;
    }
}
