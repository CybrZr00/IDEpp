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
        string? configuration = null)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A process is already running; call Stop first.");
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath),
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        if (targetFramework is not null)
        {
            startInfo.ArgumentList.Add("--framework");
            startInfo.ArgumentList.Add(targetFramework);
        }

        if (configuration is not null)
        {
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add(configuration);
        }

        // `dotnet run` already resolves launchSettings.json's env vars/URLs itself - the only thing
        // missing versus Visual Studio's F5 is opening a browser, so that's all this adds.
        var launchProfile = LaunchSettingsLoader.Load(projectPath);
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
}
