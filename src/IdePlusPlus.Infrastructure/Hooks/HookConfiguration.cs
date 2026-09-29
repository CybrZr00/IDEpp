using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Infrastructure.Hooks;

public static class HookEvents
{
    /// <summary>Before a tool runs. A hook can veto the call.</summary>
    public const string PreToolUse = "PreToolUse";

    /// <summary>After a tool ran. A hook's feedback is handed back to the model.</summary>
    public const string PostToolUse = "PostToolUse";

    public static bool IsSupported(string name) => name is PreToolUse or PostToolUse;
}

/// <summary>
/// One hook command. When <see cref="Args"/> is set the command is started directly with that
/// argument list - no shell in between, so paths with spaces and backslashes need no quoting, which
/// is the reason to prefer it on Windows (`"command": "python", "args": ["C:\\my dir\\hook.py"]`).
/// When it is null the whole <see cref="Command"/> is a PowerShell command line.
/// </summary>
public sealed record HookCommand(string Command, IReadOnlyList<string>? Args, int TimeoutSeconds);

/// <summary>A matcher and the hooks it guards. A null matcher matches every tool.</summary>
public sealed record HookMatcherGroup(string? Matcher, IReadOnlyList<HookCommand> Hooks)
{
    public bool Matches(IReadOnlyList<string> toolNames)
    {
        if (string.IsNullOrEmpty(Matcher) || Matcher == "*")
        {
            return true;
        }

        try
        {
            // Anchored, like Claude Code's own matchers: "Edit" must not also match "MultiEdit".
            var pattern = $"^(?:{Matcher})$";
            return toolNames.Any(name => Regex.IsMatch(name, pattern, RegexOptions.CultureInvariant));
        }
        catch (ArgumentException)
        {
            // A malformed regex matches nothing rather than everything: failing open would run a
            // hook the author never scoped to this tool.
            return false;
        }
    }
}

/// <summary>
/// The `hooks` section of a settings file, in the same shape Claude Code uses:
///
///   { "hooks": { "PostToolUse": [ { "matcher": "Write|Edit",
///       "hooks": [ { "type": "command", "command": "python", "args": ["hook.py"], "timeout": 30 } ] } ] } }
///
/// Parsing is forgiving about content and strict about nothing: an entry that cannot be understood
/// is skipped and reported in <see cref="Warnings"/>, so one typo does not silently disable the
/// hooks around it.
/// </summary>
public sealed class HookConfiguration
{
    public const int DefaultTimeoutSeconds = 60;

    public static HookConfiguration Empty { get; } = new(new Dictionary<string, IReadOnlyList<HookMatcherGroup>>(), []);

    private readonly IReadOnlyDictionary<string, IReadOnlyList<HookMatcherGroup>> _groupsByEvent;

    private HookConfiguration(
        IReadOnlyDictionary<string, IReadOnlyList<HookMatcherGroup>> groupsByEvent, IReadOnlyList<string> warnings)
    {
        _groupsByEvent = groupsByEvent;
        Warnings = warnings;
    }

    public IReadOnlyList<string> Warnings { get; }

    public bool IsEmpty => _groupsByEvent.Count == 0;

    public bool HasHooksFor(string eventName) => _groupsByEvent.ContainsKey(eventName);

    /// <summary>Every command whose matcher accepts any of <paramref name="toolNames"/> - a tool can
    /// be known by several names (its own and the Claude Code equivalents), and a matcher written
    /// against either should fire.</summary>
    public IReadOnlyList<HookCommand> CommandsFor(string eventName, IReadOnlyList<string> toolNames) =>
        _groupsByEvent.TryGetValue(eventName, out var groups)
            ? groups.Where(g => g.Matches(toolNames)).SelectMany(g => g.Hooks).ToList()
            : [];

    /// <summary>Concatenates, user-level first - hooks add up, they do not override each other.</summary>
    public static HookConfiguration Combine(params HookConfiguration[] configurations)
    {
        var merged = new Dictionary<string, List<HookMatcherGroup>>();
        var warnings = new List<string>();
        foreach (var configuration in configurations)
        {
            warnings.AddRange(configuration.Warnings);
            foreach (var (eventName, groups) in configuration._groupsByEvent)
            {
                if (!merged.TryGetValue(eventName, out var list))
                {
                    merged[eventName] = list = [];
                }

                list.AddRange(groups);
            }
        }

        return new HookConfiguration(
            merged.ToDictionary(p => p.Key, p => (IReadOnlyList<HookMatcherGroup>)p.Value), warnings);
    }

    public static HookConfiguration Parse(string json, string sourceName)
    {
        var warnings = new List<string>();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return new HookConfiguration(
                new Dictionary<string, IReadOnlyList<HookMatcherGroup>>(),
                [$"{sourceName}: not valid JSON - {ex.Message}"]);
        }

        var groupsByEvent = new Dictionary<string, IReadOnlyList<HookMatcherGroup>>();
        if (root?["hooks"] is not JsonObject hooksObject)
        {
            return new HookConfiguration(groupsByEvent, warnings);
        }

        foreach (var (eventName, eventNode) in hooksObject)
        {
            if (!HookEvents.IsSupported(eventName))
            {
                warnings.Add($"{sourceName}: hook event '{eventName}' is not supported here (only "
                             + $"{HookEvents.PreToolUse} and {HookEvents.PostToolUse}) and was ignored.");
                continue;
            }

            if (eventNode is not JsonArray groupNodes)
            {
                warnings.Add($"{sourceName}: '{eventName}' must be an array and was ignored.");
                continue;
            }

            var groups = new List<HookMatcherGroup>();
            foreach (var groupNode in groupNodes)
            {
                var hooks = new List<HookCommand>();
                foreach (var hookNode in groupNode?["hooks"] as JsonArray ?? [])
                {
                    if (TryParseCommand(hookNode, out var command, out var problem))
                    {
                        hooks.Add(command);
                    }
                    else
                    {
                        warnings.Add($"{sourceName}: a {eventName} hook was ignored - {problem}");
                    }
                }

                if (hooks.Count > 0)
                {
                    groups.Add(new HookMatcherGroup(GetString(groupNode?["matcher"]), hooks));
                }
            }

            if (groups.Count > 0)
            {
                groupsByEvent[eventName] = groups;
            }
        }

        return new HookConfiguration(groupsByEvent, warnings);
    }

    private static bool TryParseCommand(JsonNode? node, out HookCommand command, out string problem)
    {
        command = null!;
        problem = "";

        var type = GetString(node?["type"]);
        if (type is not null && type != "command")
        {
            problem = $"type '{type}' is not supported (only 'command').";
            return false;
        }

        var text = GetString(node?["command"]);
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "it has no 'command'.";
            return false;
        }

        IReadOnlyList<string>? args = null;
        if (node?["args"] is JsonArray argNodes)
        {
            args = argNodes.Select(a => GetString(a) ?? a?.ToJsonString() ?? "").ToList();
        }

        var timeout = DefaultTimeoutSeconds;
        if (node?["timeout"] is JsonValue timeoutValue && timeoutValue.TryGetValue<double>(out var seconds) && seconds > 0)
        {
            timeout = (int)Math.Ceiling(seconds);
        }

        command = new HookCommand(text, args, timeout);
        return true;
    }

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
