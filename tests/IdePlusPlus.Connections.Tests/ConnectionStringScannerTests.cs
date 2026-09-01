using IdePlusPlus.Connections;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Connections.Tests;

public sealed class ConnectionStringScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdePlusPlusScannerTests", Guid.NewGuid().ToString("N"));

    public ConnectionStringScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteProjectFile(string projectDirectoryName, string fileName, string content)
    {
        var projectDirectory = Path.Combine(_root, projectDirectoryName);
        Directory.CreateDirectory(projectDirectory);
        var path = Path.Combine(projectDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Scan_FindsAppSettingsJsonAmongTheSolutionFileList()
    {
        var appSettingsPath = WriteProjectFile(
            "Api", "appsettings.json", """{"ConnectionStrings":{"Default":"Server=.;Database=Foo;"}}""");
        var projectPath = Path.Combine(_root, "Api", "Api.csproj");

        var results = ConnectionStringScanner.Scan(
            [appSettingsPath], [(projectPath, Path.Combine(_root, "Api"))]);

        results.ShouldHaveSingleItem().ProjectPath.ShouldBe(projectPath);
    }

    [Fact]
    public void Scan_FindsWebConfigAmongTheSolutionFileList()
    {
        var webConfigPath = WriteProjectFile(
            "WebApp", "web.config",
            "<configuration><connectionStrings><add name=\"Default\" connectionString=\"Server=.;\" /></connectionStrings></configuration>");

        var results = ConnectionStringScanner.Scan([webConfigPath], []);

        results.ShouldHaveSingleItem().SourceKind.ShouldBe(ConnectionSourceKind.WebConfig);
    }

    [Fact]
    public void Scan_IgnoresUnrelatedFilesInTheSolutionFileList()
    {
        var randomFile = WriteProjectFile("Api", "Program.cs", "// nothing here");

        ConnectionStringScanner.Scan([randomFile], []).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_ProbesEachProjectDirectoryDirectlyForADotEnvFile()
    {
        var projectDirectory = Path.Combine(_root, "Api");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, ".env"), "DB_CONNECTION=Server=.;Database=Foo;\n");
        var projectPath = Path.Combine(projectDirectory, "Api.csproj");

        // .env is deliberately NOT in solutionFilePaths - it's gitignored and typically not an
        // MSBuild-visible item, which is exactly why the scanner has to probe for it directly.
        var results = ConnectionStringScanner.Scan([], [(projectPath, projectDirectory)]);

        results.ShouldHaveSingleItem().SourceKind.ShouldBe(ConnectionSourceKind.DotEnv);
    }

    [Fact]
    public void Scan_AttributesAFileToTheDeepestMatchingProjectDirectory()
    {
        var outerProjectDir = Path.Combine(_root, "Outer");
        var innerProjectDir = Path.Combine(_root, "Outer", "Inner");
        Directory.CreateDirectory(innerProjectDir);
        var appSettingsPath = Path.Combine(innerProjectDir, "appsettings.json");
        File.WriteAllText(appSettingsPath, """{"ConnectionStrings":{"Default":"Server=.;"}}""");

        var results = ConnectionStringScanner.Scan(
            [appSettingsPath],
            [
                (Path.Combine(outerProjectDir, "Outer.csproj"), outerProjectDir),
                (Path.Combine(innerProjectDir, "Inner.csproj"), innerProjectDir),
            ]);

        results.ShouldHaveSingleItem().ProjectPath.ShouldBe(Path.Combine(innerProjectDir, "Inner.csproj"));
    }
}
