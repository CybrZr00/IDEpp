using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// A Windows job object that kills every process inside it - however deeply nested, and whatever
/// has happened to the parent chain - when it is killed or disposed.
///
/// This exists because <c>Process.Kill(entireProcessTree: true)</c> is not sufficient, which was
/// measured rather than assumed. Running <c>npm run watch</c> produces this chain:
///
/// <code>
/// IDE++ -> cmd.exe (CreateProcess ran npm.cmd) -> node.exe (npm) -> cmd.exe -> node.exe (the script)
/// </code>
///
/// and after a tree kill the innermost node.exe was still alive, re-parented and orphaned, still
/// holding the redirected stdout pipe open. A "Stop" that leaves the watcher running - and leaves
/// the pipe open so the reader never sees EOF - is worse than no Stop button at all. A job object
/// does not walk the tree: membership is inherited by every child at creation time and enforced by
/// the kernel, so terminating the job terminates all of them at once.
///
/// The one gap is the window between <see cref="Process.Start()"/> and <see cref="TryAssign"/>: a
/// child spawned in between is not a member. Closing it properly needs CREATE_SUSPENDED, which
/// <see cref="Process"/> does not expose - so callers should assign immediately after starting, and
/// keep the tree kill as a second line of defence.
/// </summary>
public sealed class ProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly nint _handle;
    private bool _disposed;

    private ProcessJob(nint handle) => _handle = handle;

    /// <summary>Returns null rather than throwing when a job object cannot be created or
    /// configured - a task that runs without one still runs, it just falls back to the tree kill.</summary>
    public static ProcessJob? TryCreate()
    {
        var handle = CreateJobObjectW(nint.Zero, nint.Zero);
        if (handle == nint.Zero)
        {
            return null;
        }

        // KILL_ON_JOB_CLOSE makes Dispose the backstop: whatever a finished task left running is
        // cleaned up when the job handle closes, even if nobody called Kill.
        var limits = new JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new ProcessJob(handle);
    }

    /// <summary>Adds an already-started process, and every process it goes on to spawn, to this job.</summary>
    public bool TryAssign(Process process)
    {
        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited between starting and being assigned, so its handle is unusable.
            // Nothing to contain.
            return false;
        }
    }

    /// <summary>Terminates every process in the job. Safe to call more than once, and on a job
    /// whose processes have already exited.</summary>
    public void Kill()
    {
        if (!_disposed)
        {
            TerminateJobObject(_handle, 1);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseHandle(_handle);
    }

    // DllImport rather than LibraryImport: every signature here is blittable, so the generated
    // marshalling would be a no-op, and LibraryImport would mean turning on AllowUnsafeBlocks for
    // the whole project to gain nothing.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint securityAttributes, nint name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int informationClass, nint information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    // nuint for the SIZE_T/ULONG_PTR fields so the layout is right on both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
