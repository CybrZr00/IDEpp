using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.TestRunner;

namespace IdePlusPlus.Shell.ToolPanes;

public enum TestTreeNodeKind
{
    Project,
    Class,
    Method,
}

/// <summary>
/// Test Explorer's own Project&gt;Class&gt;Method tree node - deliberately not reusing
/// SolutionTreeNode, which has no notion of test outcome/duration/failure text. Mirrors
/// SolutionTreeNode's general ObservableObject/Children shape only.
/// </summary>
public sealed partial class TestTreeNode(string name, TestTreeNodeKind kind) : ObservableObject
{
    // Matches VSTest/xunit stack-trace lines like " at Namespace.Class.Method() in
    // C:\path\File.cs:line 14" - "in <path>:line <n>" is the stable suffix across frames; the
    // leading "at ..." varies too much (async state machines, generics) to be worth matching.
    [GeneratedRegex(@"in (?<path>.+):line (?<line>\d+)", RegexOptions.Compiled)]
    private static partial Regex StackFrameLocationRegex();

    public string Name { get; } = name;

    public TestTreeNodeKind Kind { get; } = kind;

    /// <summary>Set on Method-kind leaves only - the key used to look results up after a run.</summary>
    public string? FullyQualifiedName { get; init; }

    /// <summary>The project this node belongs to (itself, for Project-kind nodes) - lets a Method
    /// leaf be grouped back by owning project without walking Children upward (TestTreeNode has no
    /// parent pointer).</summary>
    public string? ProjectPath { get; init; }

    public ObservableCollection<TestTreeNode> Children { get; } = [];

    [ObservableProperty]
    private TestOutcome outcome;

    [ObservableProperty]
    private TimeSpan? duration;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string? stackTrace;

    /// <summary>Best-effort source location for double-click navigation, populated by scanning
    /// StackTrace for a recognizable "in &lt;path&gt;:line &lt;n&gt;" pattern. Both null - never
    /// throwing - when no such pattern is found.</summary>
    [ObservableProperty]
    private string? sourceFilePath;

    [ObservableProperty]
    private int? sourceLine;

    /// <summary>This node and every descendant, depth-first - mirrors SolutionTreeNode.Flatten().</summary>
    public IEnumerable<TestTreeNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Applies a parsed TestResult onto this leaf, best-effort deriving SourceFilePath/
    /// SourceLine from the stack trace text. Never throws - an unrecognized stack trace shape just
    /// leaves both null, and navigation on this node becomes a no-op.</summary>
    public void ApplyResult(TestResult result)
    {
        Outcome = result.Outcome;
        Duration = result.Duration;
        ErrorMessage = result.ErrorMessage;
        StackTrace = result.StackTrace;

        SourceFilePath = null;
        SourceLine = null;
        if (result.StackTrace is not { Length: > 0 } stackTrace)
        {
            return;
        }

        var match = StackFrameLocationRegex().Match(stackTrace);
        if (match.Success && int.TryParse(match.Groups["line"].Value, out var line))
        {
            SourceFilePath = match.Groups["path"].Value.Trim();
            SourceLine = line;
        }
    }
}
