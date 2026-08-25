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
    private readonly object _stateLock = new();
    private Process? _process;
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

    public async Task<IReadOnlyList<TestResult>> RunAsync(
        string testProjectPath,
        IReadOnlyList<string>? filterFullyQualifiedNames,
        Action<string> onOutputLine,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // _process is only assigned after Process.Start, so an IsRunning check alone left a window
        // where two callers could both pass it. That was theoretical while the Test Explorer button
        // was the only caller and is not once the copilot can start a run too.
        lock (_stateLock)
        {
            if (_starting || _process is { HasExited: false })
            {
                throw new InvalidOperationException("A test run is already in progress; call Cancel first.");
            }

            _starting = true;
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
            lock (_stateLock)
            {
                _process = process;
                _starting = false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // WaitForExitAsync only stops awaiting; without this the token left `dotnet test`
            // running. Callers had to know to call Cancel() themselves, which a timeout path is
            // exactly the sort of place to forget.
            using (ct.Register(Cancel))
            {
                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Killed by the registration above; fall through and report whatever the run
                    // managed to write to the .trx before it stopped.
                }
            }

            lock (_stateLock)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            return File.Exists(trxFilePath) ? TrxParser.Parse(trxFilePath) : [];
        }
        finally
        {
            lock (_stateLock)
            {
                _starting = false;
            }

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
        Process? process;
        lock (_stateLock)
        {
            process = _process;
        }

        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or refused to die - nothing further to do, and throwing from a
            // cancellation callback would replace the caller's cancellation with a worse error.
        }
    }
}
