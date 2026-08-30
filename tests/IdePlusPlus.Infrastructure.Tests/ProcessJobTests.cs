using System.Diagnostics;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

/// <summary>
/// Spawns real processes, because the thing under test is a kernel behaviour that cannot be faked:
/// Process.Kill(entireProcessTree: true) was measured leaving a grandchild alive and orphaned when
/// stopping `npm run watch`, and ProcessJob exists to close that hole. A test that only checked the
/// direct child would pass against the broken behaviour too, so these deliberately reach two levels
/// down.
/// </summary>
public class ProcessJobTests
{
    /// <summary>PowerShell rather than cmd: the grandchild has to report its own PID, and cmd has
    /// no equivalent of $PID.</summary>
    private static string GrandchildCommand(string pidFilePath) =>
        $"/c powershell.exe -NoProfile -NonInteractive -Command \"$PID | Out-File -Encoding ascii -FilePath '{pidFilePath}'; Start-Sleep -Seconds 120\"";

    [Fact]
    public void Kill_TerminatesAGrandchild()
    {
        using var temp = new TempPidFile();
        using var job = ProcessJob.TryCreate();
        job.ShouldNotBeNull();

        using var process = StartTree(temp.Path);
        job.TryAssign(process).ShouldBeTrue();

        var grandchildPid = temp.WaitForPid();
        IsAlive(grandchildPid).ShouldBeTrue("the grandchild should be running before the kill");

        job.Kill();

        WaitUntilGone(grandchildPid).ShouldBeTrue("the grandchild should not survive TerminateJobObject");
    }

    /// <summary>KILL_ON_JOB_CLOSE is the backstop for a run that finished without anyone calling
    /// Kill - a leaked background child still gets cleaned up.</summary>
    [Fact]
    public void Dispose_TerminatesAGrandchild()
    {
        using var temp = new TempPidFile();
        var job = ProcessJob.TryCreate();
        job.ShouldNotBeNull();

        using var process = StartTree(temp.Path);
        job.TryAssign(process).ShouldBeTrue();

        var grandchildPid = temp.WaitForPid();
        IsAlive(grandchildPid).ShouldBeTrue("the grandchild should be running before dispose");

        job.Dispose();

        WaitUntilGone(grandchildPid).ShouldBeTrue("the grandchild should not survive the job handle closing");
    }

    [Fact]
    public void Kill_OnAJobWhoseProcessesAlreadyExited_DoesNotThrow()
    {
        using var job = ProcessJob.TryCreate();
        job.ShouldNotBeNull();

        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            job.TryAssign(process);
            process.WaitForExit();
        }

        Should.NotThrow(job.Kill);
        Should.NotThrow(job.Kill);
    }

    private static Process StartTree(string pidFilePath) =>
        Process.Start(new ProcessStartInfo("cmd.exe", GrandchildCommand(pidFilePath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static bool IsAlive(int pid)
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

    private static bool WaitUntilGone(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
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

    /// <summary>Also kills whatever wrote the file, so a failing assertion cannot leave a
    /// two-minute PowerShell process behind.</summary>
    private sealed class TempPidFile : IDisposable
    {
        public TempPidFile() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ideplusplus-processjob-{Guid.NewGuid():N}.pid");

        public string Path { get; }

        public int WaitForPid()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(Path) && int.TryParse(File.ReadAllText(Path).Trim(), out var pid))
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

            throw new TimeoutException("The grandchild never reported its PID.");
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path) && int.TryParse(File.ReadAllText(Path).Trim(), out var pid))
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
                File.Delete(Path);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
