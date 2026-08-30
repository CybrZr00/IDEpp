using IdePlusPlus.Build;
using IdePlusPlus.Publish;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Publish.Tests;

/// <summary>
/// Runs a real `dotnet publish` against a scratch project, for the same reason BuildServiceTests runs
/// a real build: what is worth verifying is that the command line IDE++ produces is one the SDK
/// actually accepts, and a mocked process would assert nothing about that.
///
/// The .csproj is written out by hand rather than via `dotnet new` so no Directory.Build.props from
/// this repo leaks into it.
/// </summary>
public sealed class PublishServiceTests : IDisposable
{
    private const string TargetFramework = "net10.0";

    private readonly string _projectDirectory;
    private readonly string _projectPath;
    private readonly PublishService _publishService = new(new BuildService());

    public PublishServiceTests()
    {
        _projectDirectory = Path.Combine(Path.GetTempPath(), "ideplusplus-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectDirectory);

        _projectPath = Path.Combine(_projectDirectory, "Scratch.csproj");
        File.WriteAllText(_projectPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TargetFramework}</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(_projectDirectory, "Program.cs"), "System.Console.WriteLine(\"scratch\");");
    }

    [Fact]
    public async Task PublishAsync_AFolderProfile_SucceedsAndWritesToThePublishDirFromTheProfile()
    {
        var profile = PublishProfile.CreateFolderDefault("scratch", TargetFramework) with { PublishDir = "dist/" };
        PublishProfileStore.Save(_projectPath, profile);

        var exitCode = await _publishService.PublishAsync(_projectPath, profile, _ => { });

        exitCode.ShouldBe(0);
        File.Exists(Path.Combine(_projectDirectory, "dist", "Scratch.dll")).ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_WhenItFinishes_ClearsIsRunning()
    {
        var profile = PublishProfile.CreateFolderDefault("scratch", TargetFramework);
        PublishProfileStore.Save(_projectPath, profile);

        await _publishService.PublishAsync(_projectPath, profile, _ => { });

        _publishService.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task PublishAsync_StreamsOutputLines()
    {
        var profile = PublishProfile.CreateFolderDefault("scratch", TargetFramework);
        PublishProfileStore.Save(_projectPath, profile);
        var lines = new List<string>();

        await _publishService.PublishAsync(_projectPath, profile, line => { lock (lines) { lines.Add(line); } });

        lines.ShouldNotBeEmpty();
    }

    /// <summary>An MSDeploy or ClickOnce profile is listable but not runnable, and the refusal has to
    /// arrive as a message the caller can show rather than a mangled dotnet invocation.</summary>
    [Fact]
    public async Task PublishAsync_AnUnsupportedProfile_ThrowsWithoutRunningAnything()
    {
        var profile = PublishProfileReader.Parse(
            "<Project><PropertyGroup><WebPublishMethod>MSDeploy</WebPublishMethod></PropertyGroup></Project>",
            "azure")!;

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => _publishService.PublishAsync(_projectPath, profile, _ => { }));

        exception.Message.ShouldContain("azure");
        _publishService.IsRunning.ShouldBeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a leftover temp folder is not worth failing a passing test over.
        }
    }
}
