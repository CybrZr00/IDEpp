using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdePlusPlus.NuGet;

/// <summary>Shells to dotnet.exe for NuGet package operations (list/add/remove/restore), mirroring
/// GitService's ArgumentList process-spawn pattern but streaming output like BuildService for the
/// slow operations (add/restore).</summary>
public sealed class NuGetService
{
    public async Task<IReadOnlyList<InstalledPackage>> ListPackagesAsync(string csprojPath, CancellationToken cancellationToken = default)
    {
        var (success, stdOut, _) = await RunCapturedAsync(
            Path.GetDirectoryName(csprojPath) ?? string.Empty,
            ["list", csprojPath, "package", "--format", "json"],
            cancellationToken);

        if (!success)
        {
            return [];
        }

        try
        {
            var root = JsonNode.Parse(stdOut);
            var packages = new List<InstalledPackage>();

            foreach (var project in AsArray(root?["projects"]))
            {
                foreach (var framework in AsArray(project?["frameworks"]))
                {
                    // Transitive packages are intentionally never requested (no --include-transitive)
                    // so only top-level/direct references ever appear here.
                    foreach (var package in AsArray(framework?["topLevelPackages"]))
                    {
                        packages.Add(new InstalledPackage(
                            Id: package?["id"]?.GetValue<string>() ?? string.Empty,
                            ResolvedVersion: package?["resolvedVersion"]?.GetValue<string>() ?? string.Empty,
                            RequestedVersion: package?["requestedVersion"]?.GetValue<string>(),
                            IsTransitive: false));
                    }
                }
            }

            return packages;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // `dotnet list package --format json`'s schema is not guaranteed across SDK versions -
            // a parse failure is a soft error, not a crash.
            return [];
        }
    }

    public Task<(bool Success, string Output)> AddPackageAsync(
        string csprojPath, string packageId, string? version, Action<string> onOutputLine, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["add", csprojPath, "package", packageId];
        if (!string.IsNullOrEmpty(version))
        {
            arguments.Add("--version");
            arguments.Add(version);
        }

        return RunStreamingAsync(Path.GetDirectoryName(csprojPath) ?? string.Empty, arguments, onOutputLine, cancellationToken);
    }

    public async Task<(bool Success, string Output)> RemovePackageAsync(string csprojPath, string packageId, CancellationToken cancellationToken = default)
    {
        var (success, stdOut, stdErr) = await RunCapturedAsync(
            Path.GetDirectoryName(csprojPath) ?? string.Empty,
            ["remove", csprojPath, "package", packageId],
            cancellationToken);
        var combined = string.Join("\n", new[] { stdOut, stdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return (success, combined);
    }

    public Task<(bool Success, string Output)> RestoreAsync(string projectOrSolutionPath, Action<string> onOutputLine, CancellationToken cancellationToken = default) =>
        RunStreamingAsync(Path.GetDirectoryName(projectOrSolutionPath) ?? string.Empty, ["restore", projectOrSolutionPath], onOutputLine, cancellationToken);

    private static IEnumerable<JsonNode> AsArray(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    private static async Task<(bool Success, string StdOut, string StdErr)> RunCapturedAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) { startInfo.ArgumentList.Add(argument); }

        using var process = new Process { StartInfo = startInfo };
        try { process.Start(); }
        catch (Win32Exception ex) { return (false, "", $"Could not run dotnet - is the .NET SDK installed and on PATH? ({ex.Message})"); }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode == 0, await stdOutTask, await stdErrTask);
    }

    private static async Task<(bool Success, string Output)> RunStreamingAsync(
        string workingDirectory, IReadOnlyList<string> arguments, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) { startInfo.ArgumentList.Add(argument); }

        var outputLines = new List<string>();
        var sync = new object();
        void Capture(string line)
        {
            lock (sync) { outputLines.Add(line); }
            onOutputLine(line);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Capture(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Capture(e.Data); };

        try { process.Start(); }
        catch (Win32Exception ex)
        {
            var message = $"Could not run dotnet - is the .NET SDK installed and on PATH? ({ex.Message})";
            onOutputLine(message);
            return (false, message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        lock (sync) { return (process.ExitCode == 0, string.Join("\n", outputLines)); }
    }
}
