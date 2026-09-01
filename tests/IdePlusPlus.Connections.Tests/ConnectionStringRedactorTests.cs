using IdePlusPlus.Connections;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Connections.Tests;

public sealed class ConnectionStringRedactorTests
{
    [Fact]
    public void Redact_MasksPasswordSegment()
    {
        ConnectionStringRedactor.Redact("Server=.;Database=Foo;Password=hunter2;")
            .ShouldBe("Server=.;Database=Foo;Password=***;");
    }

    [Theory]
    [InlineData("Pwd=hunter2", "Pwd=***")]
    [InlineData("User ID=sa", "User ID=***")]
    [InlineData("Uid=sa", "Uid=***")]
    [InlineData("PASSWORD=hunter2", "PASSWORD=***")]
    public void Redact_MasksEveryKnownSensitiveKeyCaseInsensitively(string segment, string expected)
    {
        ConnectionStringRedactor.Redact(segment).ShouldBe(expected);
    }

    [Fact]
    public void Redact_LeavesNonSensitiveSegmentsUntouched()
    {
        ConnectionStringRedactor.Redact("Server=.;Database=Foo;Trusted_Connection=True;")
            .ShouldBe("Server=.;Database=Foo;Trusted_Connection=True;");
    }

    [Fact]
    public void Redact_NeverContainsTheOriginalPassword()
    {
        ConnectionStringRedactor.Redact("Server=.;Password=hunter2;").ShouldNotContain("hunter2");
    }
}
