using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Finds the Visual Studio C++ build environment, so a shell command that compiles C++ can run.
///
/// A Visual Studio install with the C++ workload has everything - the compiler, linker, headers,
/// and a bundled CMake and Ninja - but none of it is on PATH. VS puts it there per shell, through
/// vcvars64.bat, which is what its "x64 Native Tools Command Prompt" runs. A process started any
/// other way (this app, then PowerShell under it) sees no cl, no cmake, no ninja, which reads as
/// "there is no C++ toolchain" when it is installed and merely not on the path.
///
/// This does what that prompt does: runs vcvars64 once in a cmd.exe, captures the environment it
/// leaves behind, and keeps what differs from this process's own. <see cref="ShellCommandRunner"/>
/// applies it to commands that look like they compile C++ - not to every command, so the usual
/// dotnet/git/npm work keeps the environment it always had.
/// </summary>
public static partial class NativeToolchain
{
    // The tools a C++ build is made of. Word-bounded so "click" or "mylink" don't match, and a path
    // separator before it is allowed so a full path to the tool still counts.
    [GeneratedRegex(
        @"(?<![\w.\-])(cl|link|nmake|cmake|ctest|cpack|ninja|msbuild|dumpbin|ml64|clang-cl|clang|vcvars\w*|lib\.exe|rc\.exe)(\.exe)?(?![\w\-])",
        RegexOptions.IgnoreCase)]
    private static partial Regex ToolPattern();

    /// <summary>Whether a command line looks like it invokes the C++ toolchain. Erring toward true is
    /// cheap (the environment is only applied, not harmful); erring toward false is the failure this
    /// exists to remove.</summary>
    public static bool IsNeededBy(string command) => ToolPattern().IsMatch(command);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyDictionary<string, string>? _cached;
    private static bool _resolved;

    /// <summary>
    /// The environment variables that differ once the Visual Studio C++ environment is set up, or null
    /// when no installation with the C++ tools is found. Computed once per process - it takes a second
    /// or so to run vcvars - so a Visual Studio installed afterwards is picked up on the next start.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>?> GetEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        if (_resolved)
        {
            return _cached;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_resolved)
            {
                _cached = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                _resolved = true;
            }

            return _cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>?> DiscoverAsync(CancellationToken cancellationToken)
    {
        var installation = await FindInstallationAsync(cancellationToken).ConfigureAwait(false);
        if (installation is null)
        {
            return null;
        }

        var vcvars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
        if (!File.Exists(vcvars))
        {
            return null;
        }

        var captured = await CaptureEnvironmentAsync(vcvars, cancellationToken).ConfigureAwait(false);
        if (captured is null)
        {
            return null;
        }

        return Diff(captured, CurrentEnvironment(), BundledToolDirectories(installation));
    }

    /// <summary>The newest Visual Studio (or Build Tools) install that has the x64 C++ tools.</summary>
    private static async Task<string?> FindInstallationAsync(CancellationToken cancellationToken)
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(vswhere)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                     "-property", "installationPath",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var output = await RunAsync(startInfo, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var path = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return path is not null && Directory.Exists(path) ? path : null;
    }

    private static Task<string?> CaptureEnvironmentAsync(string vcvars, CancellationToken cancellationToken)
    {
        // The whole command is one quoted string for cmd /s /c, and vcvars' own banner is discarded so
        // only `set`'s NAME=value lines are left. The path is quoted because "Program Files" has a space.
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /s /c \"\"{vcvars}\" >nul 2>&1 && set\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return RunAsync(startInfo, TimeSpan.FromSeconds(60), cancellationToken);
    }

    private static async Task<string?> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            var text = process.StandardOutput.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? await text.ConfigureAwait(false) : null;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Already gone.
                }

                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            current[(string)entry.Key] = entry.Value as string ?? "";
        }

        return current;
    }

    /// <summary>The CMake and Ninja that ship inside Visual Studio, which are on the Developer
    /// Command Prompt's PATH but not added by vcvars64 itself.</summary>
    private static IEnumerable<string> BundledToolDirectories(string installation)
    {
        var root = Path.Combine(installation, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake");
        return new[] { Path.Combine(root, "CMake", "bin"), Path.Combine(root, "Ninja") }.Where(Directory.Exists);
    }

    /// <summary>
    /// What the vcvars environment changes relative to <paramref name="current"/>, plus any bundled
    /// tool directories not already on PATH. Kept pure so it can be tested without Visual Studio.
    /// </summary>
    internal static Dictionary<string, string> Diff(
        string setOutput, IReadOnlyDictionary<string, string> current, IEnumerable<string> extraPathDirectories)
    {
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in setOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var equals = line.IndexOf('=');

            // A leading '=' is cmd's per-drive bookkeeping (=C:=C:\dir), not a variable.
            if (equals <= 0)
            {
                continue;
            }

            var name = line[..equals];
            var value = line[(equals + 1)..];
            if (!current.TryGetValue(name, out var existing) || !string.Equals(existing, value, StringComparison.Ordinal))
            {
                changes[name] = value;
            }
        }

        var pathValue = changes.TryGetValue("PATH", out var changedPath)
            ? changedPath
            : current.GetValueOrDefault("PATH", "");
        var onPath = new HashSet<string>(
            pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.TrimEnd('\\')),
            StringComparer.OrdinalIgnoreCase);

        var missing = extraPathDirectories.Where(d => !onPath.Contains(d.TrimEnd('\\'))).ToList();
        if (missing.Count > 0)
        {
            changes["PATH"] = string.Join(';', missing) + ";" + pathValue;
        }

        return changes;
    }
}
