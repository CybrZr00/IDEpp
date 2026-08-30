namespace IdePlusPlus.Tasks;

/// <summary>
/// Finds the package.json files whose scripts should appear in Task Explorer.
///
/// Two sources, because neither alone is enough. The solution tree already contains a package.json
/// that sits inside an SDK-style project directory (the SDK's default None/Content globs sweep it
/// in), but not one at the repository root beside the .sln - and the root is where a web repo
/// usually keeps it. So: a shallow scan of the solution directory, unioned with package.json file
/// nodes the caller pulls out of the loaded tree.
///
/// Everything under a node_modules segment is excluded, from both sources. Those SDK globs do not
/// exclude node_modules, so a project with dependencies installed contributes hundreds of nested
/// package.json files to the tree - every one of them a dependency's own manifest, none of them the
/// user's tasks.
/// </summary>
public static class TaskManifestDiscovery
{
    public const string PackageJsonFileName = "package.json";

    private const string NodeModulesDirectoryName = "node_modules";

    /// <summary>Deep enough for a repo-root manifest and a one-level-down one (src/web,
    /// ClientApp, …). Anything deeper sits inside a project directory, which the tree covers.</summary>
    private const int SolutionScanDepth = 2;

    private static readonly string[] SkippedDirectoryNames =
        [NodeModulesDirectoryName, "bin", "obj", ".git", ".vs", ".ideplusplus"];

    /// <summary>
    /// Never throws: a directory that cannot be enumerated (permissions, a path that has gone away
    /// mid-scan) contributes nothing, and a manifest that cannot be read or parsed is skipped -
    /// discovery runs unattended on solution load, where an exception would surface as a solution
    /// that failed to open.
    /// </summary>
    public static IReadOnlyList<TaskManifest> Discover(string? solutionDirectory, IEnumerable<string> solutionFilePaths)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in EnumerateSolutionDirectory(solutionDirectory))
        {
            AddCandidate(candidates, path);
        }

        foreach (var path in solutionFilePaths)
        {
            if (PackageJsonFileName.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(candidates, path);
            }
        }

        var manifests = new List<TaskManifest>();
        foreach (var path in candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (ReadManifest(path) is { } manifest)
            {
                manifests.Add(manifest);
            }
        }

        return manifests;
    }

    public static bool IsUnderNodeModules(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(NodeModulesDirectoryName, StringComparison.OrdinalIgnoreCase));

    private static void AddCandidate(HashSet<string> candidates, string path)
    {
        if (IsUnderNodeModules(path))
        {
            return;
        }

        try
        {
            // Normalized so the same manifest reached from the scan and from the tree - which
            // holds paths built by SolutionLoader from MSBuild's relative includes - dedupes.
            candidates.Add(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a usable path; nothing to add.
        }
    }

    /// <summary>A manifest with no scripts is not listed at all - an empty node for every
    /// package.json in a repo is noise, not information.</summary>
    private static TaskManifest? ReadManifest(string path)
    {
        if (PackageJsonReader.Read(path) is not { Scripts.Count: > 0 } contents)
        {
            return null;
        }

        return new TaskManifest(
            TaskManifestKind.NpmScripts,
            path,
            PackageManagerDetector.Detect(path, contents.PackageManagerField),
            contents.Scripts);
    }

    private static IEnumerable<string> EnumerateSolutionDirectory(string? solutionDirectory)
    {
        if (string.IsNullOrEmpty(solutionDirectory) || !Directory.Exists(solutionDirectory))
        {
            yield break;
        }

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((solutionDirectory, 1));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            var manifestPath = Path.Combine(directory, PackageJsonFileName);
            if (File.Exists(manifestPath))
            {
                yield return manifestPath;
            }

            if (depth >= SolutionScanDepth)
            {
                continue;
            }

            foreach (var subdirectory in EnumerateSubdirectories(directory))
            {
                if (!SkippedDirectoryNames.Contains(Path.GetFileName(subdirectory), StringComparer.OrdinalIgnoreCase))
                {
                    queue.Enqueue((subdirectory, depth + 1));
                }
            }
        }
    }

    private static IReadOnlyList<string> EnumerateSubdirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
