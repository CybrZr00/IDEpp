using IdePlusPlus.Copilot.Mcp;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Mcp.Tests;

public class McpServerConfigStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new McpServerConfigStore(TempPath());
        store.Load().ShouldBeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsServersIncludingEnv()
    {
        var path = TempPath();
        try
        {
            var store = new McpServerConfigStore(path);
            var servers = new[]
            {
                new McpServerConfig("playwright", "npx", ["-y", "@playwright/mcp@latest"]),
                new McpServerConfig("context7", "npx", ["-y", "@upstash/context7-mcp"], new Dictionary<string, string> { ["API_KEY"] = "abc123" }),
            };

            store.Save(servers);
            var loaded = store.Load();

            loaded.Count.ShouldBe(2);
            loaded[0].Name.ShouldBe("playwright");
            loaded[0].Args.ShouldBe(["-y", "@playwright/mcp@latest"]);
            loaded[0].Env.ShouldBeNull();
            loaded[1].Name.ShouldBe("context7");
            loaded[1].Env.ShouldNotBeNull();
            loaded[1].Env!["API_KEY"].ShouldBe("abc123");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MalformedJson_ReturnsEmptyRatherThanThrowing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json");
            new McpServerConfigStore(path).Load().ShouldBeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"mcp-servers-test-{Guid.NewGuid():N}.json");
}
