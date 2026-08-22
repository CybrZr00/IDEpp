using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdePlusPlus.Debug;

/// <summary>
/// A minimal Debug Adapter Protocol client: spawns a DAP-speaking process (netcoredbg) and speaks
/// Content-Length-framed JSON over its stdio, matching responses to requests by seq and surfacing
/// events. Validated against netcoredbg's real "vscode" interpreter mode via a manual spike before
/// this was written - the framing and request/response shapes below are not guesses.
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = [];
    private readonly object _pendingLock = new();
    private int _seq;

    private DapClient(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(DrainStderrAsync);
    }

    public event Action<string, JsonNode?>? EventReceived;

    public static DapClient Start(string debugAdapterExecutablePath)
    {
        var startInfo = new ProcessStartInfo(debugAdapterExecutablePath, "--interpreter=vscode")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start debug adapter at '{debugAdapterExecutablePath}'.");
        return new DapClient(process);
    }

    public async Task<JsonNode?> SendRequestAsync(string command, object? arguments = null, CancellationToken cancellationToken = default)
    {
        var requestSeq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[requestSeq] = tcs;
        }

        var payload = new JsonObject
        {
            ["seq"] = requestSeq,
            ["type"] = "request",
            ["command"] = command,
        };
        if (arguments is not null)
        {
            payload["arguments"] = JsonSerializer.SerializeToNode(arguments);
        }

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var header = Encoding.UTF8.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        await _stdin.WriteAsync(header, cancellationToken);
        await _stdin.WriteAsync(bytes, cancellationToken);
        await _stdin.FlushAsync(cancellationToken);

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var headerLine = await ReadLineAsync(_stdout);
                if (headerLine is null)
                {
                    return;
                }

                if (headerLine.Length == 0)
                {
                    continue;
                }

                var contentLength = int.Parse(headerLine.Split(':')[1].Trim());
                await ReadLineAsync(_stdout); // blank line separating headers from the body

                var buffer = new byte[contentLength];
                var read = 0;
                while (read < contentLength)
                {
                    var n = await _stdout.ReadAsync(buffer.AsMemory(read, contentLength - read));
                    if (n == 0)
                    {
                        return;
                    }

                    read += n;
                }

                var node = JsonNode.Parse(Encoding.UTF8.GetString(buffer));
                DispatchMessage(node);
            }
        }
        catch (IOException)
        {
            // Adapter process exited out from under the pipe; nothing more to read.
        }
    }

    private void DispatchMessage(JsonNode? node)
    {
        var type = node?["type"]?.GetValue<string>();
        if (type == "response")
        {
            var requestSeq = node!["request_seq"]!.GetValue<int>();
            TaskCompletionSource<JsonNode?>? tcs;
            lock (_pendingLock)
            {
                _pending.Remove(requestSeq, out tcs);
            }

            tcs?.TrySetResult(node);
        }
        else if (type == "event")
        {
            var eventName = node!["event"]!.GetValue<string>();
            EventReceived?.Invoke(eventName, node["body"]);
        }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            await _process.StandardError.ReadToEndAsync();
        }
        catch (IOException)
        {
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, 1));
            if (n == 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            if (buffer[0] == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                {
                    sb.Length--;
                }

                return sb.ToString();
            }

            sb.Append((char)buffer[0]);
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
            // Already exited between the check and the kill - fine.
        }

        _process.Dispose();
        await Task.CompletedTask;
    }
}
