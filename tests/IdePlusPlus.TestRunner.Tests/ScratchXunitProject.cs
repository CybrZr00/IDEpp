using System.Diagnostics;

namespace IdePlusPlus.TestRunner.Tests;

/// <summary>
/// Creates a real, minimal xunit test project on disk in a scratch temp directory so tests can run
/// the actual `dotnet test` / `dotnet build` CLI end-to-end (mirrors GitServiceTests's and
/// NuGetServiceTests's "exercise the real tool, don't mock the process" philosophy) rather than
/// hand-crafting fake CLI output. The scratch project deliberately does NOT live under the repo's
/// Directory.Build.props/Directory.Packages.props tree, so its csproj pins its own package versions
/// (matching Directory.Packages.props's current pins) and its own TargetFramework.
/// </summary>
internal sealed class ScratchXunitProject : IDisposable
{
    public string ProjectDirectory { get; } = Path.Combine(Path.GetTempPath(), $"ideplusplus-testrunner-{Guid.NewGuid():N}");

    public string CsprojPath { get; }

    public ScratchXunitProject(string projectName, string sourceFileName, string sourceCode)
    {
        Directory.CreateDirectory(ProjectDirectory);

        CsprojPath = Path.Combine(ProjectDirectory, $"{projectName}.csproj");
        File.WriteAllText(CsprojPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="4.0.0" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(ProjectDirectory, sourceFileName), sourceCode);
    }

    /// <summary>Runs `dotnet build` synchronously so a later, timing-sensitive `dotnet test` call
    /// only has to do an incremental up-to-date check rather than a from-scratch restore+compile.</summary>
    public void Build()
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
        startInfo.ArgumentList.Add(CsprojPath);

        using var process = Process.Start(startInfo)!;
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'dotnet build' on the scratch project failed with exit code {process.ExitCode}.\n{stdOut}\n{stdErr}");
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(ProjectDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(ProjectDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a lingering file handle (e.g. from a killed testhost process) shouldn't
            // fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above.
        }
    }
}
