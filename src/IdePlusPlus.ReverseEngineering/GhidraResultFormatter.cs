using System.Text;
using System.Text.Json;

namespace IdePlusPlus.ReverseEngineering;

/// <summary>
/// Turns the JSON the Ghidra post-script writes into the readable text a tool returns to the model.
/// Separated from the backend so it can be tested against captured fixtures without running Ghidra.
/// </summary>
internal static class GhidraResultFormatter
{
    public static (bool IsError, string Text) Format(string op, string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (true, $"Ghidra returned output that could not be parsed:\n{Truncate(json)}");
        }

        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            return (true, Str(root, "error") is { Length: > 0 } message ? message : "Ghidra reported an error.");
        }

        return op switch
        {
            "overview" => (false, FormatOverview(root)),
            "functions" => (false, FormatFunctions(root)),
            "decompile" => (false, FormatDecompile(root)),
            "xrefs" => (false, FormatXrefs(root)),
            "callgraph" => (false, FormatCallgraph(root)),
            "rename" => (false, FormatRename(root)),
            "comment" => (false, FormatComment(root)),
            _ => (false, json),
        };
    }

    private static string FormatOverview(JsonElement r) =>
        $"{Str(r, "name")}\n"
        + $"Language: {Str(r, "languageId")}   Compiler: {Str(r, "compiler")}\n"
        + $"Image base: {Str(r, "imageBase")}\n"
        + $"Functions: {Int(r, "functions")}   Symbols: {Int(r, "symbols")}";

    private static string FormatFunctions(JsonElement r)
    {
        var builder = new StringBuilder();
        builder.Append($"{Int(r, "shown")} of {Int(r, "total")} function(s):\n");
        foreach (var function in Array(r, "functions"))
        {
            builder.Append($"  {Str(function, "address")}  {Str(function, "name")}\n");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDecompile(JsonElement r) =>
        $"// {Str(r, "name")} @ {Str(r, "address")}\n{Str(r, "code")}";

    private static string FormatXrefs(JsonElement r)
    {
        var builder = new StringBuilder($"References to {Str(r, "name")}:\n");
        var any = false;
        foreach (var reference in Array(r, "to"))
        {
            any = true;
            var fromFunction = Str(reference, "fromFunction");
            builder.Append($"  {Str(reference, "from")}{(fromFunction.Length == 0 ? "" : $"  in {fromFunction}")}\n");
        }

        if (!any)
        {
            builder.Append("  (none)\n");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatCallgraph(JsonElement r)
    {
        string Names(string property)
        {
            var names = Array(r, property)
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            return names.Count == 0 ? "(none)" : string.Join(", ", names);
        }

        return $"{Str(r, "name")}\nCallers: {Names("callers")}\nCallees: {Names("callees")}";
    }

    private static string FormatRename(JsonElement r) =>
        $"Renamed {Str(r, "oldName")} to {Str(r, "newName")} at {Str(r, "address")} (persisted to the Ghidra project).";

    private static string FormatComment(JsonElement r) =>
        $"Comment set at {Str(r, "address")} (persisted to the Ghidra project).";

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
