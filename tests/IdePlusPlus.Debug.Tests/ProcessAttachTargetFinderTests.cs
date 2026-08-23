using IdePlusPlus.Debug;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Debug.Tests;

public sealed class ProcessAttachTargetFinderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-attachtarget-{Guid.NewGuid()}");

    public ProcessAttachTargetFinderTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public void LooksLikeDotNetProcess_NamedDotnet_ReturnsTrueRegardlessOfModulePath()
    {
        ProcessAttachTargetFinder.LooksLikeDotNetProcess("dotnet", null).ShouldBeTrue();
        ProcessAttachTargetFinder.LooksLikeDotNetProcess("dotnet", @"C:\some\arbitrary\path\dotnet.exe").ShouldBeTrue();
    }

    [Fact]
    public void LooksLikeDotNetProcess_ModuleDirectoryHasRealHostfxrSibling_ReturnsTrue()
    {
        var mainModulePath = Path.Combine(_tempDirectory, "MyApp.exe");
        File.WriteAllText(mainModulePath, "");
        File.WriteAllText(Path.Combine(_tempDirectory, "hostfxr.dll"), "");

        ProcessAttachTargetFinder.LooksLikeDotNetProcess("MyApp", mainModulePath).ShouldBeTrue();
    }

    [Fact]
    public void LooksLikeDotNetProcess_ModuleDirectoryHasRealCoreclrSibling_ReturnsTrue()
    {
        var mainModulePath = Path.Combine(_tempDirectory, "MyApp.exe");
        File.WriteAllText(mainModulePath, "");
        File.WriteAllText(Path.Combine(_tempDirectory, "coreclr.dll"), "");

        ProcessAttachTargetFinder.LooksLikeDotNetProcess("MyApp", mainModulePath).ShouldBeTrue();
    }

    [Fact]
    public void LooksLikeDotNetProcess_ApphostWithMatchingDepsJson_ReturnsTrue()
    {
        var mainModulePath = Path.Combine(_tempDirectory, "MyApp.exe");
        File.WriteAllText(mainModulePath, "");
        File.WriteAllText(Path.Combine(_tempDirectory, "MyApp.deps.json"), "");

        ProcessAttachTargetFinder.LooksLikeDotNetProcess("MyApp", mainModulePath).ShouldBeTrue();
    }

    [Fact]
    public void LooksLikeDotNetProcess_UnrelatedProcessWithNoDotNetSiblings_ReturnsFalse()
    {
        var mainModulePath = Path.Combine(_tempDirectory, "notepad.exe");
        File.WriteAllText(mainModulePath, "");

        ProcessAttachTargetFinder.LooksLikeDotNetProcess("notepad", mainModulePath).ShouldBeFalse();
    }

    [Fact]
    public void LooksLikeDotNetProcess_NullMainModulePathAndNonDotnetName_ReturnsFalseWithoutThrowing()
    {
        ProcessAttachTargetFinder.LooksLikeDotNetProcess("notepad", null).ShouldBeFalse();
    }
}
