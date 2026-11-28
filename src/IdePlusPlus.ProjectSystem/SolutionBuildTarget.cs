using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace IdePlusPlus.ProjectSystem;

/// <summary>
/// Decides what every solution-wide <c>dotnet</c> call should actually be pointed at: the solution
/// itself, or - when projects are unloaded - a generated <c>.slnf</c> solution filter listing only
/// the loaded ones.
///
/// <para>The filter lives under
/// <c>%LOCALAPPDATA%\IdePlusPlus\solution-filters\&lt;hash of the solution's full path&gt;\</c>, not
/// in the repository: a file written next to the solution would turn up in the user's
/// <c>git status</c> and be swept into an Ultra worktree commit by a broad <c>git add</c>. It is
/// generated for whichever solution path is passed in, so each Ultra worktree gets its own.</para>
///
/// <para>Every path through here is best-effort: a read-only or full filter directory, a locked
/// filter file or an unparseable solution degrades to "build the solution as before" with a warning
/// rather than taking the IDE down.</para>
/// </summary>
public static class SolutionBuildTarget
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The path a solution-wide dotnet call should use. Returns <paramref name="solutionPath"/>
    /// unchanged (the very same value) when nothing is unloaded, so behaviour without the feature is
    /// exactly what it was.
    /// </summary>
    /// <param name="solutionPath">The .sln/.slnx to build.</param>
    /// <param name="unloadedRelativePaths">Solution-relative paths of the unloaded projects - see
    /// <see cref="UnloadedProjectsStore.CreateSet"/>, whose comparer this honours.</param>
    public static string Resolve(string solutionPath, IReadOnlyCollection<string>? unloadedRelativePaths)
    {
        if (string.IsNullOrWhiteSpace(solutionPath) ||
            unloadedRelativePaths is null ||
            unloadedRelativePaths.Count == 0)
        {
            return solutionPath;
        }

        SolutionLoader.EnsureMSBuildRegistered();

        try
        {
            return ResolveCore(solutionPath, unloadedRelativePaths);
        }
        catch (Exception ex)
        {
            // M7: a locked/full/read-only filter directory must reach the user as a log line, not as an
            // unhandled exception on the UI thread. Building the whole solution is the pre-feature
            // behaviour, and the build itself will then fail loudly on the unloadable project.
            Log.Warning(ex, "Could not generate a solution filter for {Solution} - building the solution as before.", solutionPath);
            return solutionPath;
        }
    }

    /// <summary>The per-solution directory the filter is written to. Exposed so callers/tests can
    /// assert "outside the repository" and clean up.</summary>
    public static string FilterDirectoryFor(string solutionPath)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..16];

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdePlusPlus",
            "solution-filters",
            hash.ToLowerInvariant());
    }

    /// <summary>Where <see cref="Resolve"/> writes the filter for this solution.</summary>
    public static string FilterPathFor(string solutionPath) =>
        Path.Combine(FilterDirectoryFor(solutionPath), Path.GetFileNameWithoutExtension(solutionPath) + ".slnf");

    // Isolated in its own method (rather than inlined into Resolve) so its MSBuild type references are
    // only JIT-resolved once EnsureMSBuildRegistered has run - the same reason SolutionLoader.LoadCore
    // exists.
    private static string ResolveCore(string solutionPath, IReadOnlyCollection<string> unloadedRelativePaths)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        var unloaded = UnloadedProjectsStore.CreateSet(unloadedRelativePaths);
        var loadedProjects = GetLoadedProjectRelativePaths(fullPath, unloaded);

        if (loadedProjects.Count == 0)
        {
            // A filter with no projects is worse than none: dotnet fails it with an unhelpful NETSDK
            // error. Fall back to the whole solution and say why.
            Log.Warning(
                "Every project in {Solution} is unloaded - building the solution itself instead of an empty filter.",
                fullPath);
            return solutionPath;
        }

        var filterPath = FilterPathFor(fullPath);
        var filterDirectory = Path.GetDirectoryName(filterPath)!;
        Directory.CreateDirectory(filterDirectory);

        var filter = new SlnfDocument(new SlnfSolution(fullPath, loadedProjects));
        var json = JsonSerializer.Serialize(filter, SerializerOptions);

        // Temp-then-move: a build must never read a half-written filter, and the move replaces
        // yesterday's filter wholesale rather than appending to it.
        var temporaryPath = filterPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, filterPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Leave the leftover temp file rather than masking the real failure.
                }
            }
        }

        Log.Debug(
            "Solution filter for {Solution} written to {FilterPath} with {ProjectCount} project(s).",
            fullPath, filterPath, loadedProjects.Count);

        return filterPath;
    }

    private static List<string> GetLoadedProjectRelativePaths(string fullPath, IReadOnlySet<string> unloaded)
    {
        var solutionFile = Microsoft.Build.Construction.SolutionFile.Parse(fullPath);

        return solutionFile.ProjectsInOrder
            .Where(p => p.ProjectType == Microsoft.Build.Construction.SolutionProjectType.KnownToBeMSBuildFormat)
            .Select(p => UnloadedProjectsStore.NormalizeRelativePath(p.RelativePath))
            .Where(relativePath => !unloaded.Contains(relativePath))
            .ToList();
    }

    private sealed record SlnfDocument(SlnfSolution Solution);

    private sealed record SlnfSolution(string Path, IReadOnlyList<string> Projects);
}
