using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>A chat-log entry for one MCP tool invocation - unlike EditProposalViewModel, this is purely informational (no accept/reject).</summary>
public sealed partial class McpToolCallViewModel : ObservableObject
{
    private const int PreviewLength = 400;

    public McpToolCallViewModel(string toolCallId, string toolName, string? argumentsJson)
    {
        ToolCallId = toolCallId;
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
    }

    public string ToolCallId { get; }

    public string ToolName { get; }

    /// <summary>The raw arguments the model sent, kept for the same reason
    /// EditProposalViewModel.ArgumentsJson is - replaying this call into a later turn's history needs
    /// the real arguments, not just its name.</summary>
    public string? ArgumentsJson { get; }

    [ObservableProperty]
    private string status = "Running...";

    [ObservableProperty]
    private bool isError;

    [ObservableProperty]
    private string? resultPreview;

    /// <summary>The untruncated result, kept separately from ResultPreview (which is capped at
    /// PreviewLength for display) so a history replay can answer for this call faithfully instead of
    /// with a "…"-truncated fragment. Null until Complete() has run.</summary>
    public string? ResultText { get; private set; }

    public void Complete(bool isError, string resultText)
    {
        IsError = isError;
        Status = isError ? "Error" : "Done";
        ResultText = resultText;
        ResultPreview = resultText.Length > PreviewLength ? resultText[..PreviewLength] + "…" : resultText;
    }
}
