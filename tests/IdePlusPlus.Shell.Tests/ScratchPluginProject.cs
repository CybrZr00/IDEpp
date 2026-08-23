using System.Diagnostics;
using IdePlusPlus.PluginContracts;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// Compiles a real, minimal IIdePlugin implementation to its own small assembly via the actual
/// `dotnet build` CLI, in a scratch temp directory outside the repo's Directory.Build.props tree -
/// mirrors ScratchXunitProject's (IdePlusPlus.TestRunner.Tests) "exercise the real tool, don't fake
/// it" philosophy. A hand-rolled fake assembly (e.g. via Reflection.Emit) wouldn't exercise the thing
/// that actually matters here: that a plugin DLL produced by a real, independent compilation - with
/// its own copy of the IdePlusPlus.PluginContracts assembly reference resolved at its own build time -
/// still gets the host's exact IIdePlugin/IPluginHost runtime types once loaded through
/// PluginLoadContext, not a second, incompatible copy.
///
/// The scratch project references the literal already-built IdePlusPlus.PluginContracts.dll (via a
/// HintPath to <c>typeof(IIdePlugin).Assembly.Location</c>) rather than the .csproj, since the scratch
/// project lives outside the repo's Directory.Build.props/Directory.Packages.props tree and a
/// ProjectReference there would need its own copy of every MSBuild prop this repo relies on.
/// </summary>
internal sealed class ScratchPluginProject : IDisposable
{
    public string ProjectDirectory { get; } = Path.Combine(Path.GetTempPath(), $"ideplusplus-plugin-{Guid.NewGuid():N}");

    private readonly string _csprojPath;
    private readonly string _projectName;

    public ScratchPluginProject(string projectName, string sourceCode)
    {
        _projectName = projectName;
        Directory.CreateDirectory(ProjectDirectory);

        var pluginContractsDllPath = typeof(IIdePlugin).Assembly.Location;

        _csprojPath = Path.Combine(ProjectDirectory, $"{projectName}.csproj");
        File.WriteAllText(_csprojPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0-windows</TargetFramework>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>

              <ItemGroup>
                <Reference Include="IdePlusPlus.PluginContracts">
                  <HintPath>{pluginContractsDllPath}</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(ProjectDirectory, $"{projectName}.cs"), sourceCode);
    }

    /// <summary>Runs `dotnet build` for real and returns the path to the built plugin DLL.</summary>
    public string Build()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ProjectDirectory,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(_csprojPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");

        using var process = Process.Start(startInfo)!;
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'dotnet build' on the scratch plugin project failed with exit code {process.ExitCode}.\n{stdOut}\n{stdErr}");
        }

        // Exact filename match under bin/Release, excluding the "ref/" reference-assembly copy the
        // SDK also produces alongside the real implementation DLL.
        var dllPath = Directory.GetFiles(ProjectDirectory, $"{_projectName}.dll", SearchOption.AllDirectories)
            .Single(p => p.Contains(Path.Combine("bin", "Release")) && !p.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}"));
        return dllPath;
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(ProjectDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(ProjectDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a lingering file handle (e.g. from the still-unloading plugin ALC under
            // test) shouldn't fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above.
        }
    }
}
