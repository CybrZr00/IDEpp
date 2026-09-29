using System.ComponentModel;
using System.Diagnostics;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Tasks;

/// <summary>Exit code and combined output of a finished tool run. <paramref name="TimedOut"/> means
/// it was killed rather than having chosen to exit, so the exit code says nothing useful.</summary>
public sealed record NodeToolResult(int ExitCode, string Output, bool TimedOut, string? StartFailure = null)
{
    public bool Started => StartFailure is null;
}

/// <summary>
/// Runs a Node CLI tool once and collects what it printed - the shared plumbing behind gulp and
/// grunt task discovery.
///
/// Separate from <see cref="TaskRunService"/> because the two want opposite things. Running a task
/// streams output to the user indefinitely until they stop it; listing tasks wants the whole output
/// as one string, bounded by a short timeout, and nothing on screen. What they do share is the part
/// that has to be right: resolving the tool through <see cref="NodeToolLocator"/> rather than
/// handing a bare name to CreateProcess, and containing the process tree in a
/// <see cref="ProcessJob"/> so a gulpfile that starts a watcher at module scope cannot outlive the
/// listing that provoked it.
/// </summary>
public static class NodeToolRunner
{
    private const int MaxOutputChars = 64 * 1024;

    /// <summary>
    /// Resolves <paramref name="toolName"/> locally first and falls back to running it through
    /// <c>npx</c>, which is how a repo that lists gulp as a devDependency without a local .bin shim
    /// still works.
    /// </summary>
    public static async Task<NodeToolResult> RunAsync(
        string toolName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (NodeToolLocator.Locate(toolName, workingDirectory) is { } toolPath)
        {
            return await RunResolvedAsync(toolPath, arguments, workingDirectory, timeout, cancellationToken);
        }

        if (NodeToolLocator.Locate("npx", workingDirectory) is { } npxPath)
        {
            // --no-install so a missing tool fails instead of silently downloading one: fetching a
            // package from the network is not something listing tasks should ever do.
            IReadOnlyList<string> npxArguments = ["--no-install", toolName, .. arguments];
            return await RunResolvedAsync(npxPath, npxArguments, workingDirectory, timeout, cancellationToken);
        }

        return new NodeToolResult(
            -1, "", TimedOut: false,
            StartFailure: $"Could not find {toolName} or npx - install Node.js, or add {toolName} to this project.");
    }

    private static async Task<NodeToolResult> RunResolvedAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executablePath)
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

        var output = new System.Text.StringBuilder();

        void Append(string line)
        {
            lock (output)
            {
                if (output.Length < MaxOutputChars)
                {
                    output.AppendLine(line);
                }
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data); };

        using var job = ProcessJob.TryCreate();

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new NodeToolResult(-1, "", TimedOut: false, StartFailure: $"Could not start {executablePath}: {ex.Message}");
        }

        job?.TryAssign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            KillBestEffort(process, job);
        }

        lock (output)
        {
            return new NodeToolResult(process.HasExited ? process.ExitCode : -1, output.ToString(), timedOut);
        }
    }

    private static void KillBestEffort(Process process, ProcessJob? job)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or refused to die - the job below is the part that has to work anyway.
        }

        job?.Kill();
    }
}
