using System.Text.Json.Nodes;
using IdePlusPlus.PluginContracts;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// PluginChatToolRegistry sits directly in the highest-stakes existing code path in the app
/// (CopilotChatPaneViewModel's tool aggregation/dispatch, which also drives ProposeEditTool's real
/// file-editing route) - so its two jobs are the qualified-name mapping that keeps plugin tools from
/// colliding with MCP tools or each other, and guaranteeing a throwing plugin delegate never
/// propagates past ExecuteAsync.
/// </summary>
public sealed class PluginChatToolRegistryTests
{
    private static PluginToolDefinition MakeDefinition(string name = "greet") =>
        new(name, "A trivial tool.", new JsonObject { ["type"] = "object" });

    [Fact]
    public void GetToolDefinitions_UsesMcpStyleQualifiedName()
    {
        var registry = new PluginChatToolRegistry();
        registry.Register("MyPlugin", MakeDefinition("greet"), (_, _) => Task.FromResult(new PluginToolResult(false, "hi")));

        var definitions = registry.GetToolDefinitions();

        definitions.Count.ShouldBe(1);
        definitions[0].Name.ShouldBe("plugin__MyPlugin__greet");
        definitions[0].Description.ShouldBe("A trivial tool.");
    }

    [Fact]
    public void IsPluginTool_TrueForRegisteredQualifiedName_FalseOtherwise()
    {
        var registry = new PluginChatToolRegistry();
        registry.Register("MyPlugin", MakeDefinition("greet"), (_, _) => Task.FromResult(new PluginToolResult(false, "hi")));

        registry.IsPluginTool("plugin__MyPlugin__greet").ShouldBeTrue();
        registry.IsPluginTool("greet").ShouldBeFalse();
        registry.IsPluginTool("plugin__OtherPlugin__greet").ShouldBeFalse();
        registry.IsPluginTool("some_mcp_server__greet").ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownQualifiedName_ReturnsErrorResult_DoesNotThrow()
    {
        var registry = new PluginChatToolRegistry();

        var result = await registry.ExecuteAsync("plugin__Nope__nope", null, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("plugin__Nope__nope");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowingDelegate_ReturnsErrorResult_DoesNotPropagate()
    {
        var registry = new PluginChatToolRegistry();
        registry.Register("MyPlugin", MakeDefinition("boom"), (_, _) => throw new InvalidOperationException("kaboom"));

        var result = await registry.ExecuteAsync("plugin__MyPlugin__boom", null, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("kaboom");
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulDelegate_ReturnsItsResult()
    {
        var registry = new PluginChatToolRegistry();
        registry.Register("MyPlugin", MakeDefinition("greet"), (args, _) =>
            Task.FromResult(new PluginToolResult(false, $"Hello, {args?["name"]?.GetValue<string>()}!")));

        var arguments = new JsonObject { ["name"] = "World" };
        var result = await registry.ExecuteAsync("plugin__MyPlugin__greet", arguments, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Text.ShouldBe("Hello, World!");
    }
}
