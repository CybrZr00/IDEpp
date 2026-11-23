using System.IO;
using IdePlusPlus.SourceControl;

namespace IdePlusPlus.Shell.Team.Pipeline;

/// <summary>A checkout the run made: where it is, the branch it works on (null for a detached one that
/// follows a commit rather than a branch), and the name its commits carry.</summary>
public sealed record WorkspaceWorktree(string Name, string Path, string? Branch, string Author);

/// <summary>What a finished run leaves: what the team branch changed against the user's snapshot, and
/// whether the branch is kept (it is deleted when it changed nothing).</summary>
public sealed record WorkspaceOutcome(IReadOnlyList<TeamFileChange> Changes, bool TeamBranchKept);

/// <summary>
/// The git side of a pipeline run - the same jobs <see cref="UltraTeamRun"/> does inline, in a class of
/// their own so the pipeline's stages can add and remove worktrees as they go instead of creating the
/// whole team up front and tearing it down once. (The team run is not switched over to this; a copy that
/// the pipeline owns is safe to change, and the 800 lines of tests around the original keep covering it.)
///
/// What it does, in the order a run needs it: snapshot the user's working tree without disturbing it,
/// optionally merge an earlier run's branch in, refuse early if the disk cannot hold the worktrees,
/// create the team branch, link the folders a build needs but a checkout does not have (sibling projects,
/// node_modules); hand out worktrees on request - one branch per agent, plus detached ones for the shared
/// read-only checkout and the gate; and at the end keep whatever work never reached the team branch,
/// remove every worktree and link, and report what the team branch changed.
///
/// Folder names under the run folder: each agent's worktree is its name (b1, d1), and the folders this
/// class reserves - <c>_ro</c>, <c>_gate</c>, <c>_phases</c> - start with an underscore, which no
/// agent name can, and which the junctions to sibling projects (named after real folders) are very
/// unlikely to.
/// </summary>
public sealed class RunWorkspace
{
    public const string PhasesFolderName = "_phases";
    public const string ReadOnlyFolderName = "_ro";
    public const string GateFolderName = "_gate";

    public const string UnfinishedWorkCommitMessage =
        "Uncommitted work when the Ultra run ended - kept so it is not lost; unverified";

    /// <summary>Kept free beyond what the worktrees need, for everything else on the machine.</summary>
    public const long DiskHeadroomBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>A worktree's size with build output, as a multiple of its checkout, until one is
    /// measured: generous, since running out of disk mid-run is worse than refusing at the start.</summary>
    public const int BuildOutputFactor = 4;

    private readonly TeamGitWorkspace _teamGit;
    private readonly Func<string, long> _freeDiskSpace;
    private readonly Action<string>? _progress;

    // One git worktree operation at a time: add and remove both write the repository's own bookkeeping.
    private readonly SemaphoreSlim _git = new(1, 1);
    private readonly object _lock = new();
    private readonly List<WorkspaceWorktree> _worktrees = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _dependencyLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notMirrored = [];
    private IReadOnlyList<string> _externalLinks = [];
    private long _checkoutBytes;
    private long _worktreeBytes;
    private bool _opened;
    private bool _closed;

    /// <param name="ultraRootDirectory">Where run folders go - outside any repository, so worktrees never
    /// show up in the user's status, file list or index.</param>
    /// <param name="freeDiskSpace">Free bytes on the drive holding a path; the real drive unless a test
    /// says otherwise.</param>
    /// <param name="progress">Told what setup is doing, for the status card.</param>
    public RunWorkspace(
        TeamGitWorkspace teamGit,
        string ultraRootDirectory,
        string runId,
        string repositoryRoot,
        string solutionPath,
        Func<string, long>? freeDiskSpace = null,
        Action<string>? progress = null)
    {
        _teamGit = teamGit;
        _freeDiskSpace = freeDiskSpace ?? DriveFreeSpace;
        _progress = progress;
        RunId = runId;
        RepositoryRoot = repositoryRoot;
        SolutionPath = solutionPath;
        TeamBranch = TeamGitWorkspace.GetTeamBranchName(runId);
        RunDirectory = Path.Combine(ultraRootDirectory, runId);
        PhasesDirectory = Path.Combine(RunDirectory, PhasesFolderName);
        ReadOnlyDirectory = Path.Combine(RunDirectory, ReadOnlyFolderName);
        GateDirectory = Path.Combine(RunDirectory, GateFolderName);
        SolutionRelativeDirectory = Path.GetRelativePath(repositoryRoot, Path.GetDirectoryName(solutionPath)!);
        Directory.CreateDirectory(RunDirectory);
    }

