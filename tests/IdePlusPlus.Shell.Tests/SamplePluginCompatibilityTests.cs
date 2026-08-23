using System.Diagnostics;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The other concrete gate for this feature: samples/SamplePlugin/HelloWorldPlugin.cs is a real,
/// already-working plugin exercising both pre-existing IPluginHost methods (RegisterHighlighting,
/// RegisterToolsMenuCommand) plus LogInfo. Since it was compiled independently of this change (it isn't
/// part of IdePlusPlus.slnx - see its .csproj comment), it's exactly the kind of already-compiled
/// plugin binary the task's hard backward-compat rule is about: it must keep loading and Initializing
/// through the new PluginLoadContext-based loader exactly as it did through the old plain
/// Assembly.LoadFrom loader, with zero source changes.
///
/// Builds the real SamplePlugin project via the real `dotnet build` CLI (not a fixture copy) and runs
/// the real PluginLoader against a scratch plugins directory containing its actual output - HelloWorldPlugin's
/// Initialize() only registers things, it never shows its "Say Hello" MessageBox, so this is safe to run
/// unattended in a test process.
/// </summary>
public sealed class SamplePluginCompatibilityTests
{
    [Fact]
    public void SamplePlugin_LoadsAndInitializes_ThroughPluginLoadContext()
    {
        var samplePluginProjectPath = FindSamplePluginCsproj();
        var outputDirectory = BuildSamplePlugin(samplePluginProjectPath);

        var pluginsDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-sampleplugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginsDirectory);

        try
        {
            // Only what a real plugin author would actually ship: the plugin's own DLL plus the one
            // extra file it needs alongside it (RegisterHighlighting looks Ini.xshd up relative to the
            // DLL at Initialize time). Deliberately NOT the build output's copy of
            // IdePlusPlus.PluginContracts.dll (present because SamplePlugin references it as a
            // ProjectReference) - PluginLoader treats every *.dll directly under the plugins directory
            // as a candidate plugin, so copying it too would make PluginLoader load a second,
            // never-forwarded copy of the contracts assembly as a (no-op) "plugin" of its own.
            foreach (var fileName in new[] { "SamplePlugin.dll", "Ini.xshd" })
            {
                File.Copy(Path.Combine(outputDirectory, fileName), Path.Combine(pluginsDirectory, fileName), overwrite: true);
            }

            var host = new RecordingPluginHost();
            var loader = new PluginLoader(host, new OutputPaneViewModel());

            loader.LoadPlugins(pluginsDirectory);

            loader.LoadedPlugins.Count.ShouldBe(1);
            loader.LoadedPlugins[0].Plugin!.Name.ShouldBe("Hello World Sample Plugin");

            host.RegisteredHighlighting.ShouldContain(r => r.FileExtension == ".ini" && r.XshdFilePath.EndsWith("Ini.xshd"));
            host.RegisteredToolsMenuCommands.ShouldContain(c => c.Header == "Say Hello (Sample Plugin)");
            host.LoggedMessages.ShouldContain(m => m.Contains("registered .ini highlighting and a Tools menu command"));

            // Unload before cleanup - the plugin's collectible ALC still has SamplePlugin.dll open, and
            // an unreleased memory-mapped file would make the Directory.Delete below fail on Windows.
            loader.TryUnload(loader.LoadedPlugins[0], out _);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally
        {
            try
            {
                Directory.Delete(pluginsDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort - see ScratchXunitProject/ScratchPluginProject for the same rationale.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string FindSamplePluginCsproj()
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

        var csprojPath = Path.Combine(directory.FullName, "samples", "SamplePlugin", "SamplePlugin.csproj");
        File.Exists(csprojPath).ShouldBeTrue($"expected to find {csprojPath}");
        return csprojPath;
    }

    private static string BuildSamplePlugin(string csprojPath)
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

        using var process = Process.Start(startInfo)!;
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'dotnet build' on SamplePlugin failed with exit code {process.ExitCode}.\n{stdOut}\n{stdErr}");
        }

        var projectDirectory = Path.GetDirectoryName(csprojPath)!;
        var dllPath = Directory.GetFiles(projectDirectory, "SamplePlugin.dll", SearchOption.AllDirectories)
            .Single(p => p.Contains(Path.Combine("bin", "Release")) && !p.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}"));
        return Path.GetDirectoryName(dllPath)!;
    }
}
