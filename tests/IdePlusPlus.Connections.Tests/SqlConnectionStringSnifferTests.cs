using IdePlusPlus.Connections;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Connections.Tests;

public sealed class SqlConnectionStringSnifferTests
{
    [Theory]
    [InlineData("Server=.;Database=Foo;Trusted_Connection=True;")]
    [InlineData("Data Source=myserver;Initial Catalog=Foo;")]
    [InlineData("Addr=tcp:myserver,1433;Database=Foo;")]
    public void LooksLikeSqlServer_ForAdoNetStyleStrings_IsTrue(string connectionString)
    {
        SqlConnectionStringSniffer.LooksLikeSqlServer(connectionString).ShouldBeTrue();
    }

    [Theory]
    [InlineData("postgres://user:pass@localhost:5432/db")]
    [InlineData("mongodb://localhost:27017/db")]
    [InlineData("mongodb+srv://cluster.example.net/db")]
    [InlineData("mysql://user:pass@localhost:3306/db")]
    public void LooksLikeSqlServer_ForOtherProviderSchemes_IsFalse(string connectionString)
    {
        SqlConnectionStringSniffer.LooksLikeSqlServer(connectionString).ShouldBeFalse();
    }

    [Fact]
    public void LooksLikeSqlServer_ForAnUnrecognizedString_IsFalse()
    {
        SqlConnectionStringSniffer.LooksLikeSqlServer("SomeOpaqueApiKey123").ShouldBeFalse();
    }
}
