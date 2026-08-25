using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Mcp.Tests;

/// <summary>
/// Covers the approval gate in front of a solution's checked-in MCP config. Every case here ends
/// with zero servers started, so nothing launches a real process - which is the whole point: an
/// unapproved config must not reach McpClient.
/// </summary>
public class McpServerManagerProjectTrustTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusMcpTrustTests", Guid.NewGuid().ToString("N"));

    private string SolutionDirectory => Path.Combine(_root, "solution");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteProjectConfig()
    {
        var directory = Path.Combine(SolutionDirectory, ".ideplusplus", "copilot");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "mcp-servers.json"),
            """[{ "Name": "evil", "Command": "ideplusplus-no-such-command", "args": [], "env": null }]""");
    }

    private McpServerManager NewManager()
    {
        // An empty global list, so anything that connects came from the project-local config.
        var manager = new McpServerManager(
            new McpServerConfigStore(Path.Combine(_root, "global.json")),
            new McpProjectTrustStore(Path.Combine(_root, "trust.json")));
        manager.SetSolutionDirectory(SolutionDirectory);
        return manager;
    }

    /// <summary>Headless or unattended, nothing may answer the trust question on the user's
    /// behalf - the config is skipped, not trusted.</summary>
    [Fact]
    public async Task ReconnectAllAsync_WithNoTrustPromptWired_SkipsTheProjectConfig()
    {
        WriteProjectConfig();
        var manager = NewManager();

        await manager.ReconnectAllAsync();

        manager.ServerStatuses.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReconnectAllAsync_WhenTheUserDeclines_StartsNothing()
    {
        WriteProjectConfig();
        var manager = NewManager();
        manager.ProjectTrustPrompt = _ => Task.FromResult(false);

        await manager.ReconnectAllAsync();

        manager.ServerStatuses.ShouldBeEmpty();
    }

    /// <summary>The prompt has to be able to show what would actually run, so it is handed the
    /// parsed servers rather than just a path.</summary>
    [Fact]
    public async Task ReconnectAllAsync_AsksWithTheCommandsThatWouldRun()
    {
        WriteProjectConfig();
        var manager = NewManager();
        McpProjectTrustRequest? asked = null;
        manager.ProjectTrustPrompt = request =>
        {
            asked = request;
            return Task.FromResult(false);
        };

        await manager.ReconnectAllAsync();

        asked.ShouldNotBeNull();
        asked!.SolutionDirectory.ShouldBe(SolutionDirectory);
        asked.ConfigPath.ShouldBe(
            Path.Combine(SolutionDirectory, ".ideplusplus", "copilot", "mcp-servers.json"));
        asked.Servers.Select(s => (s.Name, s.Command)).ShouldBe([("evil", "ideplusplus-no-such-command")]);
    }

    /// <summary>Already-approved content must not re-prompt on every solution load.</summary>
    [Fact]
    public async Task ReconnectAllAsync_WhenTheConfigWasAlreadyApproved_DoesNotAskAgain()
    {
        WriteProjectConfig();
        var configPath = Path.Combine(SolutionDirectory, ".ideplusplus", "copilot", "mcp-servers.json");
        new McpProjectTrustStore(Path.Combine(_root, "trust.json"))
            .Trust(SolutionDirectory, McpProjectTrustStore.Fingerprint(File.ReadAllText(configPath)));

        var manager = NewManager();
        var asked = false;
        manager.ProjectTrustPrompt = _ =>
        {
            asked = true;
            return Task.FromResult(false);
        };

        // The approved command does not exist, so cmd.exe exits immediately and the connect
        // fails fast rather than hanging. The assertion is only about whether we were asked.
        await manager.ReconnectAllAsync();

        asked.ShouldBeFalse();
    }

    [Fact]
    public async Task ReconnectAllAsync_WithNoProjectConfigAtAll_DoesNotAsk()
    {
        Directory.CreateDirectory(SolutionDirectory);
        var manager = NewManager();
        var asked = false;
        manager.ProjectTrustPrompt = _ =>
        {
            asked = true;
            return Task.FromResult(false);
        };

        await manager.ReconnectAllAsync();

        asked.ShouldBeFalse();
        manager.ServerStatuses.ShouldBeEmpty();
    }
}
