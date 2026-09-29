using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Tasks;

/// <summary>
/// Runs one task at a time, streaming its output live and killing the whole process tree on cancel.
/// Mirrors TestRunService's shape - the same _starting/_process guard against two callers racing
/// past an IsRunning check, and the same ct.Register(Cancel) so a token actually stops the process
/// rather than only stopping the await.
///
/// Stopping goes through a <see cref="ProcessJob"/> rather than relying on
/// Process.Kill(entireProcessTree: true) alone. Measured against a real `npm run watch`, the tree
/// kill left the innermost node.exe alive and orphaned, still holding the redirected stdout pipe -
/// see ProcessJob's own comment for the chain. The tree kill is still issued first; the job is what
/// makes Stop actually mean stop.
///
/// One run at a time overall, not one per manifest - running several tasks concurrently is a
/// deliberate non-goal for now (see docs/task-runner-design.md).
/// </summary>
public sealed partial class TaskRunService
{
    // Deliberately conservative, and enforced rather than escaped. On Windows the package manager
    // resolves to a .cmd shim, and CreateProcess runs a .cmd by handing it to cmd.exe - so the
    // arguments are parsed by cmd.exe, whose quoting rules are not the C-runtime rules
    // ProcessStartInfo.ArgumentList escapes for. Escaping correctly for both parsers at once is the
    // bug class behind BatBadBut (CVE-2024-24576 and friends). Task names come from a file in the
    // repository, which is not necessarily trusted, so a name outside this set is refused instead.
    // Every real npm script name is well inside it.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:\-]*$", RegexOptions.Compiled)]
    private static partial Regex SafeTaskNameRegex();

    private readonly object _stateLock = new();
    private Process? _process;
    private ProcessJob? _job;
    private bool _starting;

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _starting || _process is { HasExited: false };
            }
        }
    }

    /// <summary>
    /// Returns the task's exit code, or -1 if it could never start (tool not on PATH, unusable task
    /// name, unusable manifest path) - in which case the reason has been written to
    /// <paramref name="onOutputLine"/>, since that is where the user is already looking.
    /// </summary>
    public async Task<int> RunAsync(
        TaskDefinition task,
        NodePackageManager packageManager,
        Action<string> onOutputLine,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!SafeTaskNameRegex().IsMatch(task.Name))
        {
            onOutputLine($"Refusing to run '{task.Name}' - task names are limited to letters, digits, '.', ':', '_' and '-'.");
            return -1;
        }

        if (Path.GetDirectoryName(task.ManifestPath) is not { Length: > 0 } workingDirectory)
        {
            onOutputLine($"Refusing to run '{task.Name}' - {task.ManifestPath} has no containing directory.");
            return -1;
        }

        var (toolName, taskArguments) = Invocation(task, packageManager);

        // Same local-first resolution and npx fallback as task discovery: a repo that lists gulp as
        // a devDependency without a .bin shim still runs.
        string executablePath;
        IReadOnlyList<string> arguments;
        if (NodeToolLocator.Locate(toolName, workingDirectory) is { } toolPath)
        {
            executablePath = toolPath;
            arguments = taskArguments;
        }
        else if (NodeToolLocator.Locate("npx", workingDirectory) is { } npxPath)
        {
            executablePath = npxPath;
            arguments = ["--no-install", toolName, .. taskArguments];
        }
        else
        {
            onOutputLine($"Could not find {toolName} - install Node.js, or make sure {toolName} is on PATH.");
            return -1;
        }

        lock (_stateLock)
        {
            if (_starting || _process is { HasExited: false })
            {
                throw new InvalidOperationException("A task is already running; stop it first.");
            }

            _starting = true;
        }

        try
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

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };

            // Created before Start so assignment can happen as soon as possible afterwards - see
            // ProcessJob for the race this narrows but cannot fully close.
            using var job = ProcessJob.TryCreate();

            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                onOutputLine($"Could not start {executablePath}: {ex.Message}");
                return -1;
            }

            job?.TryAssign(process);

            lock (_stateLock)
            {
                _process = process;
                _job = job;
                _starting = false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(Cancel))
            {
                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Killed by the registration above - report what it exited with rather than
                    // throwing, since the caller asked for this.
                }
            }

            lock (_stateLock)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _job = null;
                }
            }

            return process.HasExited ? process.ExitCode : -1;
        }
        finally
        {
            lock (_stateLock)
            {
                _starting = false;
            }
        }
    }

    /// <summary>
    /// How a task is invoked, which depends on where it came from: an npm script runs through its
    /// package manager (`npm run build`), while a gulp or grunt task is a first-class argument to
    /// its own runner (`gulp sass`). Getting this wrong would run `npm run sass` against a gulpfile
    /// and fail with a script-not-found error that says nothing about the real cause.
    /// </summary>
    private static (string ToolName, IReadOnlyList<string> Arguments) Invocation(
        TaskDefinition task, NodePackageManager packageManager) => task.ManifestKind switch
    {
        TaskManifestKind.Gulp => ("gulp", [task.Name]),
        TaskManifestKind.Grunt => ("grunt", [task.Name]),
        _ => (packageManager.ExecutableName(), ["run", task.Name]),
    };

    /// <summary>The command line as the user would type it, for the Output pane.</summary>
    public static string DescribeCommand(TaskDefinition task, NodePackageManager packageManager)
    {
        var (toolName, arguments) = Invocation(task, packageManager);
        return $"{toolName} {string.Join(' ', arguments)}";
    }

    public void Cancel()
    {
        Process? process;
        ProcessJob? job;
        lock (_stateLock)
        {
            process = _process;
            job = _job;
        }

        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone, or refused to die - nothing further to do, and throwing from a
            // cancellation callback would replace the caller's cancellation with a worse error.
        }

        // After the tree kill, not instead of it: the tree kill reaches processes the job might
        // have missed (anything spawned in the assignment window), and the job reaches the ones the
        // tree walk demonstrably does not.
        job?.Kill();
    }
}
