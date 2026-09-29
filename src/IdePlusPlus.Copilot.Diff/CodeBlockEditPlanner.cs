using System.Text;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Copilot.Diff;

/// <summary>A fenced code block from a chat reply. <see cref="Info"/> is whatever followed the
/// opening fence (usually the language, sometimes a path); <see cref="Preceding"/> is the prose just
/// before it, where models tend to name the file.</summary>
public sealed record ReplyCodeBlock(string Info, string Code, string Preceding);

/// <summary>
/// Turns a code block in a plain-text reply into an <see cref="EditOperation"/>, for models that
/// have no tool calling and can only show code.
///
/// Deliberately conservative: it only produces an operation when it can say exactly which lines are
/// being replaced (a whole file, or a member found by its signature in a C-family file). Anything
/// it cannot place is skipped rather than guessed at - a wrong guess here overwrites the user's
/// code, and the diff review that follows is a backstop, not an excuse.
/// </summary>
public static partial class CodeBlockEditPlanner
{
    private static readonly HashSet<string> CFamilyExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs", ".java", ".js", ".ts", ".tsx", ".jsx", ".c", ".cpp", ".h", ".go", ".rs", ".kt", ".swift", ".php" };

    public static IReadOnlyList<ReplyCodeBlock> ExtractBlocks(string markdown)
    {
        var blocks = new List<ReplyCodeBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var prose = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                prose.AppendLine(lines[i]);
                continue;
            }

            var info = trimmed.TrimStart('`').Trim();
            var code = new StringBuilder();
            var closed = false;
            for (i++; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    closed = true;
                    break;
                }

