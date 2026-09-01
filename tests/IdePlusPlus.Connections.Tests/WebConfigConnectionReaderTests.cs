using IdePlusPlus.Connections;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Connections.Tests;

public sealed class WebConfigConnectionReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdePlusPlusWebConfigTests", Guid.NewGuid().ToString("N"));

    public WebConfigConnectionReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private const string SampleXml = """
        <?xml version="1.0"?>
        <configuration>
          <connectionStrings>
            <add name="Default" connectionString="Data Source=.;Initial Catalog=Foo;Integrated Security=True" providerName="System.Data.SqlClient" />
            <add name="Reporting" connectionString="Server=reports;User ID=sa;Password=hunter2;" />
          </connectionStrings>
        </configuration>
        """;

    [Fact]
    public void Read_WebConfig_ReturnsEachEntryAsWebConfigKind()
    {
        var path = WriteFile("web.config", SampleXml);

        var results = WebConfigConnectionReader.Read(path, "Api.csproj");

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.SourceKind == ConnectionSourceKind.WebConfig);
        results.ShouldContain(r => r.Name == "Default" && r.RawConnectionString.Contains("Foo"));
    }

    [Fact]
    public void Read_AppConfig_ReturnsEachEntryAsAppConfigKind()
    {
        var path = WriteFile("app.config", SampleXml);

        var results = WebConfigConnectionReader.Read(path, "Api.csproj");

        results.ShouldAllBe(r => r.SourceKind == ConnectionSourceKind.AppConfig);
    }

    [Fact]
    public void Read_NoConnectionStringsElement_ReturnsEmpty()
    {
        var path = WriteFile("web.config", "<configuration><appSettings /></configuration>");

        WebConfigConnectionReader.Read(path, null).ShouldBeEmpty();
    }

    [Fact]
    public void Read_MalformedXml_ReturnsEmptyRatherThanThrowing()
    {
        var path = WriteFile("web.config", "<configuration><unterminated>");

        WebConfigConnectionReader.Read(path, null).ShouldBeEmpty();
    }

    [Fact]
    public void Read_MissingFile_ReturnsEmpty()
    {
        WebConfigConnectionReader.Read(Path.Combine(_root, "nope.config"), null).ShouldBeEmpty();
    }
}
