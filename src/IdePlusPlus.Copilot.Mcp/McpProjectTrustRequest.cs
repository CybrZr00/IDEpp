namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// A solution's checked-in MCP config, awaiting the user's decision on whether to run it.
/// <see cref="Servers"/> is what would be launched, so a prompt can show the actual commands
/// rather than asking the user to trust a filename.
/// </summary>
public sealed record McpProjectTrustRequest(
    string SolutionDirectory,
    string ConfigPath,
    IReadOnlyList<McpServerConfig> Servers);
