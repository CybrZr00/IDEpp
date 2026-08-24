using Shouldly;
using Xunit;

namespace IdePlusPlus.Run.Tests;

public class RunServiceTests
{
    [Fact]
    public void BuildArguments_NoExtras_IsJustRunAndProject()
    {
        var arguments = RunService.BuildArguments("C:/repo/App.csproj", targetFramework: null, configuration: null, launchProfile: null);

        arguments.ShouldBe(["run", "--project", "C:/repo/App.csproj"]);
    }

    [Fact]
    public void BuildArguments_WithFrameworkAndConfiguration_AddsBothFlags()
    {
        var arguments = RunService.BuildArguments("App.csproj", targetFramework: "net10.0", configuration: "Release", launchProfile: null);

        arguments.ShouldBe(["run", "--project", "App.csproj", "--framework", "net10.0", "--configuration", "Release"]);
    }

    [Fact]
    public void BuildArguments_WithProjectProfile_AddsLaunchProfileFlag()
    {
        var launchProfile = new LaunchProfile("https", "Project", false, null, [], new Dictionary<string, string>());

        var arguments = RunService.BuildArguments("App.csproj", null, null, launchProfile);

        arguments.ShouldBe(["run", "--project", "App.csproj", "--launch-profile", "https"]);
    }

    [Fact]
    public void BuildArguments_WithNonProjectProfile_DoesNotAddLaunchProfileFlag()
    {
        var launchProfile = new LaunchProfile("IIS Express", "IISExpress", false, null, [], new Dictionary<string, string>());

        var arguments = RunService.BuildArguments("App.csproj", null, null, launchProfile);

        arguments.ShouldBe(["run", "--project", "App.csproj"]);
    }

    [Fact]
    public void BuildArguments_WithCommandLineArgs_AppendsAfterDoubleDashSeparator()
    {
        var launchProfile = new LaunchProfile("http", "Project", false, null, ["--seed", "42"], new Dictionary<string, string>());

        var arguments = RunService.BuildArguments("App.csproj", null, null, launchProfile);

        arguments.ShouldBe(["run", "--project", "App.csproj", "--launch-profile", "http", "--", "--seed", "42"]);
    }
}
