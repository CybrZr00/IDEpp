using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// A minimal MCP client over the stdio transport: spawns a server as a subprocess and speaks
/// newline-delimited JSON-RPC 2.0 over its stdin/stdout - one JSON object per line, no embedded
/// newlines, matching the framing confirmed against the MCP spec (this is NOT Content-Length-framed
/// like the Debug Adapter Protocol DapClient already in this codebase uses; a different protocol,
/// verified separately rather than assumed to match). The server may write logs to stderr, which is
/// forwarded via OnLog rather than surfaced as protocol data.
///
/// Launched via "cmd.exe /c &lt;command&gt; &lt;args...&gt;" rather than the command directly: on
/// Windows, Process.Start with UseShellExecute=false does not resolve .cmd/.bat files (npx, npm)
/// through PATHEXT the way a shell does, and stdio redirection requires UseShellExecute=false.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly Dictionary<long, TaskCompletionSource<JsonNode?>> _pending = [];
    private readonly object _pendingLock = new();
    private long _nextId;

    private McpClient(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdin.NewLine = "\n"; // messages must be newline-delimited; avoid StreamWriter's default \r\n on Windows
        _stdout = process.StandardOutput;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(DrainStderrAsync);
    }

    public event Action<string>? OnLog;

    // Encoding.UTF8 (the static instance) emits a UTF-8 BOM preamble - ProcessStartInfo writes that
    // preamble as the literal first bytes into the child's stdin, corrupting the framing of the
    // very first JSON-RPC message (confirmed live: the server never saw a valid "initialize" and
    // every connection attempt hung until the timeout). A UTF8Encoding with no BOM avoids this.
    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static McpClient Start(McpServerConfig config, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(config.Command);
        foreach (var arg in config.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (config.Env is not null)
        {
            foreach (var (key, value) in config.Env)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start MCP server '{config.Name}'.");
        return new McpClient(process);
    }

    /// <summary>Sends a JSON-RPC request and awaits its response's "result" (or throws on an "error" response).</summary>
    public async Task<JsonNode?> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = tcs;
        }

        var payload = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null)
        {
            payload["params"] = JsonSerializer.SerializeToNode(@params);
        }

        await WriteLineAsync(payload, cancellationToken);

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
        var response = await tcs.Task;

        if (response?["error"] is { } error)
        {
            throw new McpRequestException(error["message"]?.GetValue<string>() ?? "MCP request failed.");
        }

        return response?["result"];
    }

    /// <summary>Sends a JSON-RPC notification (no id, no response expected) - used for "notifications/initialized".</summary>
    public Task SendNotificationAsync(string method, object? @params = null, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null)
        {
            payload["params"] = JsonSerializer.SerializeToNode(@params);
        }

        return WriteLineAsync(payload, cancellationToken);
    }

    private async Task WriteLineAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        // Messages MUST NOT contain embedded newlines - JsonSerializer's default (non-indented)
        // output never does, so a plain single-line write is correct framing here.
        await _stdin.WriteLineAsync(payload.ToJsonString().AsMemory(), cancellationToken);
        await _stdin.FlushAsync(cancellationToken);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _stdout.ReadLineAsync();
                if (line is null)
                {
                    FailAllPending("The MCP server's stdout closed unexpectedly.");
                    return;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    // Per spec the server MUST NOT write non-protocol data to stdout, but real-world
                    // servers sometimes do anyway (a stray print, a banner) - skip rather than abort.
                    continue;
                }

                if (node?["id"] is { } idNode && node["method"] is null)
                {
                    // A response (has id, no method) - notifications/requests-from-server have no id we track.
                    TaskCompletionSource<JsonNode?>? tcs;
                    lock (_pendingLock)
                    {
                        _pending.Remove(idNode.GetValue<long>(), out tcs);
                    }

                    tcs?.TrySetResult(node);
                }
            }
        }
        catch (IOException)
        {
            FailAllPending("The MCP server's process exited unexpectedly.");
        }
    }

    private void FailAllPending(string message)
    {
        List<TaskCompletionSource<JsonNode?>> pending;
        lock (_pendingLock)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var tcs in pending)
        {
            tcs.TrySetException(new McpRequestException(message));
        }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                OnLog?.Invoke(line);
            }
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        await Task.CompletedTask;
    }
}

public sealed class McpRequestException(string message) : Exception(message);
