namespace IdePlusPlus.Copilot.Diff;

public sealed record EditOperation(
    string FilePath, string Mode, int? StartLine, int? EndLine, string NewText, string? OldText = null)
{
    public bool IsWholeFile => Mode == "whole_file";

    public bool IsReplaceText => Mode == "replace_text";
}
