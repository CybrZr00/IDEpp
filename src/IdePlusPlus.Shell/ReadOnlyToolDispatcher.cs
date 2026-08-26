using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Editor;
using IdePlusPlus.Roslyn;

namespace IdePlusPlus.Shell;

/// <summary>
/// Dispatches exactly SubAgentTools.AllowedTools' six tool calls against a given solution directory
/// - the shared implementation behind both investigate_parallel's sub-agents
/// (CopilotChatPaneViewModel.RunInvestigateParallelAsync) and scheduled automation runs
/// (ScheduledAutomationService), so this read-only behavior - including its exact wording - is
/// maintained in one place rather than drifting between two copies.
///
/// Deliberately WPF-free: none of these six tools' handlers ever touch the UI thread (see
/// SubAgentTools' own doc comment for why that fact matters), and this class exists specifically to
/// be usable from a context - a background scheduler timer - that has no UI thread to marshal onto
/// in the first place.
/// </summary>
public sealed class ReadOnlyToolDispatcher
{
    private const int CodebaseSearchTopK = 5;

    private readonly WorkspaceFileReader _workspaceFileReader;
    private readonly CodebaseIndexService _codebaseIndexService;
    private readonly RoslynWorkspaceService _roslynWorkspaceService;
    private readonly EditApplicationService _editApplicationService;

    public ReadOnlyToolDispatcher(
        WorkspaceFileReader workspaceFileReader,
        CodebaseIndexService codebaseIndexService,
        RoslynWorkspaceService roslynWorkspaceService,
        EditApplicationService editApplicationService)
    {
        _workspaceFileReader = workspaceFileReader;
        _codebaseIndexService = codebaseIndexService;
        _roslynWorkspaceService = roslynWorkspaceService;
        _editApplicationService = editApplicationService;
    }

    public async Task<(bool IsError, string Text)> ExecuteAsync(
        string toolName, JsonNode? arguments, string? solutionDirectory, CancellationToken cancellationToken)
    {
        if (solutionDirectory is null)
        {
            return (true, "No solution is open, so there is nothing to investigate.");
        }

        switch (toolName)
        {
            case WorkspaceTools.ReadFileToolName:
                var file = arguments?["file"]?.GetValue<string>();
                return file is null
                    ? (true, "read_file requires a 'file' argument.")
                    : (false, _workspaceFileReader.ReadFile(
                        solutionDirectory,
                        file,
                        arguments?["offset"]?.GetValue<int>(),
                        arguments?["limit"]?.GetValue<int>()));

            case WorkspaceTools.ListFilesToolName:
                var directory = arguments?["directory"]?.GetValue<string>();
                var recursive = arguments?["recursive"]?.GetValue<bool>() ?? false;
                return (false, _workspaceFileReader.ListFiles(solutionDirectory, directory, recursive));

            case WorkspaceTools.SearchCodebaseToolName:
                return await SearchCodebaseAsync(arguments?["query"]?.GetValue<string>(), cancellationToken);

            case SymbolTools.FindSymbolToolName:
                var symbolName = arguments?["name"]?.GetValue<string>();
                return symbolName is null
                    ? (true, "find_symbol requires a 'name' argument.")
                    : await FindSymbolAsync(symbolName, solutionDirectory, cancellationToken);

            case SymbolTools.FindReferencesToolName:
                var referenceName = arguments?["name"]?.GetValue<string>();
                return referenceName is null
                    ? (true, "find_references requires a 'name' argument.")
                    : await FindReferencesAsync(referenceName, solutionDirectory, cancellationToken);

            case DiagnosticTools.GetDiagnosticsToolName:
                var diagnosticFile = arguments?["file"]?.GetValue<string>();
                return diagnosticFile is null
                    ? (true, "get_diagnostics requires a 'file' argument.")
                    : await GetDiagnosticsAsync(diagnosticFile, solutionDirectory, cancellationToken);

            default:
                return (true, $"'{toolName}' is not available here.");
        }
    }

    private async Task<(bool IsError, string Text)> SearchCodebaseAsync(string? query, CancellationToken cancellationToken)
    {
        if (query is null)
        {
            return (true, "search_codebase requires a 'query' argument.");
        }

        if (!_codebaseIndexService.IsIndexed)
        {
            return (true, "The codebase isn't indexed yet - use read_file/list_files instead, " +
                          "or ask the user to click 'Index Codebase' in the Copilot Chat pane.");
        }

        var hits = await _codebaseIndexService.SearchAsync(query, CodebaseSearchTopK, cancellationToken);
        return hits.Count == 0
            ? (false, "No matches found.")
            : (false, string.Join("\n\n", hits.Select(c => $"// {c.FilePath}:{c.StartLine}-{c.EndLine}\n{c.Text}")));
    }

