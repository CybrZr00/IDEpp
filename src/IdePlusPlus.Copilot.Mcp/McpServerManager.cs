using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// Owns every configured MCP server connection, aggregates their tools into ToolDefinitions the
/// chat client can send alongside propose_edit, and routes a tool call by its qualified name
/// (server__tool) back to the right server.
/// </summary>
public sealed class McpServerManager
{
    // Generous: a server started via `npx` for the first time downloads the package before it can
    // speak MCP at all, which can genuinely take tens of seconds.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    private readonly McpServerConfigStore _configStore;
    private readonly List<McpServerConnection> _connections = [];
    private readonly object _connectionsLock = new();

    public McpServerManager(McpServerConfigStore configStore)
    {
        _configStore = configStore;
    }

    public event Action<string>? OnLog;

    /// <summary>Raised (on a background thread) after a reconnect finishes, so the UI can refresh tool availability.</summary>
    public event Action? Changed;

    public IReadOnlyList<(string Name, bool IsConnected, string? Error, int ToolCount)> ServerStatuses
    {
        get
        {
            lock (_connectionsLock)
            {
                return _connections
                    .Select(c => (c.ServerName, c.IsConnected, c.ConnectionError, c.Tools.Count))
                    .ToList();
            }
        }
    }

    public async Task ReconnectAllAsync()
    {
        List<McpServerConnection> old;
        lock (_connectionsLock)
        {
            old = _connections.ToList();
            _connections.Clear();
        }

        foreach (var connection in old)
        {
            await connection.DisposeAsync();
        }

        var configs = _configStore.Load();
        var connected = await Task.WhenAll(configs.Select(ConnectOneAsync));

        lock (_connectionsLock)
        {
            _connections.AddRange(connected);
        }

        Changed?.Invoke();
    }

    private async Task<McpServerConnection> ConnectOneAsync(McpServerConfig config)
    {
        using var cts = new CancellationTokenSource(ConnectTimeout);
        var connection = await McpServerConnection.ConnectAsync(config, workingDirectory: null, cts.Token);
        connection.OnLog += line => OnLog?.Invoke($"[MCP:{config.Name}] {line}");

        OnLog?.Invoke(connection.IsConnected
            ? $"[MCP:{config.Name}] Connected - {connection.Tools.Count} tool(s)."
            : $"[MCP:{config.Name}] Failed to connect: {connection.ConnectionError}");

        return connection;
    }

    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        lock (_connectionsLock)
        {
            return _connections
                .Where(c => c.IsConnected)
                .SelectMany(c => c.Tools)
                .Select(t => new ToolDefinition(t.QualifiedName, t.Description ?? "", t.InputSchema))
                .ToList();
        }
    }

    public bool IsKnownTool(string qualifiedName)
    {
        lock (_connectionsLock)
        {
            return _connections.Any(c => c.Tools.Any(t => t.QualifiedName == qualifiedName));
        }
    }

    public async Task<McpToolCallResult> CallToolAsync(string qualifiedName, string argumentsJson, CancellationToken cancellationToken)
    {
        McpServerConnection? connection;
        string? toolName;
        lock (_connectionsLock)
        {
            connection = _connections.FirstOrDefault(c => c.Tools.Any(t => t.QualifiedName == qualifiedName));
            toolName = connection?.Tools.First(t => t.QualifiedName == qualifiedName).Name;
        }

        if (connection is null || toolName is null)
        {
            return new McpToolCallResult(true, $"Unknown MCP tool '{qualifiedName}'.");
        }

        JsonNode? arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson) ? null : JsonNode.Parse(argumentsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return new McpToolCallResult(true, $"Malformed arguments JSON for '{qualifiedName}'.");
        }

        return await connection.CallToolAsync(toolName, arguments, cancellationToken);
    }
}
