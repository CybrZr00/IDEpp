using System.Diagnostics;

namespace IdePlusPlus.Run;

public sealed class RunService
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Starts `dotnet run` for the given project. Callbacks arrive on a background thread.
    /// targetFramework is required for multi-targeted projects (e.g. MAUI) - without it, `dotnet
    /// run` errors asking which framework to disambiguate to, or tries every one (including
    /// mobile TFMs whose workloads likely aren't installed).
    /// </summary>
    public void Start(
        string projectPath,
        Action<string> onOutputLine,
        Action<int> onExited,
        string? targetFramework = null,
        string? configuration = null,
        string? launchProfileName = null)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A process is already running; call Stop first.");
        }

        // `dotnet run` resolves launchSettings.json's env vars/URLs itself once told which profile to
        // use - loaded once here and reused both to build --launch-profile/args below and to decide
        // when to auto-open a browser.
        var launchProfile = LaunchSettingsLoader.Load(projectPath, launchProfileName);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath),
        };
        foreach (var arg in BuildArguments(projectPath, targetFramework, configuration, launchProfile))
        {
            startInfo.ArgumentList.Add(arg);
        }

        var browserLaunched = false;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line)
            {
                return;
            }

            onOutputLine(line);

            if (!browserLaunched && launchProfile is { LaunchBrowser: true }
                && KestrelUrlDetector.TryExtractListeningUrl(line) is { } url)
            {
                browserLaunched = true;
                BrowserLauncher.Open(url);
            }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
        process.Exited += (_, _) =>
        {
            onExited(process.ExitCode);
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }

            process.Dispose();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    public void Stop()
    {
        if (_process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Pure assembly of the `dotnet run` argument list, split out from <see cref="Start"/> so
    /// it's testable without spawning a real process. --launch-profile is only meaningful for
    /// commandName "Project" profiles; commandLineArgs are appended after a literal "--" so they reach
    /// the launched app rather than being parsed as `dotnet run`'s own arguments.</summary>
    public static List<string> BuildArguments(
        string projectPath,
        string? targetFramework,
        string? configuration,
        LaunchProfile? launchProfile)
    {
        var arguments = new List<string> { "run", "--project", projectPath };
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

        if (launchProfile is { CommandName: "Project" })
        {
            arguments.Add("--launch-profile");
            arguments.Add(launchProfile.Name);
        }

        if (launchProfile is { CommandLineArgs.Count: > 0 })
        {
            arguments.Add("--");
            arguments.AddRange(launchProfile.CommandLineArgs);
        }

        return arguments;
    }
}
