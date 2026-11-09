using System.Diagnostics;
using System.Globalization;
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
    private readonly Dictionary<string, Action<double?, double?, string?>> _progressHandlers = [];
    private readonly object _progressLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _nextId;
    private int _closedRaised;

    private McpClient(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdin.NewLine = "\n"; // messages must be newline-delimited; avoid StreamWriter's default \r\n on Windows
        _stdout = process.StandardOutput;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => RaiseClosed();
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(DrainStderrAsync);
    }

    public event Action<string>? OnLog;

    /// <summary>Raised at most once when the server process exits or its stdout closes - whichever is
    /// noticed first. Lets the connection notice a crash instead of reporting the server as alive.</summary>
    public event Action? OnClosed;

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
    public Task<JsonNode?> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default) =>
        SendRequestAsync(method, @params, onProgress: null, cancellationToken);

    /// <param name="onProgress">If set, a progressToken is attached to the request and this is invoked
    /// for each notifications/progress the server sends against it: (progress, total, message).</param>
    public async Task<JsonNode?> SendRequestAsync(
        string method, object? @params, Action<double?, double?, string?>? onProgress, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var token = id.ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = tcs;
        }

        if (onProgress is not null)
        {
            lock (_progressLock)
            {
                _progressHandlers[token] = onProgress;
            }
        }

        var payload = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null)
        {
            var paramsNode = JsonSerializer.SerializeToNode(@params);
            if (onProgress is not null && paramsNode is JsonObject paramsObject)
            {
                paramsObject["_meta"] = new JsonObject { ["progressToken"] = token };
            }

            payload["params"] = paramsNode;
        }

        await WriteLineAsync(payload, cancellationToken);

        // On cancel, tell the server to stop (notifications/cancelled) and drop the pending entry so it
        // can't leak - the old code only cancelled the local task and left _pending[id] forever.
        await using var registration = cancellationToken.Register(() =>
        {
            _ = SendCancelAsync(id);
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            var response = await tcs.Task;
            if (response?["error"] is { } error)
            {
                throw new McpRequestException(error["message"]?.GetValue<string>() ?? "MCP request failed.");
            }

            return response?["result"];
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }

            if (onProgress is not null)
            {
                lock (_progressLock)
                {
                    _progressHandlers.Remove(token);
                }
            }
        }
    }

    private async Task SendCancelAsync(long requestId)
    {
        try
        {
            await SendNotificationAsync("notifications/cancelled", new { requestId, reason = "Cancelled by IDE++." });
        }
        catch
        {
            // The server may already be gone; cancellation is best-effort.
        }
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
        // output never does, so a plain single-line write is correct framing here. The lock keeps two
        // writers (a tool call and the cancellation notification it triggers) from interleaving bytes.
        var line = payload.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stdin.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
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
                    RaiseClosed();
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

                if (node?["method"]?.GetValue<string>() is { } notificationMethod && node["id"] is null)
                {
                    // A notification from the server (has a method, no id). The ones we act on are
                    // progress updates and log messages; anything else is ignored.
                    HandleNotification(notificationMethod, node["params"]);
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
            RaiseClosed();
        }
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            OnClosed?.Invoke();
        }
    }

    private void HandleNotification(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "notifications/progress":
            {
                if (parameters?["progressToken"] is not { } tokenNode)
                {
                    return;
                }

                var token = tokenNode.ToString();
                Action<double?, double?, string?>? handler;
                lock (_progressLock)
                {
                    _progressHandlers.TryGetValue(token, out handler);
                }

                handler?.Invoke(TryGetDouble(parameters["progress"]), TryGetDouble(parameters["total"]), parameters["message"]?.GetValue<string>());
                break;
            }

            case "notifications/message":
            {
                var level = parameters?["level"]?.GetValue<string>() ?? "info";
                var data = parameters?["data"];
                var dataText = data is JsonValue value && value.TryGetValue<string>(out var text) ? text : data?.ToJsonString() ?? "";
                OnLog?.Invoke($"[{level}] {dataText}");
                break;
            }
        }
    }

    private static double? TryGetDouble(JsonNode? node)
    {
        try
        {
            return node?.GetValue<double>();
        }
        catch (Exception)
        {
            return null;
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
