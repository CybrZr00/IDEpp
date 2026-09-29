using IdePlusPlus.Connections;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Connections.Tests;

public sealed class AppSettingsConnectionReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdePlusPlusAppSettingsTests", Guid.NewGuid().ToString("N"));

    public AppSettingsConnectionReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string content)
    {
        var path = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Read_ConnectionStringsSection_ReturnsEachEntry()
    {
        var path = WriteFile("""
            {
              "ConnectionStrings": {
                "Default": "Server=.;Database=Foo;Trusted_Connection=True;",
                "Reporting": "Server=reports;Database=Bar;User Id=sa;Password=hunter2;"
              }
            }
            """);

        var results = AppSettingsConnectionReader.Read(path, "Api.csproj");

        results.Count.ShouldBe(2);
        results.ShouldContain(r => r.Name == "Default" && r.RawConnectionString.Contains("Foo"));
        results.ShouldContain(r => r.Name == "Reporting" && r.RawConnectionString.Contains("hunter2"));
        results.ShouldAllBe(r => r.SourceKind == ConnectionSourceKind.AppSettingsJson && r.ProjectPath == "Api.csproj");
    }

    [Fact]
    public void Read_NoConnectionStringsSection_ReturnsEmpty()
    {
        var path = WriteFile("""{ "Logging": {} }""");

        AppSettingsConnectionReader.Read(path, null).ShouldBeEmpty();
    }

    [Fact]
    public void Read_MalformedJson_ReturnsEmptyRatherThanThrowing()
    {
        var path = WriteFile("{ not valid json");

        AppSettingsConnectionReader.Read(path, null).ShouldBeEmpty();
    }

    [Fact]
    public void Read_MissingFile_ReturnsEmpty()
    {
        AppSettingsConnectionReader.Read(Path.Combine(_root, "nope.json"), null).ShouldBeEmpty();
    }
}
