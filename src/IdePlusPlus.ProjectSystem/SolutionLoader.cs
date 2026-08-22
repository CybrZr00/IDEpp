using Microsoft.Build.Locator;

namespace IdePlusPlus.ProjectSystem;

public static class SolutionLoader
{
    private static bool _msBuildRegistered;

    /// <summary>
    /// Must run before any code in the process touches a Microsoft.Build.* type — the CLR
    /// resolves a method's referenced assemblies when that method is first JIT'd, which for
    /// unrelated code can happen before this call if it isn't made early (e.g. as the first
    /// line of App.OnStartup). Safe to call more than once.
    /// </summary>
    public static void EnsureMSBuildRegistered()
    {
        if (_msBuildRegistered)
        {
            return;
        }

        MSBuildLocator.RegisterDefaults();
        _msBuildRegistered = true;
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
        var projectNode = new SolutionTreeNode(projectName, projectPath, SolutionTreeNodeKind.Project);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;

        var evaluated = Microsoft.Build.Evaluation.Project.FromFile(
            projectPath,
            new Microsoft.Build.Definition.ProjectOptions());

        try
        {
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
