namespace IdePlusPlus.Roslyn;

/// <summary>
/// One place a symbol is declared or used, with no Roslyn types on it - the same reason
/// LiveDiagnostic exists, so callers outside this assembly never take a Microsoft.CodeAnalysis
/// dependency just to read a result.
///
/// Line and Column are 1-based, matching LiveDiagnostic and the line numbers read_file prints,
/// so a result here can be handed straight to propose_edit's replace_range.
/// </summary>
public sealed record SymbolLocation(
    string Kind,
    string DisplayName,
    string FilePath,
    int Line,
    int Column,
    string LineText);
