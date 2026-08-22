using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// One connected (or failed-to-connect) MCP server. ConnectAsync never throws - a server that
/// fails to start, times out, or errors during the handshake produces a connection with
/// IsConnected=false and a ConnectionError, so one misconfigured server doesn't stop the others
/// from working (mirrors PluginLoader's per-plugin tolerance).
/// </summary>
public sealed class McpServerConnection : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-06-18";

    private McpClient? _client;

    private McpServerConnection(string serverName) => ServerName = serverName;

    public string ServerName { get; }

    public bool IsConnected { get; private set; }

    public string? ConnectionError { get; private set; }

    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = [];

    public event Action<string>? OnLog;

    public static async Task<McpServerConnection> ConnectAsync(
        McpServerConfig config, string? workingDirectory, CancellationToken cancellationToken)
    {
        var connection = new McpServerConnection(config.Name);
        try
        {
            var client = McpClient.Start(config, workingDirectory);
            client.OnLog += line => connection.OnLog?.Invoke(line);
            connection._client = client;

            await client.SendRequestAsync("initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "IDE++", version = "1.0.0" },
            }, cancellationToken);

            await client.SendNotificationAsync("notifications/initialized", cancellationToken: cancellationToken);

            var toolsResult = await client.SendRequestAsync("tools/list", null, cancellationToken);
            connection.Tools = (toolsResult?["tools"]?.AsArray() ?? [])
                .Where(t => t is not null)
                .Select(t => new McpToolInfo(
                    config.Name,
                    t!["name"]!.GetValue<string>(),
                    t["description"]?.GetValue<string>(),
                    t["inputSchema"]?.DeepClone() ?? new JsonObject()))
                .ToList();

            connection.IsConnected = true;
        }
        catch (Exception ex)
        {
            connection.ConnectionError = ex.Message;
            if (connection._client is not null)
            {
                await connection._client.DisposeAsync();
                connection._client = null;
            }
        }

        return connection;
    }

    public async Task<McpToolCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            return new McpToolCallResult(true, $"MCP server '{ServerName}' is not connected.");
        }

        try
        {
            var result = await _client.SendRequestAsync("tools/call", new { name = toolName, arguments }, cancellationToken);
            var isError = result?["isError"]?.GetValue<bool>() ?? false;
            var text = string.Join(
                "\n",
                (result?["content"]?.AsArray() ?? [])
                    .Where(c => c?["type"]?.GetValue<string>() == "text")
                    .Select(c => c!["text"]?.GetValue<string>() ?? ""));
            return new McpToolCallResult(isError, text);
        }
        catch (Exception ex)
        {
            return new McpToolCallResult(true, $"Tool call failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
