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
/// On timeout or cancellation it kills the whole process tree and then kills a
/// <see cref="ProcessJob"/> the shell was assigned to. Both, because neither on its own is enough:
/// a shell command routinely spawns children (dotnet spawns MSBuild worker nodes, npm spawns node)
/// so killing only the shell leaves those running - the exact bug BuildService had - while the tree
/// kill was measured leaving an orphaned grandchild alive under a real `npm run watch`, still
/// holding the redirected stdout pipe open so the reader never saw EOF. See <see cref="ProcessJob"/>
/// for the chain and why a job object does reach it.
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

        // Created before Start so assignment can happen as soon as possible afterwards - see
        // ProcessJob for the race that narrows but cannot fully close. Disposing it here is also
        // the backstop for a command that finished normally but left something running behind it:
        // KILL_ON_JOB_CLOSE takes that with it, which is the right end of a call that promises one
        // command's output and hands back no handle on anything else.
        using var job = ProcessJob.TryCreate();

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new ShellCommandResult(-1, $"Could not start PowerShell: {ex.Message}", TimedOut: false);
        }

        job?.TryAssign(process);

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
            KillTree(process, job);
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

    private static void KillTree(Process process, ProcessJob? job)
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

        // After the tree kill, not instead of it: the tree kill reaches anything spawned in the
        // window before the job assignment, and the job reaches the orphans the tree walk cannot.
        job?.Kill();
    }
}
