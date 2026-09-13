using System.Diagnostics;
using System.Threading;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The same kind of concrete gate as <see cref="SamplePluginCompatibilityTests"/>, for
/// samples/AccessVbaPlugin - a real plugin built independently of IdePlusPlus.slnx that also carries
/// its own external dependency closure (it references AccessMCP.Core from the separate AccessMCP repo
/// in-process). This is the part of Phase 1 of docs/vba-access-support-scope.md that needs verifying
/// without launching the full app: does the plugin DLL actually load and Initialize through the real
/// PluginLoadContext-based loader, with its dependencies (AccessMCP.Core, its own package references,
/// and AvalonEdit falling through to the host's copy) resolving correctly.
///
/// Skipped outright if the AccessMCP repo isn't present at its expected machine-local path - this test
/// is inherently tied to this developer's machine layout, the same way the plugin project's
/// ProjectReference to AccessMCP.Core is.
/// </summary>
public sealed class AccessVbaPluginCompatibilityTests
{
    private const string AccessMcpCoreProjectPath = @"C:\Users\markp\source\repos\AccessMCP\AccessMCP.Core\AccessMCP.Core.csproj";

    [Fact]
    public void AccessVbaPlugin_LoadsAndInitializes_ThroughPluginLoadContext()
    {
        if (!File.Exists(AccessMcpCoreProjectPath))
        {
            return; // AccessMCP repo not present on this machine - nothing to verify here.
        }

        var pluginProjectPath = FindAccessVbaPluginCsproj();
        var outputDirectory = BuildAccessVbaPlugin(pluginProjectPath);

        var pluginsDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-accessvba-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginsDirectory);

        try
        {
            // Same reasoning as SamplePluginCompatibilityTests: ship exactly what a real plugin author
            // would - the plugin's own DLL, its .deps.json (so AssemblyDependencyResolver can find its
            // package dependencies in the NuGet cache), and AccessMCP.Core.dll (a ProjectReference,
            // copied locally by the build the same way SamplePlugin's reference to PluginContracts is -
            // which is exactly why that one is deliberately NOT copied here either).
            foreach (var fileName in new[] { "AccessVbaPlugin.dll", "AccessVbaPlugin.deps.json", "AccessMCP.Core.dll" })
            {
                File.Copy(Path.Combine(outputDirectory, fileName), Path.Combine(pluginsDirectory, fileName), overwrite: true);
            }

            var host = new RecordingPluginHost();
            var loader = new PluginLoader(host, new OutputPaneViewModel());

            loader.LoadPlugins(pluginsDirectory);

            loader.LoadedPlugins.Count.ShouldBe(1);
            loader.LoadedPlugins[0].Plugin!.Name.ShouldBe("Access VBA");
            host.RegisteredToolWindows.ShouldContain(w => w.Title == "Access VBA");
            host.LoggedMessages.ShouldContain(m => m.Contains("registered the Access VBA tool window"));

            // The factory itself constructs a real WPF UserControl (with an embedded AvalonEdit editor,
            // syntax highlighting, and an in-process AccessTools instance) - that needs an STA thread,
            // which xunit's own thread pool doesn't provide. Setting AvalonEdit's SyntaxHighlighting can
            // queue Dispatcher work (e.g. an async re-highlight pass), so this pumps the thread's own
            // Dispatcher briefly after construction - a bare STA thread with no message loop at all is
            // not representative of the real app, which always has one running.
            var (_, factory) = host.RegisteredToolWindows.Single(w => w.Title == "Access VBA");
            Exception? capturedError = null;
            var sta = new Thread(() =>
            {
                try
                {
                    factory();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                catch (Exception ex)
                {
                    capturedError = ex;
                }
                finally
                {
                    // Shut the thread's Dispatcher down properly so WPF/AvalonEdit release their
                    // timers and hooks in an orderly way, rather than leaving them for finalizers
                    // to trip over after the thread is gone.
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            sta.SetApartmentState(ApartmentState.STA);
            // Background, so a thread that gets stuck constructing the window fails this test via the
            // bounded Join below instead of keeping the test host alive forever after the run.
            sta.IsBackground = true;
            sta.Start();
            var joined = sta.Join(TimeSpan.FromSeconds(30));
            joined.ShouldBeTrue("the STA thread constructing the tool window did not finish within 30s - looks hung, not crashed.");

            capturedError.ShouldBeNull(capturedError?.ToString());

            // Deliberately NOT unloading the plugin's collectible load context here, unlike
            // SamplePluginCompatibilityTests: this plugin created real WPF controls, and forcing an
            // unload plus finalization with those around crashed the test host intermittently (an
            // exception in a finalizer takes the whole process down). Unload correctness is covered by
            // the sample plugin's test; this one is about loading and constructing. The load context
            // keeps its DLLs open, so the temp directory cleanup below is best-effort, as it is there.
        }
        finally
        {
            try
            {
                Directory.Delete(pluginsDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string FindAccessVbaPluginCsproj()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IdePlusPlus.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not find the repo root (IdePlusPlus.slnx) above " + AppContext.BaseDirectory);
        }

        var csprojPath = Path.Combine(directory.FullName, "samples", "AccessVbaPlugin", "AccessVbaPlugin.csproj");
        File.Exists(csprojPath).ShouldBeTrue($"expected to find {csprojPath}");
        return csprojPath;
    }

    private static string BuildAccessVbaPlugin(string csprojPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(csprojPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        // No reusable worker nodes and no shared compiler server: those are child processes that
        // inherit this build's redirected stdout/stderr handles and then idle for ~15 minutes after
        // the build has finished, so the streams never reach EOF and the read below blocks until
        // they exit. That was the "15-minute run" and, under an external timeout, the "test host
        // crashed" - and it only bites when no node from a previous build happens to be alive.
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");

        using var process = Process.Start(startInfo)!;
        // Read both streams concurrently: reading stdout to the end before touching stderr deadlocks
        // the moment the child fills the stderr pipe buffer - and this build's output is large.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("'dotnet build' on AccessVbaPlugin did not finish within 10 minutes.");
        }

        var stdOut = stdOutTask.GetAwaiter().GetResult();
        var stdErr = stdErrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'dotnet build' on AccessVbaPlugin failed with exit code {process.ExitCode}.\n{stdOut}\n{stdErr}");
        }

        var projectDirectory = Path.GetDirectoryName(csprojPath)!;
        var dllPath = Directory.GetFiles(projectDirectory, "AccessVbaPlugin.dll", SearchOption.AllDirectories)
            .Single(p => p.Contains(Path.Combine("bin", "Release")) && !p.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}"));
        return Path.GetDirectoryName(dllPath)!;
    }
}
