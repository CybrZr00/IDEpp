using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace IdePlusPlus.Roslyn.Mutation;

/// <summary>An inclusive, 1-based range of lines - what a merge changed. Line 1 is the file's first
/// line, the way an editor and a git hunk header count them.</summary>
public readonly record struct LineRange(int Start, int End)
{
    public bool Contains(int line) => line >= Start && line <= End;

    /// <summary>The ranges a unified diff's <c>@@ ... +new,count @@</c> headers name on the new side.
    /// Hunks are read, not the lines inside them, so this is only exact for a zero-context diff
    /// (<c>git diff -U0</c>); a diff with context lines widens each range by its context.</summary>
    public static IReadOnlyList<LineRange> FromUnifiedDiff(string? unifiedDiff)
    {
        if (string.IsNullOrEmpty(unifiedDiff))
        {
            return [];
        }

        var ranges = new List<LineRange>();
        foreach (var line in unifiedDiff.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            // "@@ -12,4 +13,6 @@ optional heading"
            var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
            if (plus < 0)
            {
                continue;
            }

            var end = trimmed.IndexOf(' ', plus);
            var spec = end < 0 ? trimmed[(plus + 1)..] : trimmed[(plus + 1)..end];
            var comma = spec.IndexOf(',');
            var startText = comma < 0 ? spec : spec[..comma];
            var countText = comma < 0 ? "1" : spec[(comma + 1)..];

            if (!int.TryParse(startText, out var start))
            {
                continue;
            }

            var count = int.TryParse(countText, out var parsedCount) ? parsedCount : 1;

            // A count of 0 means the hunk only deletes lines: there is no new-side line to mutate.
            if (count <= 0)
            {
                continue;
            }

            ranges.Add(new LineRange(start, start + count - 1));
        }

        return ranges;
    }
}

/// <summary>
/// One mutation of the source: replacing <see cref="Span"/> with <see cref="Replacement"/> makes the
/// program behave differently. <see cref="Line"/> and <see cref="Column"/> are 1-based, so a report
/// can say exactly where to look. Applying a mutant is a plain text splice - the generator never
/// reformats the file, so an unmutated line stays byte-for-byte what it was.
/// </summary>
public sealed record Mutant(
    int Line,
    int Column,
    string Kind,
    string Description,
    TextSpan Span,
    string Replacement)
{
    public string ApplyTo(string source) =>
        string.Concat(source.AsSpan(0, Span.Start), Replacement.AsSpan(), source.AsSpan(Span.End));
}

/// <summary>
/// Produces small, behaviour-changing edits of C# source with the Roslyn syntax tree, for the Ultra
/// mutation tester to try against the tests. It is deliberately syntactic, not semantic: no
/// compilation, no symbol information, so it runs in milliseconds on a file a merge just changed.
/// The trade is that a mutant may not compile (removing a call whose result was used, for instance);
/// the runner reports those separately rather than counting them as killed.
///
/// Mutants are produced only inside the line ranges the merge changed - re-testing operators in code
/// nobody touched buries the team in survivors it cannot act on.
/// </summary>
public static class MutantGenerator
{
    /// <summary>How many mutants one review item gets unless the caller asks for fewer. Enough to
    /// find the obvious gaps in a changed method, few enough that one review item stays minutes not
    /// tens of minutes.</summary>
    public const int DefaultMaxMutants = 12;

    /// <summary>The most any caller may ask for - the mutation tester's tool schema uses it too.</summary>
    public const int MaxMutants = 12;

    /// <summary>
    /// Mutants in <paramref name="source"/> whose operators or statements sit inside
    /// <paramref name="changedLines"/>. Null or empty ranges mean the whole file, which is what a
    /// caller that has no diff (a test, or a brand-new file) gets.
    /// </summary>
    public static IReadOnlyList<Mutant> Generate(
        string source, IReadOnlyList<LineRange>? changedLines = null, int maxMutants = DefaultMaxMutants)
    {
        if (string.IsNullOrEmpty(source))
        {
            return [];
        }

        var limit = Math.Clamp(maxMutants, 1, MaxMutants);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        bool InScope(TextSpan span)
        {
            if (changedLines is not { Count: > 0 })
            {
                return true;
            }

            var location = tree.GetLineSpan(span);
            var first = location.StartLinePosition.Line + 1;
            var last = location.EndLinePosition.Line + 1;
            return changedLines.Any(range => range.Start <= last && first <= range.End);
        }

        var found = new List<Mutant>();
        var seen = new HashSet<(int Start, int Length, string Replacement)>();

        void Add(TextSpan span, int line, int column, string kind, string description, string replacement)
        {
            if (!seen.Add((span.Start, span.Length, replacement)))
            {
                return;
            }

            found.Add(new Mutant(line, column, kind, description, span, replacement));
        }

        void AddToken(SyntaxToken token, string kind, string description, string replacement)
        {
            if (!InScope(token.Span))
            {
                return;
            }

            var position = token.GetLocation().GetLineSpan().StartLinePosition;
            Add(token.Span, position.Line + 1, position.Character + 1, kind, description, replacement);
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BinaryExpressionSyntax binary:
                    AddBinary(binary);
                    break;

                case IfStatementSyntax ifStatement:
                    AddNegation(ifStatement.Condition);
                    break;

                case WhileStatementSyntax whileStatement:
                    AddNegation(whileStatement.Condition);
                    break;

                case DoStatementSyntax doStatement:
                    AddNegation(doStatement.Condition);
                    break;

                case ConditionalExpressionSyntax conditional:
                    AddNegation(conditional.Condition);
                    break;

                case ReturnStatementSyntax returnStatement:
                    AddReturnDefault(returnStatement);
                    break;

                case ExpressionStatementSyntax statement:
                    AddRemovedCall(statement);
                    break;
            }
        }

