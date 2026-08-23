namespace IdePlusPlus.Debug;

/// <summary>One row for the Attach-to-Process picker: every running process, best-effort flagged
/// as likely-.NET so the UI can highlight/filter without hiding anything the heuristic misses.</summary>
public sealed record ProcessAttachTarget(int ProcessId, string ProcessName, string? MainModulePath, bool IsLikelyDotNet);

/// <summary>
/// Finds candidate processes for Attach-to-Process. The heuristic (<see cref="LooksLikeDotNetProcess"/>)
/// is a pure function over plain strings/file-existence checks so it can be unit tested without a real
/// process list; <see cref="FindCandidates"/> is the live, best-effort enumeration that wraps it.
/// </summary>
public static class ProcessAttachTargetFinder
{
    /// <summary>
    /// Best-effort ".NET process" heuristic - explicitly not a guarantee (see the design's risk list).
    /// True when the process name is "dotnet", or its main module's directory carries hostfxr.dll/
    /// coreclr.dll (a self-contained or framework-dependent CLR host), or it matches the "apphost"
    /// pattern (an exe named after the app with a sibling &lt;name&gt;.deps.json).
    /// </summary>
    public static bool LooksLikeDotNetProcess(string processName, string? mainModulePath)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(processName);
        if (string.Equals(nameWithoutExtension, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(mainModulePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(mainModulePath);
        if (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "hostfxr.dll")) ||
                File.Exists(Path.Combine(directory, "coreclr.dll")))
            {
                return true;
            }
        }

        var moduleNameWithoutExtension = Path.GetFileNameWithoutExtension(mainModulePath);
        if (string.Equals(moduleNameWithoutExtension, nameWithoutExtension, StringComparison.OrdinalIgnoreCase))
        {
            var moduleDirectory = Path.GetDirectoryName(mainModulePath);
            var depsJsonPath = string.IsNullOrEmpty(moduleDirectory)
                ? moduleNameWithoutExtension + ".deps.json"
                : Path.Combine(moduleDirectory, moduleNameWithoutExtension + ".deps.json");
            if (File.Exists(depsJsonPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates all running processes for the picker. One inaccessible/elevated process (MainModule
    /// throws Win32Exception, or the process exits mid-enumeration) never aborts the whole list - it's
    /// just reported with a null MainModulePath and IsLikelyDotNet = false.
    /// </summary>
    public static IReadOnlyList<ProcessAttachTarget> FindCandidates()
    {
        var results = new List<ProcessAttachTarget>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch (Exception)
                {
                    continue;
                }

                string? mainModulePath;
                try
                {
                    mainModulePath = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Access denied (elevated/protected process) or the process exited - treat as inaccessible.
                    mainModulePath = null;
                }

                int processId;
                try
                {
                    processId = process.Id;
                }
                catch (Exception)
                {
                    continue;
                }

                var isLikelyDotNet = LooksLikeDotNetProcess(processName, mainModulePath);
                results.Add(new ProcessAttachTarget(processId, processName, mainModulePath, isLikelyDotNet));
            }
        }

        return results;
    }
}
