using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace IdePlusPlus.Infrastructure.Hooks;

/// <summary>What a hook process did. <see cref="StartError"/> is set when it never ran at all (the
/// executable was not found), in which case the other fields say nothing.</summary>
public sealed record HookRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartError);

/// <summary>
/// Runs one hook: the event's JSON on stdin, stdout/stderr captured, exit code reported.
///
/// Directly rather than through a shell whenever the hook gave an argument list, so `python` with a
/// script path needs no quoting. Otherwise the command is a PowerShell command line, same as
/// run_command. Either way the process tree is killed on timeout - a hook that shells out to a
/// linter that spawns children must not leave them behind.
/// </summary>
public sealed class HookCommandRunner
{
    private const int MaxCapturedChars = 20_000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<HookRunResult> RunAsync(
        HookCommand hook,
        string stdinJson,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        if (hook.Args is { } args)
        {
            startInfo.FileName = hook.Command;
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }
        else
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(hook.Command);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        // Python on Windows otherwise reads and writes the console code page, which mangles any
        // non-ASCII path or message in the JSON it is handed. Harmless to every other runtime.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new HookRunResult(-1, "", "", TimedOut: false, StartError:
                $"could not start '{startInfo.FileName}': {ex.Message}"
                + (hook.Args is not null ? " Is it installed and on PATH? (For Python, 'py' is the launcher.)" : ""));
        }

        var stdout = ReadCappedAsync(process.StandardOutput);
        var stderr = ReadCappedAsync(process.StandardError);

        try
        {
            await process.StandardInput.WriteAsync(stdinJson).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The hook exited (or never reads stdin) before we finished writing - not an error.
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(hook.TimeoutSeconds));

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

        // Only awaited once the process is gone, so a grandchild holding the pipe open cannot wedge
        // this - after a kill the streams close and these complete promptly.
        var stdoutText = await stdout.ConfigureAwait(false);
        var stderrText = await stderr.ConfigureAwait(false);

        return new HookRunResult(timedOut ? -1 : process.ExitCode, stdoutText, stderrText, timedOut, StartError: null);
    }

    /// <summary>Keeps reading past the cap - stopping would fill the pipe and block the hook - but
    /// only retains the first part.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (text.Length < MaxCapturedChars)
                {
                    text.Append(buffer, 0, Math.Min(read, MaxCapturedChars - text.Length));
                }
            }
        }
        catch (IOException)
        {
            // Killed mid-read: what was captured so far is what there is.
        }

        return text.ToString();
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