    public string RunId { get; }

    public string RepositoryRoot { get; }

    public string SolutionPath { get; }

    public string TeamBranch { get; }

    public string RunDirectory { get; }

    /// <summary>Where the run's records go. Not a worktree and not in any: nothing an agent edits can
    /// land here, and git never sees it.</summary>
    public string PhasesDirectory { get; }

    public string ReadOnlyDirectory { get; }

    public string GateDirectory { get; }

    /// <summary>The solution's folder relative to the repository root ("" when they are the same).</summary>
    public string SolutionRelativeDirectory { get; }

    /// <summary>The user's working tree as it was when the run started - what the result is compared
    /// against, so a run that carried on from an earlier branch offers that work for review too.</summary>
    public string Snapshot { get; private set; } = "";

    /// <summary>The commit every worktree starts from: the snapshot, or the snapshot with
    /// <see cref="StartedFrom"/> merged in.</summary>
    public string Start { get; private set; } = "";

    public string? StartedFrom { get; private set; }

    /// <summary>Folders beside the repository the solution builds from, linked into the run folder.</summary>
    public TeamExternalFolders ExternalFolders { get; private set; } = TeamExternalFolders.None;

    /// <summary>Git-ignored dependency folders (node_modules) linked into each worktree.</summary>
    public IReadOnlyList<string> IgnoredDependencyFolders { get; private set; } = [];

    /// <summary>What the team's copy lacks that it could not be given, for a failure message.</summary>
    public IReadOnlyList<string> NotMirrored
    {
        get
        {
            lock (_lock)
            {
                return [.. _notMirrored];
            }
        }
    }

    public IReadOnlyList<WorkspaceWorktree> Worktrees
    {
        get
        {
            lock (_lock)
            {
                return [.. _worktrees];
            }
        }
    }

    /// <summary>What one worktree takes on disk: its checkout, until a build in one measures the rest.</summary>
    public long WorktreeBytes => Volatile.Read(ref _worktreeBytes);

    public long CheckoutBytes => Volatile.Read(ref _checkoutBytes);

    /// <summary>
    /// Everything before the first agent starts. Snapshots the working tree (the user's index, HEAD and
    /// branches are never touched), merges <paramref name="startFrom"/> into it if given, checks the disk
    /// for <paramref name="plannedWorktrees"/>, then creates the team branch and the links. Refused - before
    /// anything exists to clean up - when <paramref name="startFrom"/> changes the same lines as the
    /// working tree, or the disk is short.
    /// </summary>
    /// <exception cref="InvalidOperationException">Starting from that branch conflicts, or git failed.</exception>
    /// <exception cref="UltraTeamDiskSpaceException">The worktrees would not fit.</exception>
    public async Task OpenAsync(string? startFrom, int plannedWorktrees, CancellationToken cancellationToken)
    {
        if (_opened)
        {
            throw new InvalidOperationException("The workspace is already open.");
        }

        _progress?.Invoke("Creating workspace - taking a snapshot of your working tree (a large repository can take a few minutes)");
        Snapshot = await _teamGit.CreateSnapshotCommitAsync(RepositoryRoot, cancellationToken);
        Start = Snapshot;

        _progress?.Invoke("Creating workspace - preparing the team's branch and the folders its worktrees will share");
        if (!string.IsNullOrWhiteSpace(startFrom))
        {
            startFrom = startFrom.Trim();
            var merge = await _teamGit.MergeIntoSnapshotAsync(RepositoryRoot, Snapshot, startFrom, cancellationToken);
            if (merge.Status == TeamStartMergeStatus.Conflicted)
            {
                throw new InvalidOperationException(
                    $"Can't start from {startFrom}: it changes the same lines as your working tree in "
                    + $"{string.Join(", ", merge.ConflictedPaths)}. Commit, stash or undo those edits - or apply "
                    + "that branch first - and send again.");
            }

            Start = merge.Commit!;
            StartedFrom = startFrom;
        }

        // Folders beside the repository that the solution builds from. Worktrees live in the run folder,
        // where "..\Sibling" points at nothing, so they are linked in below.
        ExternalFolders = TeamExternalProjects.Find(SolutionPath, RepositoryRoot);

        // Dependency folders git ignores (node_modules) are not in a checkout either, and a project can
        // build from them: each worktree gets a link to the user's own.
        IgnoredDependencyFolders = await _teamGit.FindIgnoredDependencyFoldersAsync(RepositoryRoot, cancellationToken);

        // Before anything exists: a run whose worktrees would not fit is refused while there is nothing to
        // clean up. A guess - the preflight build measures a real worktree.
        Volatile.Write(ref _checkoutBytes, await _teamGit.GetTreeSizeAsync(RepositoryRoot, Start, cancellationToken));
        Volatile.Write(ref _worktreeBytes, CheckoutBytes * BuildOutputFactor);
        EnsureDiskSpace(plannedWorktrees, WorktreeBytes, "the pipeline's worktrees");

        EnsureSucceeded(await _teamGit.CreateBranchAsync(RepositoryRoot, TeamBranch, Start, cancellationToken), "create the team branch");
        _opened = true;
        Directory.CreateDirectory(PhasesDirectory);

        // Before the first worktree is built in. A link that cannot be made does not stop the run here: the
        // preflight build then fails the way it would have, and says which folders the copy was missing.
        lock (_lock)
        {
            _notMirrored.AddRange(ExternalFolders.Unreachable);
        }

        try
        {
            _externalLinks = TeamExternalProjects.Link(RunDirectory, ExternalFolders.Siblings);
        }
        catch (InvalidOperationException ex)
        {
            lock (_lock)
            {
                _notMirrored.AddRange(ExternalFolders.Siblings.Select(f => $"{f.SourcePath} ({ex.Message})"));
            }
        }
    }

