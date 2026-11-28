using System.Text.Json;
using Serilog;

namespace IdePlusPlus.ProjectSystem;

/// <summary>
/// What <see cref="UnloadedProjectsStore"/> persisted for one solution: the projects the user
/// unloaded from the tree/build, and whether those projects are still shown in Solution Explorer.
/// </summary>
/// <param name="Projects">Solution-relative project paths, as written by the user's unload actions.</param>
/// <param name="ShowUnloaded">False when the user hid unloaded projects with the pane's toggle.</param>
public sealed record UnloadedProjectsState(IReadOnlyList<string> Projects, bool ShowUnloaded)
{
    /// <summary>Nothing unloaded, unloaded projects visible - what a solution that never used the feature has.</summary>
    public static UnloadedProjectsState Empty { get; } = new([], ShowUnloaded: true);
}

/// <summary>
/// Persists, per solution, which projects are "unloaded" - the answer to a solution containing a
/// project whose SDK/workload cannot be evaluated on this machine (a MAUI Android project is the
/// motivating case), where the whole solution would otherwise be unusable.
///
/// <para>Stored at .ideplusplus/unloaded-projects.json (same convention as
/// <see cref="StartupProjectsStore"/>) with <b>solution-relative</b> paths, so the same list works
/// inside an Ultra git worktree: the worktree's copy of the file names the same relative projects as
/// the user's checkout.</para>
///
/// <para>Everything here is best-effort and must never throw at the caller: a corrupt or
/// half-written file is a reason to start with nothing unloaded, not a reason for the solution not to
/// open.</para>
/// </summary>
public static class UnloadedProjectsStore
{
    public const string FileName = "unloaded-projects.json";

    /// <summary>Separator-normalised, case-insensitive comparer over solution-relative paths - the one
    /// rule every caller (the loader, the build target, the pane's filter) has to agree on.</summary>
    public static IEqualityComparer<string> PathComparer { get; } = new SolutionRelativePathComparer();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string FilePath(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", FileName);

    /// <summary>
    /// Reads the solution's unloaded list. A missing file is the normal "nothing unloaded" case; a
    /// corrupt one (truncated write, hand-edit, a JSON array from some other tool) logs a warning and
    /// also yields the empty state. Never throws.
    /// </summary>
    public static UnloadedProjectsState Load(string solutionDirectory)
    {
        if (string.IsNullOrWhiteSpace(solutionDirectory))
        {
            return UnloadedProjectsState.Empty;
        }

        var path = FilePath(solutionDirectory);
        try
        {
            if (!File.Exists(path))
            {
                // Not a warning: a solution that has never unloaded anything simply has no file, and
                // logging that on every solution open would bury the real warnings.
                Log.Debug("No unloaded-projects file at {Path} - nothing is unloaded.", path);
                return UnloadedProjectsState.Empty;
            }

            var stored = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), SerializerOptions);
            var projects = (stored?.UnloadedProjects ?? [])
                .Where(IsValidRelativePath)
                .Select(p => NormalizeRelativePath(p!))
                .ToList();

            return new UnloadedProjectsState(projects, stored?.ShowUnloaded ?? true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Corrupt, half-written or unreadable: the solution still opens, with nothing unloaded.
            Log.Warning(ex, "Could not read unloaded projects from {Path} - treating nothing as unloaded.", path);
            return UnloadedProjectsState.Empty;
        }
    }

    /// <summary>Writes the solution's unloaded list. Only ever writes under
    /// <c>&lt;solution&gt;\.ideplusplus\</c>, whatever paths are passed in (entries that are not
    /// solution-relative, or that escape the solution directory, are dropped).</summary>
    public static void Save(string solutionDirectory, IReadOnlyList<string> projects, bool showUnloaded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionDirectory);

        var path = FilePath(solutionDirectory);
        var stored = new StoredFile(
            (projects ?? [])
                .Where(IsValidRelativePath)
                .Select(p => NormalizeRelativePath(p!))
                .ToList(),
            showUnloaded);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(stored, SerializerOptions));
    }

    public static void Save(string solutionDirectory, UnloadedProjectsState state) =>
        Save(solutionDirectory, state.Projects, state.ShowUnloaded);

    /// <summary>
    /// The set form every consumer wants: a case- and separator-insensitive set of solution-relative
    /// paths. Entries that are rooted, UNC or that step outside the solution directory ('..') are
    /// dropped rather than kept as never-matching noise.
    /// </summary>
    public static IReadOnlySet<string> CreateSet(IEnumerable<string>? relativePaths) =>
        new HashSet<string>(
            (relativePaths ?? []).Where(IsValidRelativePath).Select(p => NormalizeRelativePath(p!)),
            PathComparer);

    /// <summary>'src/App/App.csproj' and 'src\app\APP.csproj' normalise to the same string.</summary>
    public static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('/', '\\').Trim();

        while (normalized.StartsWith(@".\", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('\\');
    }

    /// <summary>True for a path that names something inside the solution directory and nothing else -
    /// no rooted/UNC paths, no '..' segments.</summary>
    public static bool IsValidRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Path.IsPathRooted(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');

        // Path.IsPathRooted misses a drive-relative 'C:foo' and we do not want that either.
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            return false;
        }

        return !normalized.Split('\\').Any(segment => segment.Trim() == "..");
    }

    /// <summary>
    /// Projects a node's absolute path back onto the solution's relative form for store/compare -
    /// null when the path is outside the solution directory (which must never match a stored entry).
    /// </summary>
    public static string? TryGetSolutionRelativePath(string solutionDirectory, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(solutionDirectory) || string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(solutionDirectory);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }

            var fullPath = Path.GetFullPath(projectPath);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var relative = NormalizeRelativePath(fullPath[root.Length..]);
            return IsValidRelativePath(relative) ? relative : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    private sealed record StoredFile(List<string>? UnloadedProjects, bool? ShowUnloaded);

    private sealed class SolutionRelativePathComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) =>
            string.Equals(
                x is null ? null : NormalizeRelativePath(x),
                y is null ? null : NormalizeRelativePath(y),
                StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(NormalizeRelativePath(obj));
    }
}
