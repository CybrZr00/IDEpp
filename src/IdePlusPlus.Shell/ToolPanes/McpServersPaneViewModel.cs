using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Mcp;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Shows the health of every configured MCP server: connected or failed (with the error and a tail of
/// its stderr - the output that previously went nowhere a user could see), how many tools it exposes,
/// when it was last called, and a Restart button that reconnects just that one server. It refreshes
/// whenever the manager raises <see cref="McpServerManager.Changed"/>.
/// </summary>
public sealed partial class McpServersPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "McpServers";

    private readonly McpServerManager _manager;

    public McpServersPaneViewModel(McpServerManager manager)
        : base(PaneContentId, "MCP Servers")
    {
        _manager = manager;
        _manager.Changed += OnManagerChanged;
        Refresh();
    }

    public ObservableCollection<McpServerStatusRowViewModel> Servers { get; } = [];

    [ObservableProperty]
    private McpServerStatusRowViewModel? selectedServer;

    public bool HasServers => Servers.Count > 0;

    /// <summary>Marshals a background Changed notification onto the UI thread. Default is synchronous;
    /// the app sets it to a dispatcher post.</summary>
    public Action<Action> Post { get; set; } = static action => action();

    private void OnManagerChanged() => Post(Refresh);

    [RelayCommand]
    private void Refresh() => ApplyStatuses(_manager.ServerStatuses);

    /// <summary>Reconciles the rows to a set of statuses in place (add new, update existing, drop
    /// gone), preserving the selection. Separated from <see cref="Refresh"/> so it can be tested
    /// without a live server.</summary>
    internal void ApplyStatuses(IReadOnlyList<McpServerStatus> statuses)
    {
        for (var i = Servers.Count - 1; i >= 0; i--)
        {
            if (!statuses.Any(s => string.Equals(s.Name, Servers[i].Name, StringComparison.Ordinal)))
            {
                Servers.RemoveAt(i);
            }
        }

        foreach (var status in statuses)
        {
            var row = Servers.FirstOrDefault(r => string.Equals(r.Name, status.Name, StringComparison.Ordinal));
            if (row is null)
            {
                row = new McpServerStatusRowViewModel(status.Name);
                Servers.Add(row);
            }

            row.Update(status);
        }

        SelectedServer ??= Servers.FirstOrDefault();
        OnPropertyChanged(nameof(HasServers));
    }

    [RelayCommand]
    private async Task RestartAsync(McpServerStatusRowViewModel? row)
    {
        var target = row ?? SelectedServer;
        if (target is null)
        {
            return;
        }

        target.StatusText = "Restarting...";
        await _manager.RestartServerAsync(target.Name);
    }
}

/// <summary>One server's row in the MCP Servers pane. Updated in place so the list doesn't flicker and
/// the selection survives a refresh.</summary>
public sealed partial class McpServerStatusRowViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private int toolCount;

    [ObservableProperty]
    private string? error;

    [ObservableProperty]
    private string lastCallText = "No calls yet.";

    [ObservableProperty]
    private string recentStderr = "";

    public void Update(McpServerStatus status)
    {
        IsConnected = status.IsConnected;
        ToolCount = status.ToolCount;
        Error = status.Error;
        StatusText = status.IsConnected
            ? $"Connected - {status.ToolCount} tool(s)"
            : $"Failed: {status.Error}";
        LastCallText = status.LastCallAt is { } at
            ? $"Last call {at.LocalDateTime:HH:mm:ss}" + (status.LastDurationMs is { } ms ? $" ({ms:F0} ms)" : "")
            : "No calls yet.";
        RecentStderr = string.Join("\n", status.RecentStderr);
    }
}