    /// <summary>
    /// A checkout on its own branch (<c>ideplusplus/ultra/&lt;run&gt;/&lt;name&gt;</c>) at
    /// <paramref name="startCommit"/>, with the user's dependency folders linked in. For the agents that
    /// edit: the designer, the owners, a judge that writes tests.
    /// </summary>
    /// <exception cref="ArgumentException">Not a valid agent name (see <see cref="TeamGitWorkspace.IsValidAgentName"/>).</exception>
    public async Task<WorkspaceWorktree> AddWorktreeAsync(string name, string startCommit, CancellationToken cancellationToken)
    {
        var branch = TeamGitWorkspace.GetAgentBranchName(RunId, name);
        var worktree = new WorkspaceWorktree(name, Path.Combine(RunDirectory, name), branch, $"IDE++ Ultra {name}");
        await CreateAsync(
            worktree,
            () => _teamGit.AddWorktreeAsync(RepositoryRoot, worktree.Path, branch, startCommit, cancellationToken),
            linkDependencies: true,
            cancellationToken);
        return worktree;
    }

    /// <summary>
    /// A checkout with no branch, at <paramref name="commit"/>: the shared read-only one the surveyors and
    /// reviewers read (<see cref="ReadOnlyFolderName"/>), and the throwaway one the gate builds and formats
    /// in (<see cref="GateFolderName"/>). Nothing is ever committed in one, so there is nothing to keep.
    /// </summary>
    /// <exception cref="ArgumentException">Not one of the reserved folder names.</exception>
    public async Task<WorkspaceWorktree> AddDetachedWorktreeAsync(
        string name, string commit, bool linkDependencies, CancellationToken cancellationToken)
    {
        if (name is not (ReadOnlyFolderName or GateFolderName))
        {
            throw new ArgumentException(
                $"'{name}' is not a detached checkout: those are {ReadOnlyFolderName} and {GateFolderName}.", nameof(name));
        }

        var worktree = new WorkspaceWorktree(name, Path.Combine(RunDirectory, name), null, $"IDE++ Ultra {name.TrimStart('_')}");
        await CreateAsync(
            worktree,
            () => _teamGit.AddDetachedWorktreeAsync(RepositoryRoot, worktree.Path, commit, cancellationToken),
            linkDependencies,
            cancellationToken);
        return worktree;
    }

