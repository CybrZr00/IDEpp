using IdePlusPlus.ProjectSystem;
using Shouldly;
using Xunit;

namespace IdePlusPlus.ProjectSystem.Tests;

/// <summary>
/// Exercises TestProjectDetector against a real, evaluated Microsoft.Build.Evaluation.Project
/// (via Project.FromFile against a scratch .csproj on disk), mirroring SolutionLoader's own
/// evaluate-then-unload usage rather than mocking the MSBuild object model.
/// </summary>
public sealed class TestProjectDetectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ideplusplus-testdetector-{Guid.NewGuid():N}");

    public TestProjectDetectorTests()
    {
        // Same registration SolutionLoader.Load performs before touching any Microsoft.Build.*
        // type - reused here rather than duplicated, and safe to call more than once.
        SolutionLoader.EnsureMSBuildRegistered();
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - shouldn't fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above.
        }
    }

    [Fact]
    public void IsTestProject_ForAProjectWithAnXunitPackageReference_ReturnsTrue()
    {
        var csprojPath = WriteCsproj("WithXunit.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """);

        AssertIsTestProject(csprojPath, expected: true);
    }

    [Fact]
    public void IsTestProject_ForAPlainProjectWithNoTestRelatedPackageReferences_ReturnsFalse()
    {
        var csprojPath = WriteCsproj("PlainProject.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
              </ItemGroup>
            </Project>
            """);

        AssertIsTestProject(csprojPath, expected: false);
    }

    private static void AssertIsTestProject(string csprojPath, bool expected)
    {
        var evaluated = Microsoft.Build.Evaluation.Project.FromFile(
            csprojPath,
            new Microsoft.Build.Definition.ProjectOptions());
        try
        {
            TestProjectDetector.IsTestProject(evaluated).ShouldBe(expected);
        }
        finally
        {
            Microsoft.Build.Evaluation.ProjectCollection.GlobalProjectCollection.UnloadProject(evaluated);
        }
    }

    private string WriteCsproj(string fileName, string content)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
