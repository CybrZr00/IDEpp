using System.Diagnostics;

namespace IdePlusPlus.Run;

public sealed class RunService
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Starts `dotnet run` for the given project. Callbacks arrive on a background thread.</summary>
    public void Start(string projectPath, Action<string> onOutputLine, Action<int> onExited)
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

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
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
