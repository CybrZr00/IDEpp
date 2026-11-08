using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class McpServersPaneViewModelTests
{
    private static McpServerManager EmptyManager()
    {
        var root = Path.Combine(Path.GetTempPath(), "IdePlusPlusMcpPaneTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new McpServerManager(
            new McpServerConfigStore(Path.Combine(root, "global.json")),
            new McpProjectTrustStore(Path.Combine(root, "trust.json")));
    }

    private static McpServerStatus Status(string name, bool connected = true, int tools = 1) =>
        new(name, connected, connected ? null : "boom", tools, null, null, []);

    [Fact]
    public void ApplyStatuses_adds_a_row_per_server_and_selects_the_first()
    {
        var pane = new McpServersPaneViewModel(EmptyManager());

        pane.ApplyStatuses([Status("rea"), Status("playwright")]);

        pane.Servers.Select(r => r.Name).ShouldBe(["rea", "playwright"]);
        pane.SelectedServer!.Name.ShouldBe("rea");
        pane.HasServers.ShouldBeTrue();
    }

    [Fact]
    public void ApplyStatuses_updates_rows_in_place()
    {
        var pane = new McpServersPaneViewModel(EmptyManager());
        pane.ApplyStatuses([Status("rea", tools: 10)]);
        var original = pane.Servers.Single();

        pane.ApplyStatuses([Status("rea", tools: 42)]);

        pane.Servers.Single().ShouldBeSameAs(original);
        original.ToolCount.ShouldBe(42);
    }

    [Fact]
    public void ApplyStatuses_removes_servers_that_are_gone()
    {
        var pane = new McpServersPaneViewModel(EmptyManager());
        pane.ApplyStatuses([Status("rea"), Status("playwright")]);

        pane.ApplyStatuses([Status("rea")]);

        pane.Servers.Select(r => r.Name).ShouldBe(["rea"]);
    }

    [Fact]
    public void ApplyStatuses_preserves_the_selection()
    {
        var pane = new McpServersPaneViewModel(EmptyManager());
        pane.ApplyStatuses([Status("rea"), Status("playwright")]);
        pane.SelectedServer = pane.Servers.Single(r => r.Name == "playwright");

        pane.ApplyStatuses([Status("rea"), Status("playwright"), Status("context7")]);

        pane.SelectedServer!.Name.ShouldBe("playwright");
    }

    [Fact]
    public async Task Changed_on_the_manager_refreshes_through_Post()
    {
        var manager = EmptyManager();
        var pane = new McpServersPaneViewModel(manager);
        var posts = 0;
        pane.Post = action =>
        {
            posts++;
            action();
        };

        await manager.ReconnectAllAsync();

        posts.ShouldBeGreaterThan(0);
        pane.Servers.ShouldBeEmpty();
    }

    [Fact]
    public void Row_update_maps_a_connected_server_with_a_timed_call()
    {
        var row = new McpServerStatusRowViewModel("rea");

        row.Update(new McpServerStatus("rea", true, null, 7, DateTimeOffset.Now, 123.4, ["warming up", "ready"]));

        row.IsConnected.ShouldBeTrue();
        row.StatusText.ShouldBe("Connected - 7 tool(s)");
        row.LastCallText.ShouldContain("123 ms");
        row.RecentStderr.ShouldBe("warming up\nready");
    }

    [Fact]
    public void Row_update_maps_a_failed_server()
    {
        var row = new McpServerStatusRowViewModel("broken");

        row.Update(new McpServerStatus("broken", false, "spawn ENOENT", 0, null, null, []));

        row.IsConnected.ShouldBeFalse();
        row.StatusText.ShouldBe("Failed: spawn ENOENT");
        row.LastCallText.ShouldBe("No calls yet.");
    }
}