                code.AppendLine(lines[i]);
            }

            // An unclosed fence is a reply that was cut off; applying half a method would be worse
            // than applying nothing.
            if (closed)
            {
                var precedingText = prose.ToString();
                blocks.Add(new ReplyCodeBlock(
                    info, code.ToString().TrimEnd('\n', '\r'),
                    precedingText.Length > 400 ? precedingText[^400..] : precedingText));
            }

            prose.Clear();
        }

        return blocks;
    }

    /// <summary>Filename-looking tokens the reply gives for a block: in the fence info, a leading
    /// comment (`// File: Foo.cs`), or backticked in the preceding prose - most specific first.</summary>
    public static IReadOnlyList<string> PathHints(ReplyCodeBlock block)
    {
        var hints = new List<string>();
        foreach (Match m in PathToken().Matches(block.Info))
        {
            hints.Add(m.Value);
        }

        var firstLine = block.Code.Split('\n', 2)[0];
        if (firstLine.TrimStart().StartsWith("//", StringComparison.Ordinal)
            || firstLine.TrimStart().StartsWith('#'))
        {
            foreach (Match m in PathToken().Matches(firstLine))
            {
                hints.Add(m.Value);
            }
        }

        foreach (Match m in BacktickedPath().Matches(block.Preceding))
        {
            hints.Add(m.Groups[1].Value);
        }

        hints.Reverse(); // prose mentions were added last but the nearest one is the last in the text
        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Plans the edit of <paramref name="filePath"/> (workspace-relative, as the operation will name
    /// it) that <paramref name="block"/> describes. <paramref name="currentContent"/> is null for a
    /// file that does not exist yet.
    /// </summary>
    public static bool TryPlan(
        ReplyCodeBlock block, string filePath, string? currentContent, out EditOperation operation, out string reason)
    {
        operation = null!;
        var code = block.Code.Replace("\r\n", "\n");
        if (string.IsNullOrWhiteSpace(code))
        {
            reason = "the code block is empty";
            return false;
        }

        if (currentContent is null)
        {
            operation = new EditOperation(filePath, "whole_file", null, null, code);
            reason = "";
            return true;
        }

        var fileLines = currentContent.Replace("\r\n", "\n").Split('\n');
        var blockLines = code.Split('\n');

        if (LooksLikeWholeFile(blockLines, fileLines))
        {
            operation = new EditOperation(filePath, "whole_file", null, null, code);
            reason = "";
            return true;
        }

        if (!CFamilyExtensions.Contains(Path.GetExtension(filePath)))
        {
            reason = "only whole files can be placed automatically for this file type";
            return false;
        }

        return TryPlanMemberReplacement(filePath, blockLines, fileLines, out operation, out reason);
    }

    private static bool LooksLikeWholeFile(string[] blockLines, string[] fileLines)
    {
        var significant = blockLines.Count(l => l.Trim().Length > 0);
        var startsLikeAFile = blockLines
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))
            is { } first
            && (first.StartsWith("using ", StringComparison.Ordinal)
                || first.StartsWith("namespace ", StringComparison.Ordinal)
                || first.StartsWith("import ", StringComparison.Ordinal)
                || first.StartsWith("package ", StringComparison.Ordinal)
                || first.StartsWith("#include", StringComparison.Ordinal));

        // A block that opens like a file and is about as long as the one it replaces. Size matters
        // too: a using-led snippet that is a fifth of the file is an excerpt, not a replacement.
        var fileSignificant = fileLines.Count(l => l.Trim().Length > 0);
        return startsLikeAFile && significant * 10 >= fileSignificant * 6;
    }

    private static bool TryPlanMemberReplacement(
        string filePath, string[] blockLines, string[] fileLines, out EditOperation operation, out string reason)
    {
        operation = null!;

        // The block may start with attributes / doc comments; the signature is the first line that
        // is none of those.
        var signatureIndexInBlock = Array.FindIndex(blockLines, IsSignatureCandidate);
        if (signatureIndexInBlock < 0)
        {
            reason = "no member declaration found in the code block";
            return false;
        }

        var signature = blockLines[signatureIndexInBlock].Trim();
        var nameMatch = MemberName().Match(signature);
        if (!nameMatch.Success)
        {
            reason = "could not read a member name from the first declaration";
            return false;
        }

        var name = nameMatch.Groups[1].Value;
        var candidates = new List<int>();
        for (var i = 0; i < fileLines.Length; i++)
        {
            var line = fileLines[i].Trim();
            if (IsSignatureCandidate(fileLines[i]) && MemberName().Match(line) is { Success: true } m && m.Groups[1].Value == name)
            {
                candidates.Add(i);
            }
        }

        if (candidates.Count > 1)
        {
            var exact = candidates.Where(i => Normalize(fileLines[i]) == Normalize(signature)).ToList();
            if (exact.Count == 1)
            {
                candidates = exact;
            }
        }

        if (candidates.Count != 1)
        {
            reason = candidates.Count == 0
                ? $"'{name}' was not found in {filePath}, so there is nothing to replace"
                : $"'{name}' is declared {candidates.Count} times in {filePath}, so it is ambiguous which to replace";
            return false;
        }

        var start = candidates[0];
        var end = FindMemberEnd(fileLines, start);
        if (end < 0)
        {
            reason = $"could not find the end of '{name}' in {filePath}";
            return false;
        }

        // Attributes and doc comments above the member stay in the file unless the block brings its
        // own, in which case they are part of what is being replaced.
        if (signatureIndexInBlock > 0)
        {
            while (start > 0 && IsDecoration(fileLines[start - 1]))
            {
                start--;
            }
        }

        var replacement = blockLines;

        var indent = LeadingWhitespace(fileLines[candidates[0]]);
        var text = string.Join("\n", Reindent(replacement, indent));

        operation = new EditOperation(filePath, "replace_range", start + 1, end + 1, text);
        reason = "";
        return true;
    }

    private static bool IsDecoration(string line)
    {
        var t = line.Trim();
        return t.StartsWith('[') || t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal);
    }

    private static bool IsSignatureCandidate(string line)
    {
        var t = line.Trim();

        // A declaration, not a statement: "x.Foo(y);" and "return Foo(y);" have a call where a
        // declaration has a type, and a trailing ';' marks the statement (an expression-bodied
        // member ends in ';' too, but has "=>").
        return t.Length > 0
               && !IsDecoration(line)
               && MemberName().IsMatch(t)
               && (!t.EndsWith(';') || t.Contains("=>"));
    }

    /// <summary>Index of the line holding the member's closing brace (or its `;` for an
    /// expression-bodied or abstract member), scanning from the signature line.</summary>
    private static int FindMemberEnd(string[] lines, int start)
    {
        var depth = 0;
        var seenBrace = false;
        var parenDepth = 0;
        var inBlockComment = false;

        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (inBlockComment)
                {
                    if (ch == '*' && c + 1 < line.Length && line[c + 1] == '/')
                    {
                        inBlockComment = false;
                        c++;
                    }

                    continue;
                }

                if (ch == '/' && c + 1 < line.Length)
                {
                    if (line[c + 1] == '/')
                    {
                        break;
                    }

                    if (line[c + 1] == '*')
                    {
                        inBlockComment = true;
                        c++;
                        continue;
                    }
                }

                if (ch is '"' or '\'')
                {
                    // Skip a string/char literal on this line. Verbatim and raw strings that span
                    // lines are rare inside a member body and would only make us fail closed below.
                    for (c++; c < line.Length && line[c] != ch; c++)
                    {
                        if (line[c] == '\\')
                        {
                            c++;
                        }
                    }

                    continue;
                }

                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        parenDepth--;
                        break;
                    case '{':
                        depth++;
                        seenBrace = true;
                        break;
                    case '}':
                        depth--;
                        if (seenBrace && depth == 0)
                        {
                            return i;
                        }

                        break;
                    case ';' when !seenBrace && parenDepth <= 0:
                        return i; // expression-bodied / abstract / interface member
                }
            }
        }

        return -1;
    }

    private static IEnumerable<string> Reindent(string[] lines, string targetIndent)
    {
        var significant = lines.Where(l => l.Trim().Length > 0).ToList();
        if (significant.Count == 0)
        {
            return lines;
        }

        // Relative to the block's own shallowest line, so a block pasted flush-left and one already
        // indented as a class member both land at the file's indentation.
        var common = significant.Min(l => LeadingWhitespace(l).Length);
        return lines.Select(l => l.Trim().Length == 0 ? "" : targetIndent + l[common..].TrimEnd());
    }

    private static string LeadingWhitespace(string line) => line[..(line.Length - line.TrimStart().Length)];

    private static string Normalize(string line) => WhitespaceRun().Replace(line.Trim(), " ");

    [GeneratedRegex(@"[\w./\\-]+\.[A-Za-z]{1,5}\b")]
    private static partial Regex PathToken();

    [GeneratedRegex(@"`([\w./\\-]+\.[A-Za-z]{1,5})`")]
    private static partial Regex BacktickedPath();

    // The identifier directly before the first '(' that follows at least one other token, so
    // "public static async Task CreatePdf(" yields CreatePdf but "Console.WriteLine(" (no type
    // before it) and "if (" / "foreach (" do not.
    [GeneratedRegex(@"^(?!(?:if|for|foreach|while|switch|using|lock|return|catch|else|new|await|throw|yield|do|case)\b)[\w<>\[\],?.\s]*\s(\w+)\s*(?:<[^>(]*>)?\s*\(")]
    private static partial Regex MemberName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