    private async Task<(bool IsError, string Text)> FindSymbolAsync(
        string name, string solutionDirectory, CancellationToken cancellationToken)
    {
        IReadOnlyList<SymbolLocation> symbols;
        try
        {
            symbols = await _roslynWorkspaceService.FindSymbolsAsync(name, SymbolTools.MaxResults, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (true, $"Symbol search failed: {ex.Message}");
        }

        if (symbols.Count == 0)
        {
            return (false,
                $"No C# symbol named '{name}' was found. The match is exact and case-sensitive on the "
                + "symbol's own name; try search_codebase if you are not sure what it is called.");
        }

        return (false, $"{symbols.Count} declaration(s) of '{name}':\n" + FormatSymbols(symbols, solutionDirectory));
    }

    private async Task<(bool IsError, string Text)> FindReferencesAsync(
        string name, string solutionDirectory, CancellationToken cancellationToken)
    {
        IReadOnlyList<SymbolLocation> declarations;
        IReadOnlyList<SymbolLocation> references;
        int totalReferences;
        try
        {
            (declarations, references, totalReferences) =
                await _roslynWorkspaceService.FindReferencesAsync(name, SymbolTools.MaxResults, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (true, $"Reference search failed: {ex.Message}");
        }

        if (declarations.Count == 0)
        {
            return (false, $"No C# symbol named '{name}' was found, so it has no references.");
        }

        // More than one declaration means more than one symbol shares this name - an overload set,
        // or unrelated types with a same-named member. Picking one would answer a question the
        // caller did not ask.
        if (references.Count == 0 && totalReferences == 0 && declarations.Count > 1)
        {
            return (false,
                $"'{name}' is ambiguous - {declarations.Count} declarations share that name, so references "
                + "were not resolved. Narrow it down from these:\n" + FormatSymbols(declarations, solutionDirectory));
        }

        if (totalReferences == 0)
        {
            return (false,
                $"'{name}' is declared but never referenced:\n" + FormatSymbols(declarations, solutionDirectory));
        }

        var report = new StringBuilder($"'{name}' has {totalReferences} reference(s).\n\nDeclared at:\n");
        report.Append(FormatSymbols(declarations, solutionDirectory));
        report.Append("\n\nReferenced at:\n");
        report.Append(FormatSymbols(references, solutionDirectory));

        if (totalReferences > references.Count)
        {
            report.Append($"\n\n[{totalReferences - references.Count} more reference(s) not shown]");
        }

        return (false, report.ToString());
    }

    /// <summary>Paths are made relative to the solution so they can be handed straight back to
    /// read_file, and the source line is included so a list of hits is readable without opening
    /// every one of them.</summary>
    private static string FormatSymbols(IReadOnlyList<SymbolLocation> symbols, string solutionDirectory) =>
        string.Join("\n", symbols.Select(s =>
        {
            var path = Path.GetRelativePath(solutionDirectory, s.FilePath).Replace('\\', '/');
            return $"  {path}:{s.Line}:{s.Column}  {s.Kind} {s.DisplayName}"
                   + (s.LineText.Length > 0 ? $"\n      {s.LineText}" : "");
        }));

    /// <summary>
    /// Compiler errors and warnings for one file, from the already-loaded Roslyn semantic model.
    ///
    /// Reads the editor's buffer rather than disk where the file is open, so a file edited but not
    /// saved is analysed as it stands - the same reason read_file does. Unlike build_solution this
    /// deliberately does not save anything: it is meant to be cheap enough to call after every edit,
    /// and writing to disk as a side effect of a read-only question would be a surprise.
    /// </summary>
    private async Task<(bool IsError, string Text)> GetDiagnosticsAsync(
        string file, string solutionDirectory, CancellationToken cancellationToken)
    {
        if (WorkspaceFileReader.ResolveWithinRoot(solutionDirectory, file) is not { } fullPath)
        {
            return (true, $"Refused: '{file}' resolves outside the open solution directory.");
        }

        if (!File.Exists(fullPath))
        {
            return (true, $"File not found: {file}");
        }

        if (!_roslynWorkspaceService.IsAnalysed(fullPath))
        {
            return (true,
                $"{file} is not part of the loaded C# solution, so it has no compiler diagnostics. "
                + "This covers non-C# files, and C# files added since the solution was opened. "
                + "Use build_solution instead.");
        }

        var unsavedText = _editApplicationService.TryGetUnsavedText(fullPath);

        IReadOnlyList<LiveDiagnostic> diagnostics;
        try
        {
            diagnostics = await _roslynWorkspaceService.GetDiagnosticsAsync(fullPath, unsavedText, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (true, $"Could not analyse {file}: {ex.Message}");
        }

        var errors = diagnostics.Where(d => d.Severity == "Error").ToList();
        var warnings = diagnostics.Where(d => d.Severity == "Warning").ToList();

        if (errors.Count == 0 && warnings.Count == 0)
        {
            return (false, $"{file}: no errors or warnings."
                           + (unsavedText is null ? "" : " (Analysed with your unsaved changes.)"));
        }

        var report = new StringBuilder($"{file}: {errors.Count} error(s), {warnings.Count} warning(s).");
        if (unsavedText is not null)
        {
            report.Append(" Analysed with your unsaved changes.");
        }

        foreach (var diagnostic in errors.Concat(warnings).Take(DiagnosticTools.MaxReportedDiagnostics))
        {
            report.Append(
                $"\n({diagnostic.Line},{diagnostic.Column}): "
                + $"{diagnostic.Severity.ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message}");
        }

        var total = errors.Count + warnings.Count;
        if (total > DiagnosticTools.MaxReportedDiagnostics)
        {
            report.Append($"\n\n[{total - DiagnosticTools.MaxReportedDiagnostics} more not shown]");
        }

        return (false, report.ToString());
    }
}
