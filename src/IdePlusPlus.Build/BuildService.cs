using System.ComponentModel;
using System.Diagnostics;

namespace IdePlusPlus.Build;

public sealed class BuildService
{
    private readonly object _stateLock = new();
    private bool _isRunning;

    /// <summary>
    /// Whether the current async flow already holds the guard via <see cref="RunExclusiveAsync"/>.
    /// AsyncLocal rather than a plain field: it has to follow one logical operation's await chain
    /// so the inner Build/Clean call recognises the guard as already held by its own caller, while
    /// an unrelated concurrent caller still sees it as taken and is refused.
    /// </summary>
    private readonly AsyncLocal<bool> _holdsGuard = new();

    /// <summary>
    /// Whether a build/clean/rebuild - or a publish, which goes through <see cref="RunDotnetAsync"/> -
    /// started here is still going. RunService and TestRunService have always had a guard like this;
    /// BuildService did not, which was survivable while the toolbar button was the only caller and
    /// stops being so once the copilot can start one too - two `dotnet build` processes over the same
    /// obj/bin is not a race worth having.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _isRunning;
            }
        }
    }

    public Task<int> BuildAsync(
        string projectOrSolutionPath,
        Action<string> onOutputLine,
        string? targetFramework = null,
        string? configuration = null,
        CancellationToken cancellationToken = default) =>
        RunDotnetVerbAsync("build", projectOrSolutionPath, onOutputLine, targetFramework, configuration, cancellationToken);

    public Task<int> CleanAsync(
        string projectOrSolutionPath,
        Action<string> onOutputLine,
        string? targetFramework = null,
        string? configuration = null,
        CancellationToken cancellationToken = default) =>
        RunDotnetVerbAsync("clean", projectOrSolutionPath, onOutputLine, targetFramework, configuration, cancellationToken);

    /// <summary>`dotnet build -t:Rebuild` isn't a thing MSBuild honors reliably via the dotnet CLI
    /// wrapper for every project shape - clean then build is the same result and matches how
    /// BuildService already composes.</summary>
    public async Task<int> RebuildAsync(
        string projectOrSolutionPath,
        Action<string> onOutputLine,
        string? targetFramework = null,
        string? configuration = null,
        CancellationToken cancellationToken = default)
    {
        var cleanExitCode = await CleanAsync(
            projectOrSolutionPath, onOutputLine, targetFramework, configuration, cancellationToken);
        if (cleanExitCode != 0)
        {
            return cleanExitCode;
        }

        return await BuildAsync(projectOrSolutionPath, onOutputLine, targetFramework, configuration, cancellationToken);
    }

    /// <summary>
    /// Runs an arbitrary `dotnet` invocation under the same single-build-at-a-time guard as
    /// Build/Clean, streaming its output the same way.
    ///
    /// This exists so PublishService doesn't need a lock of its own: `dotnet publish` writes the same
    /// obj/bin a concurrent build would, so the two have to exclude each other, and one guard that
    /// <see cref="IsRunning"/> reports on is simpler than two that have to agree. Arguments are passed
    /// through verbatim - assembling them is the caller's business, which keeps publish-profile
    /// knowledge out of this project entirely.
    /// </summary>
    public Task<int> RunDotnetAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default) =>
        RunGuardedAsync(arguments, workingDirectory, onOutputLine, cancellationToken);

    /// <summary>
    /// Runs arbitrary work holding the same one-at-a-time guard Build/Clean/publish take, so a
    /// sequence of them counts as a single build for <see cref="IsRunning"/>'s purposes.
    ///
    /// This exists for build bindings: a Before Build task and the `dotnet build` after it have to
    /// be one indivisible run, or the copilot's build tool can start a second `dotnet build` in the
    /// gap while the bound task is still writing files the build is about to read. Calls to
    /// Build/Clean/RunDotnet from inside <paramref name="work"/> see the guard as already held by
    /// their own caller and proceed, rather than throwing.
    ///
    /// Throws <see cref="InvalidOperationException"/> - like the verbs themselves - if something
    /// else already holds the guard.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work)
    {
        if (_holdsGuard.Value)
        {
            return await work();
        }

        lock (_stateLock)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("A build is already running.");
            }

            _isRunning = true;
        }

        _holdsGuard.Value = true;
        try
        {
            return await work();
        }
        finally
        {
            _holdsGuard.Value = false;
            lock (_stateLock)
            {
                _isRunning = false;
            }
        }
    }

    private Task<int> RunDotnetVerbAsync(
        string verb,
        string projectOrSolutionPath,
        Action<string> onOutputLine,
        string? targetFramework,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { verb, projectOrSolutionPath };
        if (targetFramework is not null)
        {
            arguments.Add("--framework");
            arguments.Add(targetFramework);
        }

        if (configuration is not null)
        {
            arguments.Add("--configuration");
            arguments.Add(configuration);
        }

        return RunGuardedAsync(
            arguments, Path.GetDirectoryName(projectOrSolutionPath) ?? "", onOutputLine, cancellationToken);
    }

    private async Task<int> RunGuardedAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutputLine,
        CancellationToken cancellationToken)
    {
        // An enclosing RunExclusiveAsync on this same flow already holds the guard on this call's
        // behalf, and taking it again here would refuse the very build it was taken for.
        if (_holdsGuard.Value)
        {
            return await RunProcessAsync(arguments, workingDirectory, onOutputLine, cancellationToken);
        }

        lock (_stateLock)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("A build is already running.");
            }

            _isRunning = true;
        }

        try
        {
            return await RunProcessAsync(arguments, workingDirectory, onOutputLine, cancellationToken);
        }
        finally
        {
            lock (_stateLock)
            {
                _isRunning = false;
            }
        }
    }

    private static async Task<int> RunProcessAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutputLine,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
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

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            onOutputLine($"Could not run dotnet - is the SDK installed and on PATH? ({ex.Message})");
            return -1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // `using var process` disposes the handle without touching the process, so cancelling
            // used to abandon a running dotnet build - and MSBuild's own worker nodes under it -
            // still holding locks on obj/bin. TestRunService.Cancel and RunService.Stop both kill
            // the whole tree; this now matches. Without it a tool timeout leaks a build.
            KillBestEffort(process);
            throw;
        }

        return process.ExitCode;
    }

    private static void KillBestEffort(Process process)
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
            // Already gone, or refused to die - either way there is nothing further to do, and
            // throwing here would replace the caller's cancellation with a less useful error.
        }
    }
}
