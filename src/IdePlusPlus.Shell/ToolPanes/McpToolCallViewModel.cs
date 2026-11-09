using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>A chat-log entry for one tool invocation (MCP, built-in or plugin) - unlike
/// EditProposalViewModel, this is purely informational (no accept/reject). While it runs it shows a
/// spinner and a ticking elapsed time, and a progress line if the server reports one; the arguments
/// and full result are available in an expander.</summary>
public sealed partial class McpToolCallViewModel : ObservableObject
{
    private const int PreviewLength = 400;

    public McpToolCallViewModel(string toolCallId, string toolName, string? argumentsJson)
    {
        ToolCallId = toolCallId;
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
        StartedAt = DateTimeOffset.Now;
    }

    public string ToolCallId { get; }

    public string ToolName { get; }

    /// <summary>The raw arguments the model sent, kept for the same reason
    /// EditProposalViewModel.ArgumentsJson is - replaying this call into a later turn's history needs
    /// the real arguments, not just its name.</summary>
    public string? ArgumentsJson { get; }

    public bool HasArguments => !string.IsNullOrWhiteSpace(ArgumentsJson);

    public DateTimeOffset StartedAt { get; }

    [ObservableProperty]
    private string status = "Running...";

    [ObservableProperty]
    private bool isError;

    [ObservableProperty]
    private bool isRunning = true;

    [ObservableProperty]
    private string elapsedText = "";

    [ObservableProperty]
    private string? progressText;

    [ObservableProperty]
    private string? resultPreview;

    public bool HasProgress => !string.IsNullOrEmpty(ProgressText);

    /// <summary>The untruncated result, kept separately from ResultPreview (which is capped at
    /// PreviewLength for display) so a history replay can answer for this call faithfully instead of
    /// with a "…"-truncated fragment. Null until Complete() has run.</summary>
    public string? ResultText { get; private set; }

    /// <summary>Recomputes the elapsed-time label while the call is running. Driven by the chat view's
    /// timer so a long call visibly ticks instead of looking frozen.</summary>
    public void RefreshElapsed()
    {
        if (IsRunning)
        {
            ElapsedText = FormatElapsed(DateTimeOffset.Now - StartedAt);
        }
    }

    /// <summary>Sets the progress line from an MCP progress notification.</summary>
    public void ReportProgress(string? message, double? progress, double? total)
    {
        if (!IsRunning)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            ProgressText = progress is not null && total is > 0 ? $"{message} ({progress:F0}/{total:F0})" : message;
        }
        else if (progress is not null)
        {
            ProgressText = total is > 0 ? $"{progress:F0}/{total:F0}" : $"{progress:F0}";
        }
    }

    public void Complete(bool isError, string resultText)
    {
        IsRunning = false;
        IsError = isError;
        Status = isError ? "Error" : "Done";
        ElapsedText = FormatElapsed(DateTimeOffset.Now - StartedAt);
        ProgressText = null;
        ResultText = resultText;
        ResultPreview = resultText.Length > PreviewLength ? resultText[..PreviewLength] + "…" : resultText;
    }

    partial void OnProgressTextChanged(string? value) => OnPropertyChanged(nameof(HasProgress));

    private static string FormatElapsed(TimeSpan span) =>
        span.TotalSeconds < 1 ? $"{span.TotalMilliseconds:F0} ms" : $"{span.TotalSeconds:F1} s";
}
