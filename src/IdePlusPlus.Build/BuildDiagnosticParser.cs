using System.Text.RegularExpressions;

namespace IdePlusPlus.Build;

/// <summary>
/// Parses MSBuild's canonical console diagnostic format:
/// "path(line,col): error CS0246: message [project.csproj]" (the trailing "[project]" suffix is
/// optional and dropped). Used to turn BuildService's raw output-line callback into structured
/// diagnostics for an Error List pane, without needing a separate MSBuild logger/binlog integration.
/// </summary>
public static partial class BuildDiagnosticParser
{
    [GeneratedRegex(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error|warning)\s+(?<code>\S+):\s+(?<message>.+?)(\s+\[.+\])?$")]
    private static partial Regex Pattern();

    public static BuildDiagnostic? TryParse(string outputLine)
    {
        var match = Pattern().Match(outputLine);
        if (!match.Success)
        {
            return null;
        }

        var severity = match.Groups["severity"].Value == "error"
            ? BuildDiagnosticSeverity.Error
            : BuildDiagnosticSeverity.Warning;

        return new BuildDiagnostic(
            severity,
            match.Groups["file"].Value,
            int.Parse(match.Groups["line"].Value),
            int.Parse(match.Groups["col"].Value),
            match.Groups["code"].Value,
            match.Groups["message"].Value.Trim());
    }
}
