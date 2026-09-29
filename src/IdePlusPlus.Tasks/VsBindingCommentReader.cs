using System.Text.RegularExpressions;

namespace IdePlusPlus.Tasks;

/// <summary>What a Visual Studio binding comment asks for.</summary>
/// <param name="Bindings">Bindings IDE++ can honour, in the order the attributes appeared.</param>
/// <param name="UnsupportedProjectOpenTasks">
/// Tasks bound to VS's <c>ProjectOpen</c>, which IDE++ has no equivalent of - reported rather than
/// dropped silently, so the reason a task is not running can be said out loud.
/// </param>
public sealed record VsBindingComment(
    IReadOnlyList<VsBinding> Bindings,
    IReadOnlyList<string> UnsupportedProjectOpenTasks);

public sealed record VsBinding(string TaskName, TaskBindingKind Kind);

/// <summary>
/// Reads the binding comment Visual Studio writes at the top of a gulpfile/Gruntfile:
///
/// <code>/// &lt;binding BeforeBuild='sass' Clean='clean' /&gt;</code>
///
/// Read-only, deliberately: IDE++ never writes this comment back. A repo already configured in VS
/// therefore works here on first open, while IDE++'s own bindings live in .ideplusplus/tasks.json
/// and stay out of the user's source file. The consequence is that bindings made here are invisible
/// to VS, which the Task Runner pane says out loud rather than leaving to be discovered.
///
/// Only JavaScript manifests carry this comment. A package.json cannot: the comment would make it
/// invalid JSON, which is why npm-script bindings have no VS counterpart to import.
/// </summary>
public static partial class VsBindingCommentReader
{
    /// <summary>VS writes the comment at the very top; this is generous enough for a licence header
    /// above it without reading a whole bundled file looking for something that is not there.</summary>
    private const int LinesToScan = 30;

    [GeneratedRegex(@"<binding\b(?<attributes>[^>]*?)/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BindingElementRegex();

    [GeneratedRegex(@"(?<name>\w+)\s*=\s*(?<quote>['""])(?<value>.*?)\k<quote>", RegexOptions.Compiled)]
    private static partial Regex AttributeRegex();

    /// <summary>Returns null when the file is unreadable or has no binding comment - both mean
    /// "nothing to import", which is the common case and not an error.</summary>
    public static VsBindingComment? Read(string manifestPath)
    {
        try
        {
            using var reader = new StreamReader(manifestPath);
            var lines = new List<string>();
            for (var i = 0; i < LinesToScan && reader.ReadLine() is { } line; i++)
            {
                lines.Add(line);
            }

            return Parse(string.Join('\n', lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static VsBindingComment? Parse(string text)
    {
        var element = BindingElementRegex().Match(text);
        if (!element.Success)
        {
            return null;
        }

        var bindings = new List<VsBinding>();
        var projectOpen = new List<string>();

        foreach (Match attribute in AttributeRegex().Matches(element.Groups["attributes"].Value))
        {
            var name = attribute.Groups["name"].Value;
            var tasks = SplitTaskNames(attribute.Groups["value"].Value);

            // VS's attribute names, not IDE++'s: "Clean" is VS's name for what IDE++ calls Before
            // Clean, and there is no VS attribute matching After Clean.
            if (name.Equals("ProjectOpen", StringComparison.OrdinalIgnoreCase))
            {
                projectOpen.AddRange(tasks);
                continue;
            }

            var kind = name switch
            {
                _ when name.Equals("BeforeBuild", StringComparison.OrdinalIgnoreCase) => TaskBindingKind.BeforeBuild,
                _ when name.Equals("AfterBuild", StringComparison.OrdinalIgnoreCase) => TaskBindingKind.AfterBuild,
                _ when name.Equals("Clean", StringComparison.OrdinalIgnoreCase) => TaskBindingKind.BeforeClean,
                _ => (TaskBindingKind?)null,
            };

            if (kind is { } resolved)
            {
                bindings.AddRange(tasks.Select(task => new VsBinding(task, resolved)));
            }
        }

        return new VsBindingComment(bindings, projectOpen);
    }

    /// <summary>VS writes a comma-separated list for more than one task on the same event.</summary>
    private static IReadOnlyList<string> SplitTaskNames(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
