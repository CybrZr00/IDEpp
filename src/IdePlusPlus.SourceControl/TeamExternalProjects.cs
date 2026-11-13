using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace IdePlusPlus.SourceControl;

/// <summary>A folder next to the repository that the solution builds from - a sibling checkout such as
/// "..\RRS.FamRetail". <see cref="Name"/> is the folder's name, <see cref="SourcePath"/> where it really is.</summary>
public sealed record TeamExternalFolder(string Name, string SourcePath);

/// <summary>What a solution reaches for outside its repository. <see cref="Siblings"/> can be linked in
/// next to a worktree; <see cref="Unreachable"/> are projects that sit somewhere a link cannot help
/// with (another drive, a file rather than a folder), listed so a failed build can say so.</summary>
public sealed record TeamExternalFolders(IReadOnlyList<TeamExternalFolder> Siblings, IReadOnlyList<string> Unreachable)
{
    public static TeamExternalFolders None { get; } = new([], []);
}

/// <summary>
/// Projects that live outside the repository. A solution is often not self-contained: FamRestApi.sln
/// lists "..\RRS.FamRetail\RRS.FamRetail.csproj", a separate checkout beside the repo. In the user's own
/// tree that resolves. In a team worktree, which sits at "...\ultra\&lt;run&gt;\w1", the same relative
/// path points at "...\ultra\&lt;run&gt;\RRS.FamRetail", which does not exist, so restore fails with
/// MSB3202 and no worker can ever merge.
///
/// This finds those folders (by reading the solution and following every ProjectReference from it) and
/// links them into the run folder, where the worktrees' relative paths expect them. They are linked,
/// not copied: a sibling can be gigabytes and is not under git, so there is nothing to check out, and
/// copying it per worker would multiply it by the team size. The consequence is that the team shares
/// those folders with the user: a worker's build writes their bin and obj, as the user's own builds
/// do, and nothing a worker changes there is ever merged. The run's preflight build goes first, so
/// they are already built and up to date by the time several workers build at once.
/// </summary>
public static partial class TeamExternalProjects
{
    /// <summary>A guard against a runaway reference cycle or a monorepo of thousands of projects.</summary>
    private const int MaxProjectsScanned = 5_000;

