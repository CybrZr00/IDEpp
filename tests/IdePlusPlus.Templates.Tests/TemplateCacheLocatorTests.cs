using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class TemplateCacheLocatorTests
{
    [Fact]
    public void BuildsTheCachePathTheTemplateEngineUses() =>
        TemplateCacheLocator.CachePathFor(@"C:\Users\someone", "10.0.301")
            .ShouldBe(Path.Combine(@"C:\Users\someone", ".templateengine", "dotnetcli", "10.0.301", "templatecache.json"));

    /// <summary>Ordinal sorting puts 10.0.99 above 10.0.103, which would pick a months-old cache on a
    /// machine that has both.</summary>
    [Fact]
    public void OrdersSdkVersionsNumericallyNotAsText() =>
        TemplateCacheLocator.OrderByVersionDescending(["10.0.99", "10.0.103", "10.0.301", "9.0.100"])
            .ShouldBe(["10.0.301", "10.0.103", "10.0.99", "9.0.100"]);

    [Fact]
    public void OrdersTheRealSideBySideInstall() =>
        TemplateCacheLocator.OrderByVersionDescending(["10.0.103", "10.0.201", "10.0.301"])
            .ShouldBe(["10.0.301", "10.0.201", "10.0.103"]);

    /// <summary>A released SDK beats a pre-release of the same version.</summary>
    [Fact]
    public void PrefersAReleasedVersionOverAPreRelease() =>
        TemplateCacheLocator.OrderByVersionDescending(["10.0.300-preview.2", "10.0.300"])
            .ShouldBe(["10.0.300", "10.0.300-preview.2"]);

    /// <summary>Anything unparseable sorts last rather than throwing - a stray directory under
    /// .templateengine must not stop the newest real cache being found.</summary>
    [Fact]
    public void UnparseableNamesSortLast() =>
        TemplateCacheLocator.OrderByVersionDescending(["not-a-version", "10.0.301"])
            .ShouldBe(["10.0.301", "not-a-version"]);

    [Fact]
    public void OrderingAnEmptyListIsEmpty() =>
        TemplateCacheLocator.OrderByVersionDescending([]).ShouldBeEmpty();

    /// <summary>DOTNET_CLI_HOME is what makes this testable without touching the real profile - and
    /// what the template engine itself honors, verified by watching it build a cache under one.</summary>
    [Fact]
    public async Task FindsNothingUnderAnEmptyCliHome()
    {
        using var directory = new TempDirectory();
        using var _ = new EnvironmentVariable("DOTNET_CLI_HOME", directory.Path);

        TemplateCacheLocator.CliHome.ShouldBe(directory.Path);
        (await new TemplateCacheLocator().LocateAsync(directory.Path)).ShouldBeNull();
    }

    [Fact]
    public async Task FallsBackToTheNewestCacheOnDiskWhenTheRunningSdkHasNone()
    {
        using var directory = new TempDirectory();
        using var _ = new EnvironmentVariable("DOTNET_CLI_HOME", directory.Path);

        // A version this machine will never report, so the lookup has to fall back rather than match.
        directory.WriteFile(".templateengine/dotnetcli/3.1.100/templatecache.json", "{}");
        directory.WriteFile(".templateengine/dotnetcli/4.0.100/templatecache.json", "{}");

        (await new TemplateCacheLocator().LocateAsync(directory.Path))
            .ShouldBe(TemplateCacheLocator.CachePathFor(directory.Path, "4.0.100"));
    }

    /// <summary>An SDK directory with no cache file in it is not a candidate - the fallback has to
    /// keep looking rather than returning a path that doesn't exist.</summary>
    [Fact]
    public async Task SkipsAnSdkDirectoryWithNoCacheFile()
    {
        using var directory = new TempDirectory();
        using var _ = new EnvironmentVariable("DOTNET_CLI_HOME", directory.Path);

        directory.CreateDirectory(".templateengine/dotnetcli/4.0.100");
        directory.WriteFile(".templateengine/dotnetcli/3.1.100/templatecache.json", "{}");

        (await new TemplateCacheLocator().LocateAsync(directory.Path))
            .ShouldBe(TemplateCacheLocator.CachePathFor(directory.Path, "3.1.100"));
    }
}

/// <summary>Sets an environment variable for the duration of one test and puts back whatever was
/// there. Process-scoped, so it does not touch the user's own environment.</summary>
internal sealed class EnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _original;

    public EnvironmentVariable(string name, string? value)
    {
        _name = name;
        _original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
}
