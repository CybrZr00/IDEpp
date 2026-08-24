using Shouldly;
using Xunit;

namespace IdePlusPlus.Run.Tests;

public class LaunchSettingsLoaderTests
{
    [Fact]
    public void Load_NoLaunchSettingsFile_ReturnsNull()
    {
        using var project = new TestProject();
        LaunchSettingsLoader.Load(project.ProjectPath).ShouldBeNull();
    }

    [Fact]
    public void Load_NoProfileNameGiven_DefaultsToFirstProjectProfile_IgnoringIisExpress()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "iisSettings": { "iisExpress": { "applicationUrl": "http://localhost:10000" } },
              "profiles": {
                "IIS Express": { "commandName": "IISExpress", "launchBrowser": true },
                "http": {
                  "commandName": "Project",
                  "launchBrowser": true,
                  "applicationUrl": "http://localhost:5000",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
                },
                "https": {
                  "commandName": "Project",
                  "launchBrowser": false,
                  "applicationUrl": "https://localhost:5001;http://localhost:5000"
                }
              }
            }
            """);

        var profile = LaunchSettingsLoader.Load(project.ProjectPath);

        profile.ShouldNotBeNull();
        profile.Name.ShouldBe("http");
        profile.CommandName.ShouldBe("Project");
        profile.LaunchBrowser.ShouldBeTrue();
        profile.ApplicationUrl.ShouldBe("http://localhost:5000");
        profile.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"].ShouldBe("Development");
    }

    [Fact]
    public void Load_WithExplicitProfileName_ReturnsThatProfileEvenIfNotFirst()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "http": { "commandName": "Project", "applicationUrl": "http://localhost:5000" },
                "https": { "commandName": "Project", "applicationUrl": "https://localhost:5001" }
              }
            }
            """);

        var profile = LaunchSettingsLoader.Load(project.ProjectPath, "https");

        profile.ShouldNotBeNull();
        profile.Name.ShouldBe("https");
        profile.ApplicationUrl.ShouldBe("https://localhost:5001");
    }

    [Fact]
    public void Load_WithExplicitProfileName_CanReturnNonProjectProfile()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "IIS Express": { "commandName": "IISExpress", "launchBrowser": true }
              }
            }
            """);

        var profile = LaunchSettingsLoader.Load(project.ProjectPath, "IIS Express");

        profile.ShouldNotBeNull();
        profile.CommandName.ShouldBe("IISExpress");
    }

    [Fact]
    public void Load_WithUnknownExplicitProfileName_ReturnsNull()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "http": { "commandName": "Project" }
              }
            }
            """);

        LaunchSettingsLoader.Load(project.ProjectPath, "does-not-exist").ShouldBeNull();
    }

    [Fact]
    public void Load_ParsesCommandLineArgsIntoArgvList()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "http": { "commandName": "Project", "commandLineArgs": "--seed 42 \"path with spaces\"" }
              }
            }
            """);

        var profile = LaunchSettingsLoader.Load(project.ProjectPath);

        profile.ShouldNotBeNull();
        profile.CommandLineArgs.ShouldBe(["--seed", "42", "path with spaces"]);
    }

    [Fact]
    public void Load_MissingCommandLineArgs_ReturnsEmptyList()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "http": { "commandName": "Project" }
              }
            }
            """);

        var profile = LaunchSettingsLoader.Load(project.ProjectPath);

        profile.ShouldNotBeNull();
        profile.CommandLineArgs.ShouldBeEmpty();
    }

    [Fact]
    public void LoadProfileNames_ReturnsAllProfilesRegardlessOfCommandName()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "IIS Express": { "commandName": "IISExpress" },
                "http": { "commandName": "Project" },
                "https": { "commandName": "Project" }
              }
            }
            """);

        var names = LaunchSettingsLoader.LoadProfileNames(project.ProjectPath);

        names.ShouldBe(
        [
            ("IIS Express", "IISExpress"),
            ("http", "Project"),
            ("https", "Project"),
        ]);
    }

    [Fact]
    public void LoadProfileNames_NoLaunchSettingsFile_ReturnsEmptyList()
    {
        using var project = new TestProject();
        LaunchSettingsLoader.LoadProfileNames(project.ProjectPath).ShouldBeEmpty();
    }

    [Fact]
    public void Load_ApplicationUrl_BecomesAspNetCoreUrlsEnvironmentVariable()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "http": { "commandName": "Project", "applicationUrl": "http://localhost:5000" }
              }
            }
            """);

        var profile = LaunchSettingsLoader.Load(project.ProjectPath);

        profile.ShouldNotBeNull();
        profile.EnvironmentVariables["ASPNETCORE_URLS"].ShouldBe("http://localhost:5000");
    }

    [Fact]
    public void Load_ExplicitAspNetCoreUrls_IsNotOverwrittenByApplicationUrl()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:5000",
                  "environmentVariables": { "ASPNETCORE_URLS": "http://localhost:9999" }
                }
              }
            }
            """);

        var profile = LaunchSettingsLoader.Load(project.ProjectPath);

        profile.ShouldNotBeNull();
        profile.EnvironmentVariables["ASPNETCORE_URLS"].ShouldBe("http://localhost:9999");
    }

    [Fact]
    public void Load_NoProjectProfiles_ReturnsNull()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("""
            {
              "profiles": {
                "IIS Express": { "commandName": "IISExpress" }
              }
            }
            """);

        LaunchSettingsLoader.Load(project.ProjectPath).ShouldBeNull();
    }

    [Fact]
    public void Load_MalformedJson_ReturnsNullRatherThanThrowing()
    {
        using var project = new TestProject();
        project.WriteLaunchSettings("{ not valid json");

        LaunchSettingsLoader.Load(project.ProjectPath).ShouldBeNull();
    }

    private sealed class TestProject : IDisposable
    {
        private readonly string _directory;

        public TestProject()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"launchsettings-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(_directory);
            ProjectPath = Path.Combine(_directory, "TestProject.csproj");
            File.WriteAllText(ProjectPath, "<Project />");
        }

        public string ProjectPath { get; }

        public void WriteLaunchSettings(string json)
        {
            var propertiesDirectory = Path.Combine(_directory, "Properties");
            Directory.CreateDirectory(propertiesDirectory);
            File.WriteAllText(Path.Combine(propertiesDirectory, "launchSettings.json"), json);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
