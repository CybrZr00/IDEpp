using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>A chat-log entry for one MCP tool invocation - unlike EditProposalViewModel, this is purely informational (no accept/reject).</summary>
public sealed partial class McpToolCallViewModel : ObservableObject
{
    private const int PreviewLength = 400;

    public McpToolCallViewModel(string toolName)
    {
        ToolName = toolName;
    }

    public string ToolName { get; }

    [ObservableProperty]
    private string status = "Running...";

    [ObservableProperty]
    private bool isError;

    [ObservableProperty]
    private string? resultPreview;

    public void Complete(bool isError, string resultText)
    {
        IsError = isError;
        Status = isError ? "Error" : "Done";
        ResultPreview = resultText.Length > PreviewLength ? resultText[..PreviewLength] + "…" : resultText;
    }
}
