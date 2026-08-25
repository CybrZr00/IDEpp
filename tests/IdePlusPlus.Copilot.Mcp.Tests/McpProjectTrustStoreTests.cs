using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Mcp.Tests;

public class McpProjectTrustStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusTrustTests", Guid.NewGuid().ToString("N"));

    private McpProjectTrustStore NewStore() =>
        new(Path.Combine(_directory, "mcp-project-trust.json"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void IsTrusted_BeforeAnythingIsApproved_IsFalse()
    {
        NewStore().IsTrusted(@"C:\repos\Thing", "abc").ShouldBeFalse();
    }

    [Fact]
    public void IsTrusted_AfterTrust_IsTrueForTheSameFingerprint()
    {
        var store = NewStore();
        store.Trust(@"C:\repos\Thing", "abc");

        store.IsTrusted(@"C:\repos\Thing", "abc").ShouldBeTrue();
    }

    /// <summary>The reason trust is keyed on content, not just the solution: a repo whose config
    /// changes on the next pull must be approved again rather than inheriting the old answer.</summary>
    [Fact]
    public void IsTrusted_WhenTheConfigContentChanged_IsFalseAgain()
    {
        var store = NewStore();
        store.Trust(@"C:\repos\Thing", "abc");

        store.IsTrusted(@"C:\repos\Thing", "def").ShouldBeFalse();
    }

    [Fact]
    public void IsTrusted_ForADifferentSolution_IsFalse()
    {
        var store = NewStore();
        store.Trust(@"C:\repos\Thing", "abc");

        store.IsTrusted(@"C:\repos\Other", "abc").ShouldBeFalse();
    }

    [Fact]
    public void Trust_Persists_AcrossStoreInstances()
    {
        NewStore().Trust(@"C:\repos\Thing", "abc");

        NewStore().IsTrusted(@"C:\repos\Thing", "abc").ShouldBeTrue();
    }

    [Fact]
    public void IsTrusted_IgnoresPathCasingAndTrailingSeparators()
    {
        var store = NewStore();
        store.Trust(@"C:\repos\Thing", "abc");

        store.IsTrusted(@"c:\REPOS\thing\", "abc").ShouldBeTrue();
    }

    [Fact]
    public void Revoke_MakesAPreviouslyTrustedSolutionUntrustedAgain()
    {
        var store = NewStore();
        store.Trust(@"C:\repos\Thing", "abc");

        store.Revoke(@"C:\repos\Thing");

        store.IsTrusted(@"C:\repos\Thing", "abc").ShouldBeFalse();
    }

    [Fact]
    public void Fingerprint_OfTheSameContent_Matches()
    {
        McpProjectTrustStore.Fingerprint("""[{"Name":"a"}]""")
            .ShouldBe(McpProjectTrustStore.Fingerprint("""[{"Name":"a"}]"""));
    }

    [Fact]
    public void Fingerprint_OfDifferentContent_Differs()
    {
        McpProjectTrustStore.Fingerprint("""[{"Name":"a"}]""")
            .ShouldNotBe(McpProjectTrustStore.Fingerprint("""[{"Name":"b"}]"""));
    }

    /// <summary>A checkout under a different autocrlf setting is the same config, not a new one to
    /// re-approve.</summary>
    [Fact]
    public void Fingerprint_IgnoresLineEndingStyle()
    {
        McpProjectTrustStore.Fingerprint("[\r\n  {}\r\n]")
            .ShouldBe(McpProjectTrustStore.Fingerprint("[\n  {}\n]"));
    }

    /// <summary>An unreadable trust file must mean "nothing is trusted", never "everything is".</summary>
    [Fact]
    public void IsTrusted_WhenTheTrustFileIsCorrupt_FailsClosed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "mcp-project-trust.json"), "{ not json");

        NewStore().IsTrusted(@"C:\repos\Thing", "abc").ShouldBeFalse();
    }
}
