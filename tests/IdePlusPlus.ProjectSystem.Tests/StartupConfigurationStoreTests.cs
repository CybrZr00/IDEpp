using IdePlusPlus.ProjectSystem;
using Shouldly;
using Xunit;

namespace IdePlusPlus.ProjectSystem.Tests;

public sealed class StartupConfigurationStoreTests : IDisposable
{
    private readonly string _solutionDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-startupconfig-{Guid.NewGuid()}");

    public StartupConfigurationStoreTests() => Directory.CreateDirectory(_solutionDirectory);

    public void Dispose() => Directory.Delete(_solutionDirectory, recursive: true);

    [Fact]
    public void SaveThenLoad_RoundTripsConfigurationsAndActiveName()
    {
        var configurations = new List<StartupConfiguration>
        {
            new("Default", [new StartupConfigurationEntry(@"C:\Api\Api.csproj", StartupAction.Start, "https")]),
            new("Api only", [
                new StartupConfigurationEntry(@"C:\Api\Api.csproj", StartupAction.Start, null),
                new StartupConfigurationEntry(@"C:\Worker\Worker.csproj", StartupAction.None, null),
            ]),
        };

        StartupConfigurationStore.Save(_solutionDirectory, configurations, "Api only");
        var (loaded, activeName) = StartupConfigurationStore.Load(_solutionDirectory);

        activeName.ShouldBe("Api only");
        loaded.Select(c => c.Name).ShouldBe(configurations.Select(c => c.Name));
        loaded.Select(c => c.Entries).ShouldBe(configurations.Select(c => c.Entries));
    }

    [Fact]
    public void Load_NoFileAndNoLegacyStore_SeedsEmptyDefaultConfiguration()
    {
        var (configurations, activeName) = StartupConfigurationStore.Load(_solutionDirectory);

        activeName.ShouldBe(StartupConfigurationStore.DefaultConfigurationName);
        configurations.Count.ShouldBe(1);
        configurations[0].Name.ShouldBe(StartupConfigurationStore.DefaultConfigurationName);
        configurations[0].Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Load_NoFileButLegacyMultiProjectStoreExists_SeedsDefaultFromIt()
    {
        StartupProjectsStore.Save(_solutionDirectory, [@"C:\Api\Api.csproj", @"C:\Worker\Worker.csproj"]);

        var (configurations, activeName) = StartupConfigurationStore.Load(_solutionDirectory);

        activeName.ShouldBe(StartupConfigurationStore.DefaultConfigurationName);
        configurations.Count.ShouldBe(1);
        configurations[0].Entries.ShouldBe([
            new StartupConfigurationEntry(@"C:\Api\Api.csproj", StartupAction.Start, null),
            new StartupConfigurationEntry(@"C:\Worker\Worker.csproj", StartupAction.Start, null),
        ]);
    }

    [Fact]
    public void Load_NoFileButLegacySingleProjectStoreExists_SeedsDefaultFromIt()
    {
        StartupProjectStore.Save(_solutionDirectory, @"C:\Api\Api.csproj");

        var (configurations, _) = StartupConfigurationStore.Load(_solutionDirectory);

        configurations[0].Entries.ShouldBe([new StartupConfigurationEntry(@"C:\Api\Api.csproj", StartupAction.Start, null)]);
    }

    [Fact]
    public void Load_MigrationWritesTheFile_SoLegacyStoresAreNotConsultedAgain()
    {
        StartupProjectStore.Save(_solutionDirectory, @"C:\Api\Api.csproj");
        StartupConfigurationStore.Load(_solutionDirectory);

        // Simulate the legacy choice changing after migration - Load must not notice, since it should
        // now be reading only the migrated startup-configurations.json.
        StartupProjectStore.Save(_solutionDirectory, @"C:\Worker\Worker.csproj");

        var (configurations, _) = StartupConfigurationStore.Load(_solutionDirectory);

        configurations[0].Entries.ShouldBe([new StartupConfigurationEntry(@"C:\Api\Api.csproj", StartupAction.Start, null)]);
    }

    [Fact]
    public void Load_StaleActiveNameNotAmongConfigurations_FallsBackToFirst()
    {
        var configurations = new List<StartupConfiguration> { new("Default", []), new("Api only", []) };
        StartupConfigurationStore.Save(_solutionDirectory, configurations, "Deleted configuration");

        var (_, activeName) = StartupConfigurationStore.Load(_solutionDirectory);

        activeName.ShouldBe("Default");
    }

    [Fact]
    public void Load_MalformedFile_FallsBackToFreshMigration()
    {
        var path = Path.Combine(_solutionDirectory, ".ideplusplus", "startup-configurations.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not valid json");

        var (configurations, activeName) = StartupConfigurationStore.Load(_solutionDirectory);

        activeName.ShouldBe(StartupConfigurationStore.DefaultConfigurationName);
        configurations.Count.ShouldBe(1);
    }
}
