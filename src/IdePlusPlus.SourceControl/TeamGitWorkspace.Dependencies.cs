namespace IdePlusPlus.SourceControl;

/// <summary>
/// Dependency folders that git ignores. A worktree is a checkout, so it has none of them, yet a project
/// may build from them: FamWholesale.csproj lists "node_modules\bootstrap\scss\..." as files to copy to
/// the output, and fails with MSB3030 where no npm install has been run. Restoring them per worktree
/// would mean an npm install per worker, so they are linked to the user's own folders instead - see
/// <see cref="TeamExternalProjects.CreateLink"/>.
/// </summary>
public sealed partial class TeamGitWorkspace
{
    /// <summary>Folders a package manager fills and a build reads but never writes. Deliberately short:
    /// bin and obj are build output and must stay per worktree, and anything else a build cannot do
    /// without is named in the failure report instead of guessed at.</summary>
    private static readonly string[] DependencyFolderNames = ["node_modules"];

    /// <summary>
    /// The git-ignored dependency folders in the user's working tree, as repository-relative paths with
    /// forward slashes (git's own form). Only a folder git ignores as a whole counts, which is what makes
    /// linking it safe: in a worktree it is ignored the same way, so a worker's `git add -A` never
    /// stages the link. Empty when there are none, or git cannot say.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindIgnoredDependencyFoldersAsync(
        string repoRoot, CancellationToken cancellationToken = default)
    {
        // --directory reports an ignored folder as one entry instead of listing what is inside it, which
        // is what keeps this quick next to a node_modules of ten thousand files. -z so a path is exact.
        var result = await GitProcess.RunAsync(
            repoRoot, ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z"], cancellationToken);
        if (!result.Success)
        {
            return [];
        }

        return result.StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.TrimEnd('/'))
            .Where(path => DependencyFolderNames.Contains(path[(path.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase))
            .Where(path => Directory.Exists(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether git ignores <paramref name="relativePath"/> in <paramref name="directory"/>, a
    /// repository or one of its worktrees. check-ignore exits 0 for ignored, 1 for not, and anything
    /// else for an error, which is read as not ignored: the caller is deciding whether it is safe.</summary>
    public async Task<bool> IsIgnoredAsync(string directory, string relativePath, CancellationToken cancellationToken = default)
    {
        var result = await GitProcess.RunAsync(
            directory, ["check-ignore", "-q", "--", relativePath.Replace('\\', '/')], cancellationToken);
        return result.ExitCode == 0;
    }
}
