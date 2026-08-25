using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace IdePlusPlus.Infrastructure;

/// <summary>Exit code and combined output of a finished command. TimedOut means the process was
/// killed rather than having chosen to exit, so ExitCode says nothing useful.</summary>
public sealed record ShellCommandResult(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// Runs one shell command and returns what it printed.
///
/// PowerShell rather than cmd.exe: it is what the user asked for, and its non-interactive switches
/// are the difference between a command that fails and one that hangs forever waiting for input
/// nobody can type - the same problem GitService solves with GIT_TERMINAL_PROMPT=0.
///
/// Kills the whole process tree on timeout or cancellation. A shell command routinely spawns
/// children (dotnet spawns MSBuild worker nodes, npm spawns node), and killing only the shell
/// leaves those running - the exact bug BuildService had.
/// </summary>
public sealed class ShellCommandRunner
{
    public async Task<ShellCommandResult> RunAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        // -NoProfile so a user's $PROFILE cannot change what a command means; -NonInteractive so a
        // prompt fails fast instead of hanging; -ExecutionPolicy Bypass because -Command is not a
        // script file and should not be blocked by a machine policy meant for those.
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        var output = new StringBuilder();
        var truncated = false;

        void Append(string line)
        {
            lock (output)
            {
                if (output.Length >= maxOutputChars)
                {
                    truncated = true;
                    return;
                }

                output.AppendLine(line);
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new ShellCommandResult(-1, $"Could not start PowerShell: {ex.Message}", TimedOut: false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Closed immediately: a command that reads stdin gets EOF rather than blocking forever on
        // input that is never coming.
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        if (truncated)
        {
            text += $"\n[output truncated at {maxOutputChars} chars]";
        }

        return new ShellCommandResult(timedOut ? -1 : process.ExitCode, text, timedOut);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // Already gone, or refused to die - nothing further to do here.
        }
    }
}
