using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell;

/// <summary>One editable row in the Settings MCP server list. Args/Env are edited as plain text and parsed on save.</summary>
public sealed partial class McpServerRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string name = "";

    [ObservableProperty]
    private string command = "";

    /// <summary>Space-separated, e.g. "-y @playwright/mcp@latest".</summary>
    [ObservableProperty]
    private string argsText = "";

    /// <summary>Semicolon-separated KEY=VALUE pairs, e.g. "API_KEY=abc123".</summary>
    [ObservableProperty]
    private string envText = "";

    [ObservableProperty]
    private string? statusText;
}