        return found
            .OrderBy(m => m.Span.Start)
            .ThenBy(m => m.Span.Length)
            .Take(limit)
            .ToList();

        void AddBinary(BinaryExpressionSyntax binary)
        {
            var token = binary.OperatorToken;
            var expression = Cap(binary.ToString(), 60);
            switch (token.Kind())
            {
                case SyntaxKind.LessThanToken:
                    AddToken(token, "relational", $"flip '<' to '>' in {expression}", ">");
                    AddToken(token, "boundary", $"boundary '<' to '<=' in {expression}", "<=");
                    break;

                case SyntaxKind.GreaterThanToken:
                    AddToken(token, "relational", $"flip '>' to '<' in {expression}", "<");
                    AddToken(token, "boundary", $"boundary '>' to '>=' in {expression}", ">=");
                    break;

                case SyntaxKind.LessThanEqualsToken:
                    AddToken(token, "relational", $"flip '<=' to '>=' in {expression}", ">=");
                    AddToken(token, "boundary", $"boundary '<=' to '<' in {expression}", "<");
                    break;

                case SyntaxKind.GreaterThanEqualsToken:
                    AddToken(token, "relational", $"flip '>=' to '<=' in {expression}", "<=");
                    AddToken(token, "boundary", $"boundary '>=' to '>' in {expression}", ">");
                    break;

                case SyntaxKind.EqualsEqualsToken:
                    AddToken(token, "equality", $"flip '==' to '!=' in {expression}", "!=");
                    break;

                case SyntaxKind.ExclamationEqualsToken:
                    AddToken(token, "equality", $"flip '!=' to '==' in {expression}", "==");
                    break;

                case SyntaxKind.AmpersandAmpersandToken:
                    AddToken(token, "logical", $"swap '&&' to '||' in {expression}", "||");
                    break;

                case SyntaxKind.BarBarToken:
                    AddToken(token, "logical", $"swap '||' to '&&' in {expression}", "&&");
                    break;
            }
        }

        void AddNegation(ExpressionSyntax condition)
        {
            if (!InScope(condition.Span)
                || condition is LiteralExpressionSyntax
                || condition is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression })
            {
                return;
            }

            var text = condition.ToString();
            var position = condition.GetLocation().GetLineSpan().StartLinePosition;
            Add(
                condition.Span, position.Line + 1, position.Character + 1,
                "negation", $"negate condition {Cap(text, 60)}", $"!({text})");
        }

        void AddReturnDefault(ReturnStatementSyntax statement)
        {
            if (statement.Expression is not { } expression
                || !InScope(statement.Span)
                || expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression }
                || expression is DefaultExpressionSyntax)
            {
                return;
            }

            var position = expression.GetLocation().GetLineSpan().StartLinePosition;
            Add(
                expression.Span, position.Line + 1, position.Character + 1,
                "return-default", $"return default instead of {Cap(expression.ToString(), 60)}", "default!");
        }

        void AddRemovedCall(ExpressionStatementSyntax statement)
        {
            if (statement.Expression is not InvocationExpressionSyntax invocation || !InScope(statement.Span))
            {
                return;
            }

            // ';' rather than an empty string: an embedded statement ('if (x) Foo();') needs a
            // statement there, and a lone ';' is a valid empty one inside a block too.
            var position = statement.GetLocation().GetLineSpan().StartLinePosition;
            Add(
                statement.Span, position.Line + 1, position.Character + 1,
                "call-removal", $"remove call {Cap(invocation.Expression.ToString(), 60)}(...)", ";");
        }
    }

    private static string Cap(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}