    /// <summary>Moves a detached checkout to <paramref name="commit"/>, discarding whatever it had
    /// changed - the shared read-only one at each stage boundary, the gate's after a run that rewrote files.</summary>
    public async Task MoveDetachedAsync(WorkspaceWorktree worktree, string commit, CancellationToken cancellationToken)
    {
        if (worktree.Branch is not null)
        {
            throw new ArgumentException($"{worktree.Name} has a branch; only a detached checkout can be moved.", nameof(worktree));
        }

        await _git.WaitAsync(cancellationToken);
        try
        {
            EnsureSucceeded(await _teamGit.CheckoutDetachedAsync(worktree.Path, commit, cancellationToken), $"move {worktree.Name} to {commit}");
        }
        finally
        {
            _git.Release();
        }
    }

    /// <summary>
    /// A path as the design spells it - relative to the solution folder, forward slashes - as git spells it:
    /// relative to the repository root. They are the same when the solution is at the root.
    /// </summary>
    public string ToRepositoryPath(string solutionRelativePath)
    {
        var path = solutionRelativePath.Replace('\\', '/').TrimStart('/');
        var directory = SolutionRelativeDirectory.Replace('\\', '/').Trim('/');
        return directory is "" or "." ? path : $"{directory}/{path}";
    }

    /// <summary>
    /// A repository-relative path as the design spells files - relative to the solution folder with forward
    /// slashes - or null for one outside the solution folder, which no design file can name.
    /// </summary>
    public string? ToSolutionPath(string repositoryRelativePath)
    {
        var path = repositoryRelativePath.Replace('\\', '/').TrimStart('/');
        var directory = SolutionRelativeDirectory.Replace('\\', '/').Trim('/');
        if (directory is "" or ".")
        {
            return path;
        }

        var prefix = directory + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;
    }

    /// <summary>The commit the team branch is at now - what the owners start from, and what a stage's
    /// read-only checkout is moved to.</summary>
    /// <exception cref="InvalidOperationException">The branch is missing.</exception>
    public async Task<string> TeamHeadAsync(CancellationToken cancellationToken) =>
        await _teamGit.ResolveRefAsync(RepositoryRoot, $"refs/heads/{TeamBranch}", cancellationToken)
        ?? throw new InvalidOperationException($"The team branch {TeamBranch} is missing.");