    /// <summary>
    /// The folders beside <paramref name="repositoryRoot"/> that the solution (and everything it
    /// references) builds from. Reads files only, and never throws: a solution or project that cannot be
    /// read is skipped, and the build itself will say what is wrong with it.
    /// </summary>
    public static TeamExternalFolders Find(string solutionPath, string repositoryRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var parent = Path.GetDirectoryName(root);
        if (parent is null)
        {
            return TeamExternalFolders.None;
        }

        var siblings = new Dictionary<string, TeamExternalFolder>(StringComparer.OrdinalIgnoreCase);
        var unreachable = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(SolutionProjects(solutionPath));

        while (pending.Count > 0 && scanned.Count < MaxProjectsScanned)
        {
            var project = pending.Dequeue();
            if (!scanned.Add(project) || !File.Exists(project))
            {
                continue;
            }

            if (!IsInside(project, root))
            {
                Classify(project, parent, siblings, unreachable);
            }

            foreach (var reference in ProjectReferences(project))
            {
                pending.Enqueue(reference);
            }
        }

        return siblings.Count == 0 && unreachable.Count == 0
            ? TeamExternalFolders.None
            : new TeamExternalFolders([.. siblings.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)], [.. unreachable]);
    }

    /// <summary>
    /// Makes each folder show up as <c>&lt;runDirectory&gt;\&lt;name&gt;</c>, a directory junction to the real
    /// one, so "..\&lt;name&gt;" from any worker's worktree resolves. A junction needs no administrator
    /// rights or developer mode, unlike a symbolic link. Returns the links created, for
    /// <see cref="Unlink"/>. Something already there (a worker's own folder, an earlier link) is left
    /// alone and not reported.
    /// </summary>
    public static IReadOnlyList<string> Link(string runDirectory, IEnumerable<TeamExternalFolder> folders)
    {
        var created = new List<string>();
        try
        {
            foreach (var folder in folders)
            {
                var link = Path.Combine(runDirectory, folder.Name);
                if (Path.Exists(link))
                {
                    continue;
                }

                CreateLink(link, folder.SourcePath);
                created.Add(link);
            }
        }
        catch
        {
            Unlink(created);
            throw;
        }

        return created;
    }

    /// <summary>
    /// Removes links made by <see cref="Link"/>. Only ever the link: a path that is not a junction or
    /// symbolic link is left alone, and removing a link does not touch what it points at. Best effort -
    /// a link that will not go is harmless, as it is only a pointer.
    /// </summary>
    public static void Unlink(IEnumerable<string> links)
    {
        foreach (var link in links)
        {
            try
            {
                if (Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Not recursive: that would be a request to delete the contents, and for a link it
                    // removes the link and nothing else.
                    Directory.Delete(link, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Classify(
        string project, string parent, Dictionary<string, TeamExternalFolder> siblings, SortedSet<string> unreachable)
    {
        var relative = Path.GetRelativePath(parent, project);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            // Another drive, or higher than the folder beside the repository: "..\..\Shared\x.csproj".
            unreachable.Add(project);
            return;
        }

        var name = relative.Split(Path.DirectorySeparatorChar, 2)[0];
        var folder = Path.Combine(parent, name);
        if (Directory.Exists(folder))
        {
            siblings.TryAdd(name, new TeamExternalFolder(name, folder));
        }
        else
        {
            unreachable.Add(project);
        }
    }

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The solution's project files, as full paths. .slnx (XML) and .sln (text) are read the same
    /// way; a path that is itself a project is the whole list.</summary>
    private static IEnumerable<string> SolutionProjects(string solutionPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        if (directory is null)
        {
            return [];
        }

        try
        {
            IEnumerable<string> raw;
            switch (Path.GetExtension(solutionPath).ToLowerInvariant())
            {
                case ".slnx":
                    raw = XDocument.Load(solutionPath).Descendants()
                        .Where(e => e.Name.LocalName == "Project")
                        .Select(e => (string?)e.Attribute("Path"))
                        .OfType<string>();
                    break;
                case ".sln":
                    raw = File.ReadLines(solutionPath)
                        .Select(line => SolutionProjectLine().Match(line))
                        .Where(m => m.Success)
                        .Select(m => m.Groups["path"].Value);
                    break;
                default:
                    return Path.GetExtension(solutionPath).EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                        ? [Path.GetFullPath(solutionPath)]
                        : [];
            }

            // A solution folder is listed like a project, with its own name as the path.
            return raw
                .Where(p => Path.GetExtension(p).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                .Select(p => Resolve(directory, p))
                .OfType<string>()
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ProjectReferences(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath)!;
        try
        {
            return XDocument.Load(projectPath).Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .OfType<string>()
                .Select(include => include
                    .Replace("$(MSBuildThisFileDirectory)", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    .Replace("$(MSBuildProjectDirectory)", directory, StringComparison.OrdinalIgnoreCase))
                // Anything still containing a property or wildcard cannot be resolved without MSBuild.
                .Where(include => !include.Contains("$(") && !include.Contains("@(") && !include.Contains('*'))
                .Select(include => Resolve(directory, include))
                .OfType<string>()
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }
    }

    private static string? Resolve(string directory, string path)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(directory, path.Replace('\\', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes <paramref name="link"/> a directory junction to the existing folder
    /// <paramref name="target"/>. Throws InvalidOperationException when it cannot. Undo it with
    /// <see cref="Unlink"/> and never with a recursive delete, which would follow the junction.
    /// </summary>
    public static void CreateLink(string link, string target)
    {
        // cmd expands %VAR% even inside quotes, and a quote ends the argument; neither belongs in a
        // folder name worth linking, so refuse rather than build a command line that means something else.
        if (link.Contains('%') || target.Contains('%') || link.Contains('"') || target.Contains('"'))
        {
            throw new InvalidOperationException($"Can't link {target}: the path contains a % or \" character.");
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start cmd.exe to link a folder.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not link {target} into the team's folder as {link}: {(error.Result + output.Result).Trim()}");
        }
    }

    // Project("{type-guid}") = "Name", "relative\path.csproj", "{project-guid}"
    [GeneratedRegex(@"^\s*Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""")]
    private static partial Regex SolutionProjectLine();
}
