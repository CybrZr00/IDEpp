using IdePlusPlus.Copilot.Mcp;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Mcp.Tests;

public class McpConfigMergerTests
{
    [Fact]
    public void Merge_NoOverlap_ReturnsBothLists()
    {
        var global = new[] { new McpServerConfig("playwright", "npx", ["-y", "@playwright/mcp@latest"]) };
        var projectLocal = new[] { new McpServerConfig("db", "node", ["db-mcp.js"]) };

        var merged = McpConfigMerger.Merge(global, projectLocal);

        merged.Count.ShouldBe(2);
        merged.ShouldContain(c => c.Name == "playwright");
        merged.ShouldContain(c => c.Name == "db");
    }

    [Fact]
    public void Merge_SameNameInBoth_ProjectLocalWins()
    {
        var global = new[] { new McpServerConfig("db", "node", ["global-db-mcp.js"]) };
        var projectLocal = new[] { new McpServerConfig("db", "node", ["project-db-mcp.js"]) };

        var merged = McpConfigMerger.Merge(global, projectLocal);

        var config = merged.ShouldHaveSingleItem();
        config.Args.ShouldBe(["project-db-mcp.js"]);
    }

    [Fact]
    public void Merge_EmptyProjectLocal_ReturnsGlobalUnchanged()
    {
        var global = new[] { new McpServerConfig("playwright", "npx", ["-y", "@playwright/mcp@latest"]) };

        var merged = McpConfigMerger.Merge(global, []);

        merged.ShouldBe(global);
    }

    [Fact]
    public void Merge_EmptyGlobal_ReturnsProjectLocalOnly()
    {
        var projectLocal = new[] { new McpServerConfig("db", "node", ["db-mcp.js"]) };

        var merged = McpConfigMerger.Merge([], projectLocal);

        merged.ShouldBe(projectLocal);
    }
}
