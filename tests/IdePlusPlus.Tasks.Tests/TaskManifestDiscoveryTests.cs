using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

public class TaskManifestDiscoveryTests
{
    private const string WithBuildScript = """{ "scripts": { "build": "tsc" } }""";

    [Fact]
    public void Discover_FindsManifestInTheSolutionDirectory()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WritePackageJson(WithBuildScript);

        var manifests = TaskManifestDiscovery.Discover(directory.Path, []);

        manifests.Select(m => m.Path).ShouldBe([manifestPath]);
        manifests[0].Tasks.Select(t => t.Name).ShouldBe(["build"]);
        manifests[0].Kind.ShouldBe(TaskManifestKind.NpmScripts);
    }

    [Fact]
    public void Discover_FindsManifestOneDirectoryDown()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WriteFile("ClientApp/package.json", WithBuildScript);

        TaskManifestDiscovery.Discover(directory.Path, []).Select(m => m.Path).ShouldBe([manifestPath]);
    }

    /// <summary>Deeper than the shallow scan goes - that depth is the solution tree's job, which is
    /// why the tree paths are a second source rather than a convenience.</summary>
    [Fact]
    public void Discover_ManifestDeeperThanTheScan_IsFoundOnlyViaTheTree()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WriteFile("src/Web/Client/package.json", WithBuildScript);

        TaskManifestDiscovery.Discover(directory.Path, []).ShouldBeEmpty();
        TaskManifestDiscovery.Discover(directory.Path, [manifestPath]).Select(m => m.Path).ShouldBe([manifestPath]);
    }

    [Fact]
    public void Discover_SameManifestFromBothSources_IsListedOnce()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WritePackageJson(WithBuildScript);

        TaskManifestDiscovery.Discover(directory.Path, [manifestPath]).Count.ShouldBe(1);
    }

    [Fact]
    public void Discover_SkipsNodeModules_FromBothSources()
    {
        using var directory = new TempDirectory();
        var dependencyManifest = directory.WriteFile("node_modules/left-pad/package.json", WithBuildScript);

        TaskManifestDiscovery.Discover(directory.Path, [dependencyManifest]).ShouldBeEmpty();
    }

    /// <summary>The SDK's default globs sweep node_modules into the solution tree, so a nested
    /// dependency manifest reaches discovery with the segment part-way through its path.</summary>
    [Fact]
    public void Discover_SkipsNodeModules_NestedDeepInAPath()
    {
        using var directory = new TempDirectory();
        var nested = directory.WriteFile("src/Web/node_modules/left-pad/dist/package.json", WithBuildScript);

        TaskManifestDiscovery.Discover(solutionDirectory: null, [nested]).ShouldBeEmpty();
    }

    [Fact]
    public void Discover_SkipsManifestsWithNoScripts()
    {
        using var directory = new TempDirectory();
        directory.WritePackageJson("""{ "name": "web", "version": "1.0.0" }""");

        TaskManifestDiscovery.Discover(directory.Path, []).ShouldBeEmpty();
    }

    [Fact]
    public void Discover_SkipsMalformedManifests()
    {
        using var directory = new TempDirectory();
        directory.WritePackageJson("{ not json");
        var good = directory.WriteFile("web/package.json", WithBuildScript);

        TaskManifestDiscovery.Discover(directory.Path, []).Select(m => m.Path).ShouldBe([good]);
    }

    [Fact]
    public void Discover_DetectsThePackageManagerPerManifest()
    {
        using var directory = new TempDirectory();
        directory.WritePackageJson(WithBuildScript);
        directory.WriteFile("pnpm-lock.yaml");

        TaskManifestDiscovery.Discover(directory.Path, []).Single().PackageManager.ShouldBe(NodePackageManager.Pnpm);
    }

    [Fact]
    public void Discover_NoSolutionDirectory_StillReadsTreePaths()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WriteFile("web/package.json", WithBuildScript);

        TaskManifestDiscovery.Discover(solutionDirectory: null, [manifestPath])
            .Select(m => m.Path)
            .ShouldBe([manifestPath]);
    }

    [Fact]
    public void Discover_NothingToFind_ReturnsEmpty()
    {
        using var directory = new TempDirectory();

        TaskManifestDiscovery.Discover(directory.Path, []).ShouldBeEmpty();
    }

    [Fact]
    public void Discover_NonexistentSolutionDirectory_ReturnsEmpty() =>
        TaskManifestDiscovery.Discover(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid()}"), [])
            .ShouldBeEmpty();

    [Fact]
    public void Discover_SkipsBuildOutputDirectories()
    {
        using var directory = new TempDirectory();
        directory.WriteFile("bin/package.json", WithBuildScript);
        directory.WriteFile("obj/package.json", WithBuildScript);

        TaskManifestDiscovery.Discover(directory.Path, []).ShouldBeEmpty();
    }
}
