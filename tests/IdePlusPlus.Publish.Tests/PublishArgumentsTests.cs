using IdePlusPlus.Publish;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Publish.Tests;

public sealed class PublishArgumentsTests
{
    private static readonly string ProjectDirectory = Path.Combine("C:", "src", "App");
    private static readonly string ProjectPath = Path.Combine(ProjectDirectory, "App.csproj");

    [Fact]
    public void Build_AFolderProfile_ReferencesTheProfileRatherThanRepeatingItsSettings()
    {
        var profile = PublishProfile.CreateFolderDefault("win-x64") with
        {
            RuntimeIdentifier = "win-x64",
            SelfContained = true,
            PublishSingleFile = true,
            PublishDir = "out/",
        };

        var arguments = PublishArguments.Build(ProjectPath, profile);

        arguments.ShouldBe(["publish", ProjectPath, "-p:PublishProfile=win-x64", "--configuration", "Release"]);
    }

    [Fact]
    public void Build_AContainerProfile_AddsThePublishContainerTarget()
    {
        var profile = PublishProfile.CreateContainerDefault("image", "App");

        PublishArguments.Build(ProjectPath, profile).ShouldBe(
            ["publish", ProjectPath, "-t:PublishContainer", "-p:PublishProfile=image", "--configuration", "Release"]);
    }

    /// <summary>The profile is imported per-TFM, so a multi-targeted project has to be told which one
    /// before the import happens.</summary>
    [Fact]
    public void Build_WithATargetFramework_PassesItThrough()
    {
        var arguments = PublishArguments.Build(ProjectPath, PublishProfile.CreateFolderDefault("win-x64", "net10.0-windows"));

        arguments.ShouldContain("--framework");
        arguments.ShouldContain("net10.0-windows");
    }

    [Fact]
    public void Build_WithNoFrameworkAnywhere_OmitsTheFrameworkSwitch() =>
        PublishArguments.Build(ProjectPath, PublishProfile.CreateFolderDefault("win-x64"))
            .ShouldNotContain("--framework");

    /// <summary>A framework named by the profile wins over the one the solution tree knows about.</summary>
    [Fact]
    public void Build_WhenTheProfileNamesAFramework_PrefersItOverTheProjectDefault() =>
        PublishArguments.Build(ProjectPath, PublishProfile.CreateFolderDefault("win-x64", "net8.0"), "net10.0")
            .ShouldContain("net8.0");

    [Fact]
    public void Build_WhenOnlyTheProjectKnowsTheFramework_UsesThat() =>
        PublishArguments.Build(ProjectPath, PublishProfile.CreateFolderDefault("win-x64"), "net10.0")
            .ShouldContain("net10.0");

    /// <summary>The configuration comes from the profile, not the IDE Debug/Release picker -
    /// publishing Release while debugging Debug is the normal case.</summary>
    [Fact]
    public void Build_UsesTheConfigurationFromTheProfile()
    {
        var profile = PublishProfile.CreateFolderDefault("staging") with { Configuration = "Debug" };

        var arguments = PublishArguments.Build(ProjectPath, profile);

        arguments[arguments.IndexOf("--configuration") + 1].ShouldBe("Debug");
    }

    [Fact]
    public void ResolvePublishDirectory_WithAnExplicitPublishDir_ResolvesItAgainstTheProject()
    {
        var profile = PublishProfile.CreateFolderDefault("win-x64") with { PublishDir = "dist/app/" };

        PublishArguments.ResolvePublishDirectory(ProjectPath, profile)
            .ShouldBe(Path.GetFullPath(Path.Combine(ProjectDirectory, "dist", "app")));
    }

    [Fact]
    public void ResolvePublishDirectory_WithNoPublishDir_UsesTheSdkDefaultLayout() =>
        PublishArguments.ResolvePublishDirectory(ProjectPath, PublishProfile.CreateFolderDefault("win-x64", "net10.0"))
            .ShouldBe(Path.GetFullPath(Path.Combine(ProjectDirectory, "bin", "Release", "net10.0", "publish")));

    [Fact]
    public void ResolvePublishDirectory_WithARuntimeIdentifier_IncludesTheRidSegment()
    {
        var profile = PublishProfile.CreateFolderDefault("win-x64", "net10.0") with { RuntimeIdentifier = "win-x64" };

        PublishArguments.ResolvePublishDirectory(ProjectPath, profile)
            .ShouldBe(Path.GetFullPath(Path.Combine(ProjectDirectory, "bin", "Release", "net10.0", "win-x64", "publish")));
    }

    [Fact]
    public void ResolvePublishDirectory_WithNothingToGoOn_ReturnsNull() =>
        PublishArguments.ResolvePublishDirectory(ProjectPath, PublishProfile.CreateFolderDefault("win-x64"))
            .ShouldBeNull();
}
