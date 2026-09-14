using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.PluginContracts;

namespace AccessVbaPlugin;

/// <summary>
/// What the Access VBA tool window is showing right now: the module whose code is in its editor, that
/// code as currently edited, and whether it differs from what was last pulled from (or pushed to)
/// Access. A null <see cref="ModuleName"/> means nothing has been pulled yet.
/// </summary>
public sealed record CurrentModuleSnapshot(
    string? ModuleName,
    string? ModuleKind,
    string Code,
    bool HasUnpushedChanges,
    string? DatabasePath);

/// <summary>
/// The chat tools the Access VBA plugin contributes through <see cref="IPluginHost.RegisterChatTool"/>,
/// so the Copilot can read VBA that lives inside Access rather than in the open solution. The
/// Copilot's own context never reaches this plugin: "Include current file" covers the main editor
/// only, and its read_file/search_codebase tools refuse anything outside the solution directory.
///
/// Kept free of WPF and of AccessMCP.Core types on purpose. The three data sources arrive as
/// delegates (the tool window's current editor state, and the same two AccessTools calls the module
/// list and the Pull button already use), which is what makes the argument handling, result shaping,
/// truncation and error paths testable without Access installed.
///
/// Every Access-backed call starts a hidden Access instance, as all AccessTools calls do, so it takes
/// a few seconds - and if Access puts up a modal dialog (a startup message box from the database,
/// say) the call would otherwise never return. The timeout turns that into an error result the model
/// can act on instead of a chat turn that hangs forever.
/// </summary>
public sealed class VbaChatTools
{
    /// <summary>Same cap as the IDE's own read_file tool, so one huge module can't blow the context window.</summary>
    public const int MaxModuleChars = 60_000;

    /// <summary>Same cap as the IDE's own list_files tool.</summary>
    public const int MaxListedModules = 400;

    public static readonly TimeSpan DefaultAccessTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<CancellationToken, Task<CurrentModuleSnapshot?>> _getCurrentModule;
    private readonly Func<CancellationToken, Task<string>> _listModulesJson;
    private readonly Func<string, CancellationToken, Task<string>> _getModuleJson;
    private readonly TimeSpan _accessTimeout;

    /// <param name="getCurrentModule">The tool window's current state, or null while no window exists.</param>
    /// <param name="listModulesJson">AccessTools.ListVbeModulesAsync, or anything returning its JSON shape.</param>
    /// <param name="getModuleJson">AccessTools.GetVbeModuleCodeAsync for the given module name, or anything returning its JSON shape.</param>
    /// <param name="accessTimeout">How long an Access-backed call may take before it is reported as an error. Defaults to <see cref="DefaultAccessTimeout"/>.</param>
    public VbaChatTools(
        Func<CancellationToken, Task<CurrentModuleSnapshot?>> getCurrentModule,
        Func<CancellationToken, Task<string>> listModulesJson,
        Func<string, CancellationToken, Task<string>> getModuleJson,
        TimeSpan? accessTimeout = null)
    {
        _getCurrentModule = getCurrentModule;
        _listModulesJson = listModulesJson;
        _getModuleJson = getModuleJson;
        _accessTimeout = accessTimeout ?? DefaultAccessTimeout;
    }

