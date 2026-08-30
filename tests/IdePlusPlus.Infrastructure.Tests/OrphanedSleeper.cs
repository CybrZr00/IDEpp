using System.Diagnostics;

namespace IdePlusPlus.Infrastructure.Tests;

/// <summary>
/// Spawns the process shape that defeats <c>Process.Kill(entireProcessTree: true)</c>: a launcher
/// that starts a long-running sleeper and then exits immediately, leaving the sleeper orphaned. The
/// tree kill walks parent PIDs, so once the launcher is gone the sleeper is not in anyone's tree
/// and survives - which is exactly what a real `npm run watch` was measured doing, with the
/// surviving node.exe still holding the redirected stdout pipe open.
///
/// Written as .ps1 files with their paths baked in rather than as nested <c>-Command</c> strings:
/// three levels of PowerShell quoting is its own bug source, and the thing under test is process
/// lifetime, not quoting.
/// </summary>
internal sealed class OrphanedSleeper : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"ideplusplus-orphan-{Guid.NewGuid():N}");

    private readonly string _sleeperPidPath;
    private readonly string _launcherPidPath;

    public OrphanedSleeper()
    {
        Directory.CreateDirectory(_directory);

        _sleeperPidPath = Path.Combine(_directory, "sleeper.pid");
        _launcherPidPath = Path.Combine(_directory, "launcher.pid");
        var sleeperScript = Path.Combine(_directory, "sleeper.ps1");
        var launcherScript = Path.Combine(_directory, "launcher.ps1");

        File.WriteAllText(sleeperScript, $"""
            $PID | Out-File -Encoding ascii -FilePath '{Literal(_sleeperPidPath)}'
            Start-Sleep -Seconds 120
            """);

        // Records its own PID *after* starting the sleeper and then falls off the end, so a test
        // can wait for this process to exit and know the sleeper is orphaned before it kills
        // anything.
        File.WriteAllText(launcherScript, $"""
            Start-Process -WindowStyle Hidden -FilePath 'powershell.exe' -ArgumentList '-NoProfile -NonInteractive -File "{sleeperScript}"'
            $PID | Out-File -Encoding ascii -FilePath '{Literal(_launcherPidPath)}'
            """);

        LauncherCommandLine = $"powershell.exe -NoProfile -NonInteractive -File \"{launcherScript}\"";
    }

    /// <summary>A command line that starts the launcher as a separate process, for pasting into
    /// whatever the service under test actually runs.</summary>
    public string LauncherCommandLine { get; }

    public int WaitForSleeperPid() => WaitForPid(_sleeperPidPath);

    public int WaitForLauncherPid() => WaitForPid(_launcherPidPath);

    public static bool IsAlive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool WaitUntilGone(int pid, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    public void Dispose()
    {
        // A failing assertion must not leave a two-minute sleeper behind.
        try
        {
            if (File.Exists(_sleeperPidPath) && int.TryParse(File.ReadAllText(_sleeperPidPath).Trim(), out var pid))
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the expected case.
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static string Literal(string path) => path.Replace("'", "''");

    private static int WaitForPid(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid))
                {
                    return pid;
                }
            }
            catch (IOException)
            {
                // Being written right now - try again.
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException($"Nothing ever wrote a PID to {path}.");
    }
}
