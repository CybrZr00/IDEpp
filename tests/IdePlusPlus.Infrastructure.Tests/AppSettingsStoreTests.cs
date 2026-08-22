using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"ideplusplus-settings-{Guid.NewGuid()}.json");

    public void Dispose() => File.Delete(_settingsPath);

    [Fact]
    public void Get_UnknownKey_ReturnsNull()
    {
        var store = new AppSettingsStore(_settingsPath);

        store.Get("missing").ShouldBeNull();
    }

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        var store = new AppSettingsStore(_settingsPath);

        store.Set("ChatProvider", "LM Studio");

        store.Get("ChatProvider").ShouldBe("LM Studio");
    }

    [Fact]
    public void SetThenGet_PersistsAcrossInstances()
    {
        new AppSettingsStore(_settingsPath).Set("ChatProvider", "LM Studio");

        var reloaded = new AppSettingsStore(_settingsPath);

        reloaded.Get("ChatProvider").ShouldBe("LM Studio");
    }

    [Fact]
    public void Set_OverwritesExistingValueForSameKey()
    {
        var store = new AppSettingsStore(_settingsPath);
        store.Set("ChatProvider", "LM Studio");

        store.Set("ChatProvider", "OpenRouter");

        store.Get("ChatProvider").ShouldBe("OpenRouter");
    }

    [Fact]
    public void MissingSettingsFile_LoadsAsEmpty()
    {
        var store = new AppSettingsStore(_settingsPath);

        store.Get("anything").ShouldBeNull();
    }

    [Fact]
    public void CorruptSettingsFile_LoadsAsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, "{ not valid json");

        var store = new AppSettingsStore(_settingsPath);

        store.Get("anything").ShouldBeNull();
    }
}
