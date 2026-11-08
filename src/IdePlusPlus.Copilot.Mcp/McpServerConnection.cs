using System.Diagnostics;
using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// One connected (or failed-to-connect) MCP server. ConnectAsync never throws - a server that
/// fails to start, times out, or errors during the handshake produces a connection with
/// IsConnected=false and a ConnectionError, so one misconfigured server doesn't stop the others
/// from working (mirrors PluginLoader's per-plugin tolerance).
///
/// The connection keeps the config it was started from (so it can be restarted on its own), a bounded
/// tail of the server's stderr (captured from before the handshake, which is exactly when a failing
/// server explains itself), and the time and duration of its last tool call.
/// </summary>
public sealed class McpServerConnection : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    private const int StderrTailMax = 200;

    private readonly McpServerConfig _config;
    private readonly List<string> _stderrTail = [];
    private readonly object _stderrLock = new();

    private McpClient? _client;
    private Action? _onClosed;

    private McpServerConnection(McpServerConfig config)
    {
        _config = config;
        ServerName = config.Name;
    }

    public string ServerName { get; }

    public McpServerConfig Config => _config;

    public bool IsConnected { get; private set; }

    public string? ConnectionError { get; private set; }

    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = [];

    public DateTimeOffset? LastCallAt { get; private set; }

    public double? LastDurationMs { get; private set; }

    /// <summary>A snapshot of the server's most recent stderr lines.</summary>
    public IReadOnlyList<string> RecentStderr
    {
        get
        {
            lock (_stderrLock)
            {
                return _stderrTail.ToList();
            }
        }
    }

    /// <param name="onLog">Receives each stderr line (also kept in <see cref="RecentStderr"/>). Wired
    /// before the handshake, so handshake-time stderr from a failing server is not lost.</param>
    /// <param name="onClosed">Raised once if the server process exits or its stdout closes after a
    /// successful connection - lets the manager flip status and notify the UI instead of reporting a
    /// crashed server as still connected.</param>
    public static async Task<McpServerConnection> ConnectAsync(
        McpServerConfig config, string? workingDirectory, Action<string>? onLog, Action? onClosed, CancellationToken cancellationToken)
    {
        var connection = new McpServerConnection(config) { _onClosed = onClosed };
        try
        {
            var client = McpClient.Start(config, workingDirectory);
            client.OnLog += line => connection.RecordStderr(line, onLog);
            client.OnClosed += connection.HandleClientClosed;
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

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
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
        finally
        {
            stopwatch.Stop();
            LastCallAt = startedAt;
            LastDurationMs = stopwatch.Elapsed.TotalMilliseconds;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A deliberate dispose (restart, reconnect, shutdown) should not look like the server crashing.
        _onClosed = null;
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    private void RecordStderr(string line, Action<string>? onLog)
    {
        lock (_stderrLock)
        {
            _stderrTail.Add(line);
            if (_stderrTail.Count > StderrTailMax)
            {
                _stderrTail.RemoveRange(0, _stderrTail.Count - StderrTailMax);
            }
        }

        onLog?.Invoke(line);
    }

    private void HandleClientClosed()
    {
        if (!IsConnected)
        {
            return;
        }

        IsConnected = false;
        _onClosed?.Invoke();
    }
}
