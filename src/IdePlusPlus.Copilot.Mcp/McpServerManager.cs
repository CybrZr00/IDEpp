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
    private readonly McpProjectTrustStore _trustStore;
    private readonly List<McpServerConnection> _connections = [];
    private readonly object _connectionsLock = new();

    // ReconnectAllAsync's clear-then-repopulate sequence isn't atomic across concurrent calls - e.g.
    // App.xaml.cs kicks one off at startup for the global config, and MainWindowViewModel.LoadSolutionAsync
    // kicks off another moments later once a reopened-on-startup solution finishes loading. Without this,
    // two overlapping calls can each see the (already-cleared) list as empty and both AddRange their own
    // connections in, leaving duplicate-named entries that crash ServerStatuses' ToDictionary lookup
    // (SettingsViewModel.RefreshMcpServerStatuses) the next time Settings opens.
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    private string? _solutionDirectory;

    public McpServerManager(McpServerConfigStore configStore, McpProjectTrustStore trustStore)
    {
        _configStore = configStore;
        _trustStore = trustStore;
    }

    /// <summary>
    /// Asked before a solution's checked-in MCP config is run for the first time, and again
    /// whenever that config changes. Left unset (tests, headless) the config is skipped rather
    /// than trusted - an unattended process must not answer this question on the user's behalf.
    /// </summary>
    public Func<McpProjectTrustRequest, Task<bool>>? ProjectTrustPrompt { get; set; }

    public event Action<string>? OnLog;

    /// <summary>Raised (on a background thread) after a reconnect finishes, so the UI can refresh tool availability.</summary>
    public event Action? Changed;

    /// <summary>Enables project-local MCP config discovery - a `.ideplusplus/copilot/mcp-servers.json`
    /// in the solution directory, checked into source control, adding to or overriding (by server
    /// name) the global user-level list on the next ReconnectAllAsync. Running one requires the
    /// user's approval first; see LoadMergedConfigsAsync.</summary>
    public void SetSolutionDirectory(string? solutionDirectory) => _solutionDirectory = solutionDirectory;

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
        await _reconnectLock.WaitAsync();
        try
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

            var configs = await LoadMergedConfigsAsync();
            var connected = await Task.WhenAll(configs.Select(ConnectOneAsync));

            lock (_connectionsLock)
            {
                _connections.AddRange(connected);
            }

            Changed?.Invoke();
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    /// <summary>
    /// The global user-level list, plus the open solution's checked-in list once the user has
    /// approved it.
    ///
    /// The approval step is the point. Every entry here is launched as `cmd.exe /c` by McpClient,
    /// and this runs on every solution load - so before it existed, cloning a repository and
    /// opening it executed whatever that repository's config named, with no prompt and no record.
    /// Approval is keyed to the config's content as well as the solution, so a repo whose config
    /// changes on the next pull has to be approved again rather than inheriting the old answer.
    /// </summary>
    private async Task<IReadOnlyList<McpServerConfig>> LoadMergedConfigsAsync()
    {
        var configs = _configStore.Load();
        if (_solutionDirectory is null)
        {
            return configs;
        }

        var projectConfigPath = Path.Combine(_solutionDirectory, ".ideplusplus", "copilot", "mcp-servers.json");
        var projectConfigs = new McpServerConfigStore(projectConfigPath).Load();
        if (projectConfigs.Count == 0)
        {
            return configs;
        }

        string configText;
        try
        {
            configText = File.ReadAllText(projectConfigPath);
        }
        catch (IOException ex)
        {
            OnLog?.Invoke($"[MCP] Could not read {projectConfigPath} to check it: {ex.Message}");
            return configs;
        }

        var fingerprint = McpProjectTrustStore.Fingerprint(configText);
        if (_trustStore.IsTrusted(_solutionDirectory, fingerprint))
        {
            return McpConfigMerger.Merge(configs, projectConfigs);
        }

        var names = string.Join(", ", projectConfigs.Select(c => c.Name));
        if (ProjectTrustPrompt is not { } prompt)
        {
            OnLog?.Invoke(
                $"[MCP] Skipped {projectConfigPath} - {projectConfigs.Count} project-local server(s) " +
                $"({names}) have not been approved for this solution.");
            return configs;
        }

        if (!await prompt(new McpProjectTrustRequest(_solutionDirectory, projectConfigPath, projectConfigs)))
        {
            OnLog?.Invoke($"[MCP] Declined {projectConfigPath} - {names} will not be started.");
            return configs;
        }

        _trustStore.Trust(_solutionDirectory, fingerprint);
        OnLog?.Invoke($"[MCP] Approved {projectConfigPath} - starting {names}.");
        return McpConfigMerger.Merge(configs, projectConfigs);
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
