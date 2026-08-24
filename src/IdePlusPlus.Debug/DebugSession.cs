using System.Text.Json.Nodes;
using IdePlusPlus.Editor;

namespace IdePlusPlus.Debug;

public sealed record StackFrameInfo(int Id, string Name, string? FilePath, int Line);

public sealed record VariableInfo(string Name, string Value, string? Type);

public sealed class DebugSession
{
    private readonly BreakpointManager _breakpointManager;
    private readonly DebugLocationIndicator _debugLocation;
    private DapClient? _client;
    private int _currentThreadId;

    public DebugSession(BreakpointManager breakpointManager, DebugLocationIndicator debugLocation)
    {
        _breakpointManager = breakpointManager;
        _debugLocation = debugLocation;
    }

    /// <summary>Raised (on a background thread) whenever IsActive or the stopped/running state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised (on a background thread) with a line of the debuggee's stdout/stderr.</summary>
    public event Action<string>? OutputReceived;

    public bool IsActive => _client is not null;

    public bool IsStopped { get; private set; }

    /// <summary>Human-readable label for this session (e.g. the program's file name, or the
    /// attached process), used by session-picker UI once more than one session can be active.</summary>
    public string DisplayName { get; private set; } = "";

    public async Task StartAsync(
        string debugAdapterExecutablePath,
        string programPath,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        IReadOnlyList<string>? commandLineArgs = null)
    {
        if (!File.Exists(debugAdapterExecutablePath))
        {
            throw new FileNotFoundException(
                "netcoredbg not found. Download it from https://github.com/Samsung/netcoredbg/releases " +
                $"and extract it to '{debugAdapterExecutablePath}'.",
                debugAdapterExecutablePath);
        }

        DisplayName = Path.GetFileName(programPath);

        var client = DapClient.Start(debugAdapterExecutablePath);
        client.EventReceived += OnEvent;
        _client = client;

        await client.SendRequestAsync("initialize", new
        {
            clientID = "ideplusplus",
            adapterID = "netcoredbg",
            pathFormat = "path",
            linesStartAt1 = true,
            columnsStartAt1 = true,
        });

        // Without this, whether an unhandled exception even stops the debugger depends entirely on
        // netcoredbg's undeclared default. "user-unhandled" is netcoredbg's documented filter id
        // (mirroring vsdbg's) for "break when an exception escapes user code unhandled".
        await client.SendRequestAsync("setExceptionBreakpoints", new { filters = new[] { "user-unhandled" } });

        foreach (var (file, lines) in _breakpointManager.AllBreakpoints)
        {
            if (lines.Count == 0)
            {
                continue;
            }

            await client.SendRequestAsync("setBreakpoints", new
            {
                source = new { path = file },
                breakpoints = lines.Select(line => new { line }).ToArray(),
            });
        }

        await client.SendRequestAsync("launch", new
        {
            name = "IDE++ launch",
            type = "coreclr",
            request = "launch",
            program = programPath,
            cwd = workingDirectory,
            stopAtEntry = false,
            env = environmentVariables ?? new Dictionary<string, string>(),
            args = commandLineArgs ?? Array.Empty<string>(),
        });

        await client.SendRequestAsync("configurationDone");
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Sibling to <see cref="StartAsync"/> for attaching to an already-running process instead of
    /// launching a new one: same initialize/exception-filter/breakpoints/configurationDone sequence,
    /// but sends DAP "attach" (netcoredbg's vscode-interpreter mode, mirroring vsdbg, accepts
    /// "attach" as a sibling request to "launch" taking a processId) instead of "launch".
    /// </summary>
    public async Task AttachAsync(
        string debugAdapterExecutablePath,
        int processId,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        if (!File.Exists(debugAdapterExecutablePath))
        {
            throw new FileNotFoundException(
                "netcoredbg not found. Download it from https://github.com/Samsung/netcoredbg/releases " +
                $"and extract it to '{debugAdapterExecutablePath}'.",
                debugAdapterExecutablePath);
        }

        DisplayName = $"Attached: process {processId}";

        var client = DapClient.Start(debugAdapterExecutablePath);
        client.EventReceived += OnEvent;
        _client = client;

        await client.SendRequestAsync("initialize", new
        {
            clientID = "ideplusplus",
            adapterID = "netcoredbg",
            pathFormat = "path",
            linesStartAt1 = true,
            columnsStartAt1 = true,
        });

        await client.SendRequestAsync("setExceptionBreakpoints", new { filters = new[] { "user-unhandled" } });

        foreach (var (file, lines) in _breakpointManager.AllBreakpoints)
        {
            if (lines.Count == 0)
            {
                continue;
            }

            await client.SendRequestAsync("setBreakpoints", new
            {
                source = new { path = file },
                breakpoints = lines.Select(line => new { line }).ToArray(),
            });
        }

        await client.SendRequestAsync("attach", new
        {
            name = "IDE++ attach",
            type = "coreclr",
            request = "attach",
            processId,
        });

        await client.SendRequestAsync("configurationDone");
        StateChanged?.Invoke();
    }

    private void OnEvent(string eventName, JsonNode? body)
    {
        switch (eventName)
        {
            case "stopped":
                _currentThreadId = body!["threadId"]!.GetValue<int>();
                IsStopped = true;
                _ = UpdateCurrentLocationAsync();
                break;

            case "continued":
                IsStopped = false;
                _debugLocation.Clear();
                StateChanged?.Invoke();
                break;

            case "output":
                var category = body?["category"]?.GetValue<string>();
                if (category is "stdout" or "stderr" && body?["output"]?.GetValue<string>() is { } text)
                {
                    OutputReceived?.Invoke(text);
                }

                break;

            case "exited":
                // Informational only (carries the debuggee's exit code) - the "terminated"
                // event that follows is what actually ends the debug session.
                break;

            case "terminated":
                IsStopped = false;
                _debugLocation.Clear();

                // netcoredbg doesn't exit on its own after "terminated" - it expects the client
                // to send "disconnect". Null out _client immediately so IsActive reflects the
                // ended session right away, then shut the process down in the background.
                var endedClient = _client;
                _client = null;
                StateChanged?.Invoke();
                if (endedClient is not null)
                {
                    _ = ShutdownAsync(endedClient);
                }

                break;
        }
    }

    private async Task UpdateCurrentLocationAsync()
    {
        var frames = await GetStackTraceAsync();
        if (frames.Count > 0 && frames[0].FilePath is { } filePath)
        {
            _debugLocation.Show(filePath, frames[0].Line);
        }

        StateChanged?.Invoke();
    }

    public async Task<IReadOnlyList<StackFrameInfo>> GetStackTraceAsync()
    {
        if (_client is null)
        {
            return [];
        }

        var response = await _client.SendRequestAsync("stackTrace", new { threadId = _currentThreadId });
        var frames = response?["body"]?["stackFrames"]?.AsArray() ?? [];
        return frames
            .Where(f => f is not null)
            .Select(f => new StackFrameInfo(
                f!["id"]!.GetValue<int>(),
                f["name"]?.GetValue<string>() ?? "",
                f["source"]?["path"]?.GetValue<string>(),
                f["line"]?.GetValue<int>() ?? 0))
            .ToList();
    }

    public async Task<IReadOnlyList<VariableInfo>> GetLocalsAsync(int frameId)
    {
        if (_client is null)
        {
            return [];
        }

        var scopesResponse = await _client.SendRequestAsync("scopes", new { frameId });
        var scopes = scopesResponse?["body"]?["scopes"]?.AsArray() ?? [];
        var localsScope = scopes.FirstOrDefault(s => s?["name"]?.GetValue<string>() == "Locals");
        if (localsScope is null)
        {
            return [];
        }

        var variablesReference = localsScope["variablesReference"]!.GetValue<int>();
        var variablesResponse = await _client.SendRequestAsync("variables", new { variablesReference });
        var variables = variablesResponse?["body"]?["variables"]?.AsArray() ?? [];
        return variables
            .Where(v => v is not null)
            .Select(v => new VariableInfo(
                v!["name"]!.GetValue<string>(),
                v["value"]?.GetValue<string>() ?? "",
                v["type"]?.GetValue<string>()))
            .ToList();
    }

    public Task ContinueAsync() =>
        _client?.SendRequestAsync("continue", new { threadId = _currentThreadId }) ?? Task.CompletedTask;

    public Task StepOverAsync() =>
        _client?.SendRequestAsync("next", new { threadId = _currentThreadId }) ?? Task.CompletedTask;

    public Task StepIntoAsync() =>
        _client?.SendRequestAsync("stepIn", new { threadId = _currentThreadId }) ?? Task.CompletedTask;

    public Task StepOutAsync() =>
        _client?.SendRequestAsync("stepOut", new { threadId = _currentThreadId }) ?? Task.CompletedTask;

    public async Task StopAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        await client.SendRequestAsync("disconnect", new { terminateDebuggee = true });
        await client.DisposeAsync();
        _client = null;
        IsStopped = false;
        _debugLocation.Clear();
        StateChanged?.Invoke();
    }

    /// <summary>Best-effort graceful shutdown for a client whose debuggee already exited on its own.</summary>
    private static async Task ShutdownAsync(DapClient client)
    {
        try
        {
            await client.SendRequestAsync("disconnect", new { terminateDebuggee = true }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // The process may already be gone, or slow to respond - DisposeAsync force-kills it either way.
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
