using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Templates;

/// <summary>Null is returned instead of one of these when <c>dotnet</c> could not be started at all -
/// a different thing from a command that ran and failed.</summary>
internal sealed record DotnetCliResult(int ExitCode, string Output);

/// <summary>
/// Runs the two read-only <c>dotnet</c> calls the catalog needs - <c>--version</c> to find which
/// SDK's template cache to read, and <c>new list</c> to make the SDK build that cache when it does
/// not exist yet.
///
/// <para>Deliberately not routed through <c>BuildService</c>: its guard exists so two things cannot
/// write the same obj/bin at once, and neither of these writes any. Taking that guard would mean
/// the New Project dialog refused to list templates during a build. Everything that *does* write -
/// creating a template, adding to a solution - goes through <c>BuildService</c> as usual.</para>
///
/// <para>Not <c>ShellCommandRunner</c> either: that goes through PowerShell, which is the right
/// answer for a user-authored command line and the wrong one for a fixed argument list.</para>
/// </summary>
internal static class DotnetCliRunner
{
    public static async Task<DotnetCliResult?> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

        using var job = ProcessJob.TryCreate();

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            // No SDK on PATH. The caller turns this into an empty template list and a readable
            // message rather than an exception out of a menu-opening path.
            return null;
        }

        job?.TryAssign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            KillBestEffort(process, job);
            return null;
        }

        lock (output)
        {
            return new DotnetCliResult(process.ExitCode, output.ToString());
        }
    }

    /// <summary>Tree kill first, then the job - the same pair BuildService and TaskRunService use,
    /// because the tree kill alone was measured leaving orphaned grandchildren alive.</summary>
    private static void KillBestEffort(Process process, ProcessJob? job)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, which is the outcome being asked for.
        }

        job?.Kill();
    }
}
