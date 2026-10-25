using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace IdePlusPlus.SourceControl;

/// <summary>
/// The one place git actually gets spawned, shared by GitService (the Source Control pane and the
/// copilot's git tools) and TeamGitWorkspace (Ultra team runs), so both get the same
/// non-interactive environment and the same "no shell in the loop" argument handling.
/// </summary>
internal static class GitProcess
{
    // Git writes paths, commit messages and blob content as raw UTF-8 bytes. Left unset, .NET
    // decodes redirected output with the console's code page (437/850 under a test host), which
    // turns any non-ASCII file content into mojibake - and TeamGitWorkspace writes file content
    // read this way back into the user's working tree.
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Runs git with <paramref name="arguments"/> in <paramref name="workingDirectory"/>. The
    /// optional <paramref name="environment"/> entries are applied on top of the inherited
    /// environment and the non-interactive defaults - TeamGitWorkspace uses it to point
    /// GIT_INDEX_FILE at a throwaway index so a snapshot never touches the user's real one.
    ///
    /// ExitCode is -1 when git could not be started at all, with the reason in StdErr, so callers
    /// that branch on specific exit codes (merge-base --is-ancestor, merge-file) see it as an error
    /// rather than as one of git's meaningful non-zero results.
    /// </summary>
    public static async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        // Process.Start reports a missing working directory as the same Win32Exception as a missing
        // executable, which would otherwise surface as a misleading "is git installed?" message.
        if (!Directory.Exists(workingDirectory))
        {
            return new GitProcessResult(-1, "", $"Directory not found: {workingDirectory}");
        }

        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
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

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new GitProcessResult(-1, "", $"Could not run git - is it installed and on PATH? ({ex.Message})");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new GitProcessResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }
}

internal readonly record struct GitProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>stdout+stderr combined, in that order - the same shape GitCommandResult.Output
    /// promises, since git puts most human-facing text on stderr even on success.</summary>
    public string Output =>
        string.Join("\n", new[] { StdOut, StdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
