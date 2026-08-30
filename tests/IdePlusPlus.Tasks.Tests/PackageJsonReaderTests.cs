using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

public class PackageJsonReaderTests
{
    [Fact]
    public void Read_MissingFile_ReturnsNull() =>
        PackageJsonReader.Read(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid()}", "package.json"))
            .ShouldBeNull();

    [Fact]
    public void Read_MalformedJson_ReturnsNull()
    {
        using var directory = new TempDirectory();
        var path = directory.WritePackageJson("{ not json at all");

        PackageJsonReader.Read(path).ShouldBeNull();
    }

    [Fact]
    public void Read_RootIsNotAnObject_ReturnsNull()
    {
        using var directory = new TempDirectory();
        var path = directory.WritePackageJson("[1, 2, 3]");

        PackageJsonReader.Read(path).ShouldBeNull();
    }

    [Fact]
    public void Read_ReturnsScriptsInFileOrder_WithTheirCommandText()
    {
        using var directory = new TempDirectory();
        var path = directory.WritePackageJson("""
            {
              "name": "web",
              "scripts": {
                "build": "gulp build",
                "test": "jest --ci"
              }
            }
            """);

        var contents = PackageJsonReader.Read(path);

        contents.ShouldNotBeNull();
        contents.Scripts.Select(s => s.Name).ShouldBe(["build", "test"]);
        contents.Scripts[0].Command.ShouldBe("gulp build");
        contents.Scripts[0].ManifestPath.ShouldBe(path);
        contents.Scripts[0].ManifestKind.ShouldBe(TaskManifestKind.NpmScripts);
    }

    [Fact]
    public void Read_NoScriptsSection_ReturnsEmptyScripts()
    {
        using var directory = new TempDirectory();
        var path = directory.WritePackageJson("""{ "name": "web", "version": "1.0.0" }""");

        var contents = PackageJsonReader.Read(path);

        contents.ShouldNotBeNull();
        contents.Scripts.ShouldBeEmpty();
    }

    /// <summary>npm ignores non-string script values; offering one as runnable would list a task
    /// that cannot start.</summary>
    [Fact]
    public void Read_SkipsNonStringScriptValues()
    {
        using var directory = new TempDirectory();
        var path = directory.WritePackageJson("""
            {
              "scripts": {
                "build": "tsc",
                "broken": { "nested": true },
                "alsoBroken": 42
              }
            }
            """);

        var contents = PackageJsonReader.Read(path);

        contents.ShouldNotBeNull();
        contents.Scripts.Select(s => s.Name).ShouldBe(["build"]);
    }

    [Fact]
    public void Read_ReadsPackageManagerField()
    {
        using var directory = new TempDirectory();
        var path = directory.WritePackageJson("""
            { "packageManager": "pnpm@8.6.0", "scripts": { "build": "tsc" } }
            """);

        PackageJsonReader.Read(path)!.PackageManagerField.ShouldBe("pnpm@8.6.0");
    }

    [Fact]
    public void Read_NoPackageManagerField_ReturnsNullField()
    {
        using var directory = new TempDirectory();
        var path = directory.WritePackageJson("""{ "scripts": { "build": "tsc" } }""");

        PackageJsonReader.Read(path)!.PackageManagerField.ShouldBeNull();
    }
}
