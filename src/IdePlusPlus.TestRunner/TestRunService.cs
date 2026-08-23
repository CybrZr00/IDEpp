using System.Diagnostics;

namespace IdePlusPlus.TestRunner;

/// <summary>
/// Runs `dotnet test` with a TRX logger, streaming raw output lines live (mirroring BuildService's
/// ProcessStartInfo/ArgumentList/OutputDataReceived pattern) and parsing the resulting .trx once the
/// process exits. Cancel() mirrors RunService.Stop() exactly - the only real process-cancellation
/// precedent in this codebase - since BuildService's own CancellationToken parameter is dead code
/// today (nothing wires a cancel button to it; it only stops awaiting, not the process itself).
/// </summary>
public sealed class TestRunService
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public async Task<IReadOnlyList<TestResult>> RunAsync(
        string testProjectPath,
        IReadOnlyList<string>? filterFullyQualifiedNames,
        Action<string> onOutputLine,
        CancellationToken ct = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A test run is already in progress; call Cancel first.");
        }

        var resultsDirectory = Path.Combine(Path.GetTempPath(), "IdePlusPlus.TestRunner", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resultsDirectory);
        var trxFileName = $"{Guid.NewGuid():N}.trx";
        var trxFilePath = Path.Combine(resultsDirectory, trxFileName);

        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(testProjectPath),
            };
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add(testProjectPath);
            startInfo.ArgumentList.Add("--logger");
            startInfo.ArgumentList.Add($"trx;LogFileName={trxFileName}");
            startInfo.ArgumentList.Add("--results-directory");
            startInfo.ArgumentList.Add(resultsDirectory);

            if (filterFullyQualifiedNames is { Count: > 0 })
            {
                var filter = string.Join('|', filterFullyQualifiedNames.Select(name => $"FullyQualifiedName~{name}"));
                startInfo.ArgumentList.Add("--filter");
                startInfo.ArgumentList.Add(filter);
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };

            process.Start();
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // WaitForExitAsync only stops awaiting on cancellation - Cancel() (or the caller,
                // via the token) is responsible for actually killing the process tree.
            }

            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }

            return File.Exists(trxFilePath) ? TrxParser.Parse(trxFilePath) : [];
        }
        finally
        {
            try
            {
                if (Directory.Exists(resultsDirectory))
                {
                    Directory.Delete(resultsDirectory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup only - a locked/undeletable temp directory shouldn't fail the run.
            }
        }
    }

    public void Cancel()
    {
        if (_process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
        }
    }
}