    public static readonly PluginToolDefinition GetCurrentModuleDefinition = new(
        Name: "vba_get_current_module",
        Description:
            "Returns the VBA module currently open in the Access VBA tool window: its name, kind, and the " +
            "code exactly as it stands in that editor, including edits not yet pushed to Access. Use this " +
            "first whenever the user asks about 'this module', 'this code', or the VBA they are looking at. " +
            "The IDE's 'Include current file' context never covers that window, so this is the only way to see it.",
        ParametersSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });

    public static readonly PluginToolDefinition ListModulesDefinition = new(
        Name: "vba_list_modules",
        Description:
            "Lists the VBA modules in the Access database the Access VBA tool window is connected to, read " +
            "live from Access: name, kind (Standard, Class, Form or Document) and line count for each. On a " +
            "large project pass nameContains to narrow the list rather than paging through everything. " +
            "Takes a few seconds, because it starts a hidden Access instance.",
        ParametersSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["nameContains"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional case-insensitive substring; only modules whose name contains it are listed.",
                },
            },
        });

    public static readonly PluginToolDefinition GetModuleDefinition = new(
        Name: "vba_get_module",
        Description:
            "Returns the full code of one VBA module by name, read live from Access (not from any export " +
            "folder). Use vba_list_modules to find names. If that module is also open in the Access VBA tool " +
            "window with unpushed edits, the result says so; call vba_get_current_module for the edited " +
            "version. Takes a few seconds, because it starts a hidden Access instance.",
        ParametersSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The module name as listed by vba_list_modules (case-insensitive).",
                },
            },
            ["required"] = new JsonArray("name"),
        });

    /// <summary>Every tool this class offers, paired with its execute delegate, in registration order.</summary>
    public IReadOnlyList<(PluginToolDefinition Definition, Func<JsonNode?, CancellationToken, Task<PluginToolResult>> Execute)> Tools =>
    [
        (GetCurrentModuleDefinition, GetCurrentModuleAsync),
        (ListModulesDefinition, ListModulesAsync),
        (GetModuleDefinition, GetModuleAsync),
    ];

    public void RegisterAll(IPluginHost host)
    {
        foreach (var (definition, execute) in Tools)
        {
            host.RegisterChatTool(definition, execute);
        }
    }

    public async Task<PluginToolResult> GetCurrentModuleAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var snapshot = await _getCurrentModule(cancellationToken);
        if (snapshot?.ModuleName is null)
        {
            return new PluginToolResult(
                IsError: false,
                Text: "No module is open in the Access VBA tool window - nothing has been pulled yet. " +
                      "Use vba_list_modules to find a module and vba_get_module to read it.");
        }

        var text = new StringBuilder();
        text.Append("Module: ").Append(snapshot.ModuleName);
        if (snapshot.ModuleKind is not null)
        {
            text.Append(" (").Append(snapshot.ModuleKind).Append(')');
        }
        text.AppendLine();
        if (!string.IsNullOrEmpty(snapshot.DatabasePath))
        {
            text.Append("Database: ").AppendLine(snapshot.DatabasePath);
        }
        text.AppendLine(snapshot.HasUnpushedChanges
            ? "State: edited in the tool window and NOT yet pushed to Access - the code below is the edited text."
            : "State: matches what was last pulled from or pushed to Access.");
        AppendCode(text, snapshot.Code);
        return new PluginToolResult(IsError: false, text.ToString());
    }

    public async Task<PluginToolResult> ListModulesAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var filter = TryGetString(arguments, "nameContains")?.Trim();

        string json;
        try
        {
            json = await WithAccessTimeout(_listModulesJson, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PluginToolResult(IsError: true, $"Could not list modules from Access: {ex.Message}");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var projectName = root.TryGetProperty("projectName", out var projectProperty) ? projectProperty.GetString() : null;

        var modules = new List<(string Name, string Kind, int Lines)>();
        foreach (var module in root.GetProperty("modules").EnumerateArray())
        {
            var name = module.GetProperty("name").GetString();
            if (name is null)
            {
                continue;
            }

            var kind = module.TryGetProperty("type", out var typeProperty) ? KindName(typeProperty.GetInt32()) : "Unknown";
            var lines = module.TryGetProperty("lineCount", out var linesProperty) ? linesProperty.GetInt32() : 0;
            modules.Add((name, kind, lines));
        }

        var total = modules.Count;
        if (!string.IsNullOrEmpty(filter))
        {
            modules.RemoveAll(m => !m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        modules.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var text = new StringBuilder();
        text.Append("VBA project");
        if (!string.IsNullOrEmpty(projectName))
        {
            text.Append(" '").Append(projectName).Append('\'');
        }
        text.Append(": ").Append(total).Append(" module(s)");
        if (!string.IsNullOrEmpty(filter))
        {
            text.Append(", ").Append(modules.Count).Append(" with '").Append(filter).Append("' in the name");
        }
        text.AppendLine(".");

        foreach (var (name, kind, lines) in modules.Take(MaxListedModules))
        {
            text.Append(name).Append("  [").Append(kind).Append(", ").Append(lines).AppendLine(" lines]");
        }

        if (modules.Count > MaxListedModules)
        {
            text.Append("[showing the first ").Append(MaxListedModules).Append(" of ").Append(modules.Count)
                .AppendLine(" - pass nameContains to narrow the list]");
        }

        return new PluginToolResult(IsError: false, text.ToString());
    }

    public async Task<PluginToolResult> GetModuleAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var name = TryGetString(arguments, "name")?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return new PluginToolResult(IsError: true, "'name' is required: the module name as listed by vba_list_modules.");
        }

        string json;
        try
        {
            json = await WithAccessTimeout(token => _getModuleJson(name, token), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PluginToolResult(IsError: true, $"Could not read module '{name}' from Access: {ex.Message}");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var resolvedName = root.TryGetProperty("moduleName", out var nameProperty) ? nameProperty.GetString() ?? name : name;
        var code = root.GetProperty("code").GetString() ?? string.Empty;

        var text = new StringBuilder();
        text.Append("Module: ").AppendLine(resolvedName);
        text.AppendLine("Source: live from Access.");

        // Best effort only: the note is a courtesy, and a snapshot failure must not turn a successful
        // read into an error.
        CurrentModuleSnapshot? snapshot = null;
        try
        {
            snapshot = await _getCurrentModule(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }

        if (snapshot?.ModuleName is not null
            && string.Equals(snapshot.ModuleName, resolvedName, StringComparison.OrdinalIgnoreCase)
            && snapshot.HasUnpushedChanges)
        {
            text.AppendLine("Note: this module is open in the Access VBA tool window with unpushed edits. The code below " +
                            "is what Access holds; call vba_get_current_module for the edited version.");
        }

        AppendCode(text, code);
        return new PluginToolResult(IsError: false, text.ToString());
    }

    internal static string KindName(int vbeComponentType) => vbeComponentType switch
    {
        1 => "Standard",
        2 => "Class",
        3 => "Form",
        100 => "Document",
        _ => $"Type {vbeComponentType}",
    };

    private static void AppendCode(StringBuilder text, string code)
    {
        var lineCount = code.Length == 0 ? 0 : code.Replace("\r\n", "\n").Split('\n').Length;
        text.Append("Code (").Append(lineCount).AppendLine(" lines):");
        if (code.Length <= MaxModuleChars)
        {
            text.Append(code);
            return;
        }

        text.Append(code, 0, MaxModuleChars);
        text.AppendLine();
        text.Append("[truncated - showing the first ").Append(MaxModuleChars).Append(" of ").Append(code.Length).Append(" chars]");
    }

    private static string? TryGetString(JsonNode? arguments, string propertyName)
    {
        if (arguments is not JsonObject obj || obj[propertyName] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text : value.ToJsonString().Trim('"');
    }

    /// <summary>
    /// Runs an Access-backed call with a deadline. Races the call against the deadline rather than
    /// relying on the callee to honour cancellation, because a COM call blocked behind a modal dialog
    /// never observes its token; the abandoned call is left to finish (or be killed by AccessTools'
    /// own process cleanup) in the background.
    /// </summary>
    private async Task<string> WithAccessTimeout(Func<CancellationToken, Task<string>> call, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_accessTimeout);

        var callTask = call(deadline.Token);
        var finished = await Task.WhenAny(callTask, Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token));
        if (finished == callTask)
        {
            return await callTask;
        }

        // Observe the abandoned task's eventual failure so it can't surface as an unobserved exception.
        _ = callTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"Access did not answer within {_accessTimeout.TotalSeconds:0} seconds. It may be showing a dialog " +
            "(for example a startup message from the database) that has to be dismissed before automation can continue.");
    }
}
