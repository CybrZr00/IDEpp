using System.Text.Json;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

public class SecureSettingsStoreTests : IDisposable
{
    private readonly string _secretsPath = Path.Combine(Path.GetTempPath(), $"ideplusplus-secrets-{Guid.NewGuid()}.json");

    public void Dispose() => File.Delete(_secretsPath);

    [Fact]
    public void GetSecret_UnknownKey_ReturnsNull()
    {
        var store = new SecureSettingsStore(_secretsPath);

        store.GetSecret("missing").ShouldBeNull();
    }

    [Fact]
    public void SetThenGetSecret_RoundTrips()
    {
        var store = new SecureSettingsStore(_secretsPath);

        store.SetSecret("OpenRouterApiKey", "sk-test-12345");

        store.GetSecret("OpenRouterApiKey").ShouldBe("sk-test-12345");
    }

    [Fact]
    public void SetThenGetSecret_PersistsAcrossInstances()
    {
        new SecureSettingsStore(_secretsPath).SetSecret("OpenRouterApiKey", "sk-test-12345");

        var reloaded = new SecureSettingsStore(_secretsPath);

        reloaded.GetSecret("OpenRouterApiKey").ShouldBe("sk-test-12345");
    }

    [Fact]
    public void SecretIsNotStoredAsPlaintextOnDisk()
    {
        var store = new SecureSettingsStore(_secretsPath);

        store.SetSecret("OpenRouterApiKey", "sk-test-12345");

        File.ReadAllText(_secretsPath).ShouldNotContain("sk-test-12345");
    }

    [Fact]
    public void MissingSecretsFile_LoadsAsEmpty()
    {
        var store = new SecureSettingsStore(_secretsPath);

        store.GetSecret("anything").ShouldBeNull();
    }

    [Fact]
    public void CorruptSecretsFile_LoadsAsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_secretsPath)!);
        File.WriteAllText(_secretsPath, "{ not valid json");

        var store = new SecureSettingsStore(_secretsPath);

        store.GetSecret("anything").ShouldBeNull();
    }

    [Fact]
    public void HandCorruptedBase64Entry_IsTreatedAsUnsetRatherThanThrowing()
    {
        WriteRawEntries(new Dictionary<string, string> { ["OpenRouterApiKey"] = "not-valid-base64!!!" });
        var store = new SecureSettingsStore(_secretsPath);

        var result = Record.Exception(() => store.GetSecret("OpenRouterApiKey"));

        result.ShouldBeNull();
        store.GetSecret("OpenRouterApiKey").ShouldBeNull();
    }

    [Fact]
    public void HandCorruptedEntry_IsPrunedFromDiskAfterBeingRead()
    {
        WriteRawEntries(new Dictionary<string, string> { ["OpenRouterApiKey"] = "not-valid-base64!!!" });
        var store = new SecureSettingsStore(_secretsPath);

        store.GetSecret("OpenRouterApiKey");

        var reloaded = new SecureSettingsStore(_secretsPath);
        reloaded.GetSecret("OpenRouterApiKey").ShouldBeNull();
        File.ReadAllText(_secretsPath).ShouldNotContain("not-valid-base64");
    }

    private void WriteRawEntries(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_secretsPath)!);
        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(entries));
    }
}
