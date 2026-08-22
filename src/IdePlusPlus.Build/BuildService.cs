using System.Diagnostics;

namespace IdePlusPlus.Build;

public sealed class BuildService
{
    public async Task<int> BuildAsync(
        string projectOrSolutionPath,
        Action<string> onOutputLine,
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectOrSolutionPath),
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectOrSolutionPath);
        if (targetFramework is not null)
        {
            startInfo.ArgumentList.Add("--framework");
            startInfo.ArgumentList.Add(targetFramework);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
