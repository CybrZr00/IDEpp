using System.ComponentModel;
using System.Diagnostics;

namespace IdePlusPlus.SourceControl;

/// <summary>
/// Shells out to the git CLI rather than embedding a library like LibGit2Sharp - the same
/// "spawn a process, parse its output" pattern BuildService/RunService already use for dotnet, and
/// the same choice VS Code's built-in Git integration makes: it inherits the user's real git config,
/// credential helpers, and hooks for free, with no native per-platform binary to bundle. Every
/// argument goes through ProcessStartInfo.ArgumentList (never a concatenated command-line string),
/// so a commit message or branch name containing quotes/operators can never be interpreted by a
/// shell - there is no shell in the loop at all.
/// </summary>
public sealed class GitService
{
    public async Task<bool> IsGitRepositoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var (success, stdOut, _) = await RunRawAsync(directory, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        return success && stdOut.Trim() == "true";
    }

    public async Task<GitStatus> GetStatusAsync(string repositoryDirectory, CancellationToken cancellationToken = default)
    {
        var (success, stdOut, _) = await RunRawAsync(repositoryDirectory, ["status", "--porcelain=v1", "--branch"], cancellationToken);
        return success ? GitStatusParser.Parse(stdOut) : new GitStatus(null, 0, 0, [], []);
    }

    public Task<GitCommandResult> StageAsync(string repositoryDirectory, string relativePath, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["add", "--", relativePath], cancellationToken);

    /// <summary>Stages every pending change (tracked and untracked) in one call - backs "Commit All",
    /// which mirrors Visual Studio's Git Changes window: type a message, stage-and-commit everything
    /// in one step, without requiring the file-by-file Stage click first.</summary>
    public Task<GitCommandResult> StageAllAsync(string repositoryDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["add", "-A"], cancellationToken);

    // `git restore --staged` requires HEAD to exist and fails with "fatal: could not resolve 'HEAD'"
    // in a brand-new repo with no commits yet - `git reset --` unstages correctly in both cases.
    public Task<GitCommandResult> UnstageAsync(string repositoryDirectory, string relativePath, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["reset", "--", relativePath], cancellationToken);

    public Task<GitCommandResult> DiscardChangesAsync(string repositoryDirectory, string relativePath, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["checkout", "--", relativePath], cancellationToken);

    public Task<GitCommandResult> CommitAsync(string repositoryDirectory, string message, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["commit", "-m", message], cancellationToken);

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryDirectory, CancellationToken cancellationToken = default)
    {
        var (success, stdOut, _) = await RunRawAsync(
            repositoryDirectory, ["branch", "--format=%(refname:short)\t%(HEAD)"], cancellationToken);
        return success ? GitBranchParser.Parse(stdOut) : [];
    }

    public Task<GitCommandResult> CheckoutBranchAsync(string repositoryDirectory, string branchName, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["checkout", branchName], cancellationToken);

    public Task<GitCommandResult> CreateBranchAsync(string repositoryDirectory, string branchName, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["checkout", "-b", branchName], cancellationToken);

    public Task<GitCommandResult> PushAsync(string repositoryDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["push"], cancellationToken);

    // Plain `git pull` depends on the user's global pull.rebase/pull.ff config to know how to
    // reconcile diverged branches; when that's unset (the out-of-the-box state on a lot of real
    // machines) git 2.29+ refuses outright with "fatal: Need to specify how to reconcile divergent
    // branches" instead of pulling anything - so this pins the strategy explicitly rather than
    // inheriting whatever (if anything) is configured outside the IDE. --no-edit avoids the merge
    // commit needing an editor, which a GUI-launched process has no terminal to run.
    public Task<GitCommandResult> PullAsync(string repositoryDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["pull", "--no-rebase", "--no-edit"], cancellationToken);

    public Task<GitCommandResult> FetchAsync(string repositoryDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(repositoryDirectory, ["fetch"], cancellationToken);

    /// <summary>
    /// The file's content at HEAD, or "" if it has none there yet (a new/untracked file) - matching
    /// EditApplicationService's convention of treating a nonexistent file as empty content, so a
    /// reused DiffComputer renders a brand-new file as entirely "added" rather than erroring.
    /// </summary>
    public async Task<string> GetFileAtHeadAsync(string repositoryDirectory, string relativePath, CancellationToken cancellationToken = default)
    {
        var (success, stdOut, _) = await RunRawAsync(repositoryDirectory, ["show", $"HEAD:{relativePath}"], cancellationToken);
        return success ? stdOut : "";
    }

    /// <summary>
    /// Unified diff of the working tree, or of the index against HEAD when <paramref name="staged"/>
    /// is set. Optionally limited to one path.
    ///
    /// Returns the exit status alongside the output rather than swallowing failure the way
    /// GetStatusAsync does - a caller cannot otherwise tell "no changes" from "git blew up", and
    /// both come back as an empty string. Note that a diff never shows untracked files; use
    /// GetShortStatusAsync to see those.
    /// </summary>
    public async Task<(bool Success, string Output)> GetDiffAsync(
        string repositoryDirectory,
        string? relativePath = null,
        bool staged = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["diff"];
        if (staged)
        {
            arguments.Add("--staged");
        }

        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            arguments.Add("--");
            arguments.Add(relativePath);
        }

        var (success, stdOut, stdErr) = await RunRawAsync(repositoryDirectory, arguments, cancellationToken);
        return (success, success ? stdOut : stdErr);
    }

    /// <summary>Short branch-and-changes summary, in git's own `status --short` form. Includes
    /// untracked files, which a diff does not.</summary>
    public async Task<(bool Success, string Output)> GetShortStatusAsync(
        string repositoryDirectory, CancellationToken cancellationToken = default)
    {
        var (success, stdOut, stdErr) = await RunRawAsync(
            repositoryDirectory, ["status", "--short", "--branch"], cancellationToken);
        return (success, success ? stdOut : stdErr);
    }

    private static async Task<GitCommandResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var (success, stdOut, stdErr) = await RunRawAsync(workingDirectory, arguments, cancellationToken);
        var combined = string.Join("\n", new[] { stdOut, stdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new GitCommandResult(success, combined);
    }

    private static async Task<(bool Success, string StdOut, string StdErr)> RunRawAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // A GUI-launched process has no terminal for git to prompt against, so without this a push/
        // pull/fetch against a remote needing credentials (or an SSH host-key confirmation) just
        // hangs forever waiting for input nobody can type - IsBusy stays true and the pane looks
        // completely dead. Disabling prompts makes git fail fast instead, with a message ("could not
        // read Username...") that actually reaches StatusMessage.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return (false, "", $"Could not run git - is it installed and on PATH? ({ex.Message})");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode == 0, await stdOutTask, await stdErrTask);
    }
}
