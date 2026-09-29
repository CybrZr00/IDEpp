using IdePlusPlus.Connections;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Connections.Tests;

public sealed class DotEnvConnectionReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdePlusPlusDotEnvTests", Guid.NewGuid().ToString("N"));

    public DotEnvConnectionReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string content)
    {
        var path = Path.Combine(_root, ".env");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Read_KeyContainingConn_IsSurfaced()
    {
        var path = WriteFile("DATABASE_CONNECTION_STRING=Server=.;Database=Foo;\n");

        var results = DotEnvConnectionReader.Read(path, "Api.csproj");

        results.ShouldHaveSingleItem().Name.ShouldBe("DATABASE_CONNECTION_STRING");
        results[0].RawConnectionString.ShouldBe("Server=.;Database=Foo;");
        results[0].SourceKind.ShouldBe(ConnectionSourceKind.DotEnv);
    }

    [Fact]
    public void Read_UnrelatedKeys_AreIgnored()
    {
        var path = WriteFile("API_KEY=abc123\nPORT=5000\n");

        DotEnvConnectionReader.Read(path, null).ShouldBeEmpty();
    }

    [Fact]
    public void Read_CommentsAndBlankLines_AreSkipped()
    {
        var path = WriteFile("# a comment\n\nCONN_STRING=Server=.;\n");

        DotEnvConnectionReader.Read(path, null).ShouldHaveSingleItem();
    }

    [Fact]
    public void Read_QuotedValue_StripsQuotes()
    {
        var path = WriteFile("CONN_STRING=\"Server=.;Database=Foo;\"\n");

        DotEnvConnectionReader.Read(path, null)[0].RawConnectionString.ShouldBe("Server=.;Database=Foo;");
    }

    [Fact]
    public void Read_MissingFile_ReturnsEmpty()
    {
        DotEnvConnectionReader.Read(Path.Combine(_root, ".env"), null).ShouldBeEmpty();
    }
}
