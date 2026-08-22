namespace IdePlusPlus.Copilot.Diff;

public sealed record EditOperation(string FilePath, string Mode, int? StartLine, int? EndLine, string NewText)
{
    public bool IsWholeFile => Mode == "whole_file";
}
