using System.Diagnostics;

namespace IdePlusPlus.Build;

public sealed class BuildService
{
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
        var cleanExitCode = await CleanAsync(projectOrSolutionPath, onOutputLine, targetFramework, configuration, cancellationToken);
        if (cleanExitCode != 0)
        {
            return cleanExitCode;
        }

        return await BuildAsync(projectOrSolutionPath, onOutputLine, targetFramework, configuration, cancellationToken);
    }

    private static async Task<int> RunDotnetVerbAsync(
        string verb,
        string projectOrSolutionPath,
        Action<string> onOutputLine,
        string? targetFramework,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectOrSolutionPath),
        };
        startInfo.ArgumentList.Add(verb);
        startInfo.ArgumentList.Add(projectOrSolutionPath);
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
