using IdePlusPlus.Connections;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Connections.Tests;

public sealed class ConnectionsStoreTests : IDisposable
{
    private readonly string _solutionDirectory =
        Path.Combine(Path.GetTempPath(), $"ideplusplus-connections-{Guid.NewGuid()}");

    public ConnectionsStoreTests() => Directory.CreateDirectory(_solutionDirectory);

    public void Dispose() => Directory.Delete(_solutionDirectory, recursive: true);

    [Fact]
    public void Load_NoSavedFile_ReturnsEmpty()
    {
        ConnectionsStore.Load(_solutionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var connections = new[] { new StoredConnection("abc123", "Prod DB") };

        ConnectionsStore.Save(_solutionDirectory, connections);

        var loaded = ConnectionsStore.Load(_solutionDirectory);
        loaded.ShouldHaveSingleItem().ShouldBe(connections[0]);
    }

    [Fact]
    public void Save_NeverWritesAConnectionStringValue()
    {
        // ConnectionsStore only ever sees {Id, Name} - the actual connection string is routed
        // through SecureSettingsStore instead, so there's nothing to accidentally leak here even if
        // a caller tried.
        ConnectionsStore.Save(_solutionDirectory, [new StoredConnection("abc123", "Prod DB")]);

        var path = Path.Combine(_solutionDirectory, ".ideplusplus", "connections.json");
        File.ReadAllText(path).ShouldNotContain("Password");
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyRatherThanThrowing()
    {
        var dir = Path.Combine(_solutionDirectory, ".ideplusplus");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "connections.json"), "{ not valid json");

        ConnectionsStore.Load(_solutionDirectory).ShouldBeEmpty();
    }
}