    /// <summary>
    /// Puts <paramref name="commit"/> on the team branch: moves the branch forward to it, which is only
    /// possible when the commit descends from where the branch is now. For the designer's contract commit,
    /// which the runner lands once it has checked it - an agent that could team_merge it would have put it
    /// there unchecked.
    /// </summary>
    /// <exception cref="InvalidOperationException">The branch is missing or the commit does not descend from it.</exception>
    public async Task LandAsync(string commit, CancellationToken cancellationToken)
    {
        var teamHead = await TeamHeadAsync(cancellationToken);
        var result = await _teamGit.FastForwardBranchAsync(RepositoryRoot, TeamBranch, commit, teamHead, cancellationToken);
        if (result.Status != TeamFastForwardStatus.FastForwarded)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    /// <summary>The context a dispatcher needs for an agent working in <paramref name="worktree"/>.</summary>
    public TeamWorkerContext ContextFor(WorkspaceWorktree worktree, IReadOnlyList<string> testProjectPaths)
    {
        var solutionDirectory = Path.GetFullPath(Path.Combine(worktree.Path, SolutionRelativeDirectory));
        return new TeamWorkerContext(
            worktree.Name,
            RepositoryRoot,
            worktree.Path,
            solutionDirectory,
            Path.Combine(solutionDirectory, Path.GetFileName(SolutionPath)),
            [.. testProjectPaths.Select(p => Path.Combine(worktree.Path, Path.GetRelativePath(RepositoryRoot, p)))],
            TeamBranch,
            worktree.Author);
    }

    /// <summary>Refuses to go on when <paramref name="worktrees"/> more of <paramref name="bytesEach"/>
    /// would leave less than the headroom free. Called with the roster once it is known, as owners and
    /// test-writing judges are added.</summary>
    /// <exception cref="UltraTeamDiskSpaceException"/>
    public void EnsureDiskSpace(int worktrees, long bytesEach, string what)
    {
        if (DiskShortfall(worktrees, bytesEach) is { } shortfall)
        {
            throw new UltraTeamDiskSpaceException(
                $"There isn't room on the disk for {what}: {shortfall}. Use fewer workers, or free some space.");
        }
    }

    /// <summary>After a build in <paramref name="worktreePath"/>: its real size replaces the guess, and
    /// every other worktree is expected to grow to it. Returns what each of the others will add.</summary>
    public long RememberBuiltWorktree(string worktreePath)
    {
        Volatile.Write(ref _worktreeBytes, Math.Max(CheckoutBytes, DirectorySize(worktreePath)));
        return Math.Max(0, WorktreeBytes - CheckoutBytes);
    }

    /// <summary>What a failed preflight build says the copy lacks: what could not be linked in, and
    /// whatever the report names that is in the user's tree but not the copy - the git-ignored folders
    /// (generated sources, local config) a build needs and a checkout never has.</summary>
    public IReadOnlyList<string> NotInTheCopy(string report, string firstWorktree)
    {
        var known = NotMirrored.ToList();
        foreach (var missing in TeamBuildReport.FindMissingFromCopy(report, firstWorktree, RepositoryRoot))
        {
            known.Add($"{missing} (in your tree, but not in the team's copy - git ignores it)");
        }

        return known;
    }

    /// <summary>
    /// The end of the run: keeps whatever work never reached the team branch (committing what was left
    /// uncommitted first, marked unverified), removes every worktree and then every link. Returns the
    /// branches that were kept. Safe to call twice, and meant for a cancelled run too - which is why it
    /// takes no token: a cancelled run's worktrees most need removing, and a stray one keeps its branch
    /// checked out until someone prunes it.
    /// </summary>
    public async Task<IReadOnlyList<string>> CloseAsync()
    {
        List<WorkspaceWorktree> created;
        lock (_lock)
        {
            if (_closed)
            {
                return [];
            }

            _closed = true;
            created = [.. _worktrees];
        }

        var kept = new List<string>();
        foreach (var worktree in created)
        {
            // First of all: git removes a worktree by deleting what is in it, and a link to the user's
            // node_modules is something it could follow into the real folder.
            UnlinkDependencies(worktree.Path);
            var keep = worktree.Branch is not null && await PreserveUnmergedWorkAsync(worktree);
            await _git.WaitAsync(CancellationToken.None);
            try
            {
                await _teamGit.RemoveWorktreeAsync(RepositoryRoot, worktree.Path, CancellationToken.None);
                if (worktree.Branch is not null)
                {
                    if (keep)
                    {
                        kept.Add(worktree.Branch);
                    }
                    else
                    {
                        await _teamGit.DeleteBranchAsync(RepositoryRoot, worktree.Branch, CancellationToken.None);
                    }
                }
            }
            finally
            {
                _git.Release();
            }
        }

        // After the worktrees: nothing builds against the links once those are gone. Only the links go -
        // the folders they point at are the user's.
        TeamExternalProjects.Unlink(_externalLinks);
        return kept;
    }

    /// <summary>What the team branch changed against the user's snapshot. The branch is deleted when it
    /// changed nothing, and kept when it did - it is what the result is reviewed and landed from.</summary>
    public async Task<WorkspaceOutcome> FinishAsync(CancellationToken cancellationToken = default)
    {
        var changes = await _teamGit.GetChangedFilesAsync(RepositoryRoot, Snapshot, TeamBranch, cancellationToken);
        var keep = changes.Count > 0;
        if (!keep)
        {
            await _teamGit.DeleteBranchAsync(RepositoryRoot, TeamBranch, CancellationToken.None);
        }

        return new WorkspaceOutcome(changes, keep);
    }

    /// <summary>For a run that failed before it did anything (the build was broken, the disk filled): the
    /// team branch only marked where it would have started, so it goes.</summary>
    public async Task DiscardTeamBranchAsync()
    {
        if (_opened)
        {
            await _teamGit.DeleteBranchAsync(RepositoryRoot, TeamBranch, CancellationToken.None);
        }
    }

    private async Task CreateAsync(
        WorkspaceWorktree worktree,
        Func<Task<GitCommandResult>> add,
        bool linkDependencies,
        CancellationToken cancellationToken)
    {
        if (!_opened)
        {
            throw new InvalidOperationException("Open the workspace before adding worktrees.");
        }

        lock (_lock)
        {
            if (_closed)
            {
                throw new InvalidOperationException("The workspace is closed.");
            }

            if (_worktrees.Any(w => string.Equals(w.Name, worktree.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"{worktree.Name} already has a worktree.");
            }
        }

        await _git.WaitAsync(cancellationToken);
        try
        {
            EnsureSucceeded(await add(), $"create {worktree.Name}'s worktree");
            lock (_lock)
            {
                _worktrees.Add(worktree);
            }
        }
        finally
        {
            _git.Release();
        }

        if (linkDependencies)
        {
            await LinkDependenciesAsync(worktree.Path, cancellationToken);
        }
    }

    /// <summary>
    /// Links the user's git-ignored dependency folders into <paramref name="worktree"/> at the same
    /// relative paths. A link that cannot be made, or that git would not ignore in the worktree (so a
    /// worker's `git add -A` would stage it), is removed again and reported in <see cref="NotMirrored"/>;
    /// the worktree is created either way, and the preflight build says whether it mattered.
    /// </summary>
    private async Task LinkDependenciesAsync(string worktree, CancellationToken cancellationToken)
    {
        var links = new List<string>();
        foreach (var folder in IgnoredDependencyFolders)
        {
            var relative = folder.Replace('/', Path.DirectorySeparatorChar);
            var link = Path.Combine(worktree, relative);
            if (Path.Exists(link))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                TeamExternalProjects.CreateLink(link, Path.Combine(RepositoryRoot, relative));
                links.Add(link);
                if (!await _teamGit.IsIgnoredAsync(worktree, folder, cancellationToken))
                {
                    TeamExternalProjects.Unlink([link]);
                    links.Remove(link);
                    lock (_lock)
                    {
                        _notMirrored.Add($"{relative} (git would not ignore a link to it in the team's copy)");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                lock (_lock)
                {
                    _notMirrored.Add($"{relative} ({ex.Message})");
                }
            }
        }

        lock (_lock)
        {
            _dependencyLinks[worktree] = links;
        }
    }

    private void UnlinkDependencies(string worktree)
    {
        IReadOnlyList<string>? links;
        lock (_lock)
        {
            _dependencyLinks.Remove(worktree, out links);
        }

        if (links is not null)
        {
            TeamExternalProjects.Unlink(links);
        }
    }

    /// <summary>
    /// Whether a worktree's branch holds work the team branch does not, after first committing any edits
    /// it left uncommitted. Deleting every agent branch at the end would throw away whatever had not been
    /// merged - on a run whose merge gate never passed, that is all of it, and a judge's failing test is
    /// evidence that only ever lives on its own branch.
    /// </summary>
    private async Task<bool> PreserveUnmergedWorkAsync(WorkspaceWorktree worktree)
    {
        try
        {
            if (await _teamGit.HasUncommittedChangesAsync(worktree.Path, CancellationToken.None))
            {
                await _teamGit.CommitAllAsync(worktree.Path, UnfinishedWorkCommitMessage, worktree.Author, CancellationToken.None);
            }

            var head = await _teamGit.GetHeadAsync(worktree.Path, CancellationToken.None);
            return !await _teamGit.IsAncestorAsync(RepositoryRoot, head, TeamBranch, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Could not tell. Keeping a branch costs nothing; deleting one that held work loses it.
            return true;
        }
    }

    private string? DiskShortfall(int worktrees, long bytesEach)
    {
        if (worktrees <= 0)
        {
            return null;
        }

        var needed = worktrees * bytesEach;
        var free = _freeDiskSpace(RunDirectory);
        return free - needed >= DiskHeadroomBytes
            ? null
            : $"{worktrees} worktree(s) need about {Gigabytes(needed)}, {Gigabytes(free)} is free, and "
              + $"{Gigabytes(DiskHeadroomBytes)} is kept spare";
    }

    private static long DriveFreeSpace(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    private static string Gigabytes(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";

    /// <summary>A built worktree's size; anything unreadable is skipped - it is an estimate.</summary>
    private static long DirectorySize(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                // Links are skipped, not followed: a linked node_modules is the user's, and shared, so it is
                // none of this worktree's weight - and following it would count it once per worktree.
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void EnsureSucceeded(GitCommandResult result, string what)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not {what}: {result.Output}");
        }
    }
}
