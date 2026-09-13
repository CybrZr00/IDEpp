using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessVbaPlugin.Symbols;

public enum VbaModuleKind
{
    Standard,
    Class,
    Document,
    Form,
}

public enum VbaVisibility
{
    Public,
    Private,
    Friend,
}

public enum VbaProcedureKind
{
    Sub,
    Function,
    PropertyGet,
    PropertyLet,
    PropertySet,
    Event,
    DeclareSub,
    DeclareFunction,
}

public sealed record VbaParameter(string Name, string? Type, bool IsOptional, bool ByVal, bool IsParamArray, bool IsArray, string? DefaultValue)
{
    public string Display
    {
        get
        {
            var sb = new StringBuilder();
            if (IsOptional) sb.Append("Optional ");
            if (ByVal) sb.Append("ByVal ");
            if (IsParamArray) sb.Append("ParamArray ");
            sb.Append(Name);
            if (IsArray) sb.Append("()");
            sb.Append(" As ").Append(Type ?? "Variant");
            if (DefaultValue is not null) sb.Append(" = ").Append(DefaultValue);
            return sb.ToString();
        }
    }
}

/// <param name="Procedure">Null for a module-level field or constant; otherwise the procedure this local belongs to.</param>
public sealed record VbaVariable(string Name, string? Type, VbaVisibility Visibility, bool IsConst, bool IsArray, int Line, string? Procedure)
{
    public string Display => $"{(Procedure is null ? Visibility.ToString() : "Dim")}{(IsConst ? " Const" : "")} {Name}{(IsArray ? "()" : "")} As {Type ?? "Variant"}";
}

public sealed record VbaProcedure(
    string Name,
    VbaProcedureKind Kind,
    VbaVisibility Visibility,
    IReadOnlyList<VbaParameter> Parameters,
    string? ReturnType,
    int StartLine,
    int EndLine,
    IReadOnlyList<VbaVariable> Locals)
{
    public bool IsProperty => Kind is VbaProcedureKind.PropertyGet or VbaProcedureKind.PropertyLet or VbaProcedureKind.PropertySet;

    public string KindDisplay => Kind switch
    {
        VbaProcedureKind.Sub => "Sub",
        VbaProcedureKind.Function => "Function",
        VbaProcedureKind.PropertyGet => "Property Get",
        VbaProcedureKind.PropertyLet => "Property Let",
        VbaProcedureKind.PropertySet => "Property Set",
        VbaProcedureKind.Event => "Event",
        VbaProcedureKind.DeclareSub => "Declare Sub",
        VbaProcedureKind.DeclareFunction => "Declare Function",
        _ => Kind.ToString(),
    };

    public string Signature
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Visibility).Append(' ').Append(KindDisplay).Append(' ').Append(Name);
            sb.Append('(').Append(string.Join(", ", Parameters.Select(p => p.Display))).Append(')');
            if (ReturnType is not null)
            {
                sb.Append(" As ").Append(ReturnType);
            }

            return sb.ToString();
        }
    }
}

public sealed record VbaEnumMember(string Name, string? Value);

public sealed record VbaEnum(string Name, VbaVisibility Visibility, IReadOnlyList<VbaEnumMember> Members, int Line);

public sealed record VbaUserType(string Name, VbaVisibility Visibility, IReadOnlyList<VbaVariable> Fields, int Line);

public sealed record VbaModule(
    string Name,
    VbaModuleKind Kind,
    IReadOnlyList<VbaProcedure> Procedures,
    IReadOnlyList<VbaVariable> Fields,
    IReadOnlyList<VbaEnum> Enums,
    IReadOnlyList<VbaUserType> Types)
{
    public bool IsClassLike => Kind is VbaModuleKind.Class or VbaModuleKind.Document or VbaModuleKind.Form;

    public VbaProcedure? ProcedureAtLine(int line)
        => Procedures.FirstOrDefault(p => line >= p.StartLine && line <= p.EndLine);
}

/// <summary>
/// A declaration-level parser for VBA source: procedures (with parameters and locals), module-level
/// fields and constants, Declare statements, Events, Enums and user-defined Types. Deliberately not a
/// full grammar - it reads what completion and hover need, tolerates anything it doesn't understand,
/// and never throws on real-world code. Accepts either a bare code body or a raw VBComponent.Export
/// file (header block and Attribute lines are skipped).
/// </summary>
public static class VbaModuleParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex ProcStart = new(@"^(?:(?<vis>Public|Private|Friend)\s+)?(?:Static\s+)?(?<kind>Sub|Function|Property\s+Get|Property\s+Let|Property\s+Set)\s+(?<name>[A-Za-z_]\w*)(?<suffix>[$%&!#@])?\s*(?<rest>.*)$", Opts);
    private static readonly Regex ProcEnd = new(@"^End\s+(?:Sub|Function|Property)\b", Opts);
    private static readonly Regex DeclareStart = new(@"^(?:(?<vis>Public|Private)\s+)?Declare\s+(?:PtrSafe\s+)?(?<kind>Sub|Function)\s+(?<name>[A-Za-z_]\w*)\s+Lib\s+""[^""]*""\s*(?:Alias\s+""[^""]*""\s*)?(?<rest>.*)$", Opts);
    private static readonly Regex EventDecl = new(@"^(?:(?<vis>Public|Private)\s+)?Event\s+(?<name>[A-Za-z_]\w*)\s*(?<rest>.*)$", Opts);
    private static readonly Regex ConstDecl = new(@"^(?:(?<vis>Public|Private|Global)\s+)?Const\s+(?<rest>.+)$", Opts);
    private static readonly Regex TypeStart = new(@"^(?:(?<vis>Public|Private)\s+)?Type\s+(?<name>[A-Za-z_]\w*)\s*$", Opts);
    private static readonly Regex TypeEnd = new(@"^End\s+Type\b", Opts);
    private static readonly Regex EnumStart = new(@"^(?:(?<vis>Public|Private)\s+)?Enum\s+(?<name>[A-Za-z_]\w*)\s*$", Opts);
    private static readonly Regex EnumEnd = new(@"^End\s+Enum\b", Opts);
    private static readonly Regex VarDecl = new(@"^(?<scope>Dim|Private|Public|Global|Static)\s+(?:WithEvents\s+)?(?<rest>.+)$", Opts);
    private static readonly Regex VarDeclGuard = new(@"^(?:Sub|Function|Property|Type|Enum|Const|Declare|Event)\b", Opts);
    private static readonly Regex VarItem = new(@"^(?<name>[A-Za-z_]\w*)(?<suffix>[$%&!#@])?\s*(?<arr>\((?:[^()]|\([^()]*\))*\))?\s*(?:As\s+(?:New\s+)?(?<type>[A-Za-z_][\w.]*))?\s*$", Opts);
    private static readonly Regex ConstItem = new(@"^(?<name>[A-Za-z_]\w*)(?<suffix>[$%&!#@])?\s*(?:As\s+(?<type>[A-Za-z_][\w.]*))?\s*=\s*(?<value>.+)$", Opts);
    private static readonly Regex ParamItem = new(@"^(?:(?<opt>Optional)\s+)?(?:(?<mode>ByVal|ByRef)\s+)?(?:(?<pa>ParamArray)\s+)?(?<name>[A-Za-z_]\w*)(?<suffix>[$%&!#@])?\s*(?<arr>\(\s*\))?\s*(?:As\s+(?<type>[A-Za-z_][\w.]*))?\s*(?:=\s*(?<def>.+))?$", Opts);
    private static readonly Regex EnumMemberDecl = new(@"^(?<name>[A-Za-z_]\w*)\s*(?:=\s*(?<value>.+))?$", Opts);
    private static readonly Regex TypeField = new(@"^(?<name>[A-Za-z_]\w*)\s*(?<arr>\([^)]*\))?\s+As\s+(?<type>[A-Za-z_][\w.]*)", Opts);
    private static readonly Regex AsReturn = new(@"^\s*As\s+(?<type>[A-Za-z_][\w.]*)\s*(?<arr>\(\s*\))?", Opts);

    public static VbaModule Parse(string name, VbaModuleKind kind, string code)
    {
        var procedures = new List<VbaProcedure>();
        var fields = new List<VbaVariable>();
        var enums = new List<VbaEnum>();
        var types = new List<VbaUserType>();

        ProcBuilder? proc = null;
        (string Name, VbaVisibility Vis, List<VbaEnumMember> Members, int Line)? currentEnum = null;
        (string Name, VbaVisibility Vis, List<VbaVariable> Fields, int Line)? currentType = null;
        var lastLine = 1;

        foreach (var (logicalText, line) in LogicalLines(StripExportHeaders(code)))
        {
            lastLine = line;
            foreach (var rawStatement in SplitTopLevel(logicalText, ':'))
            {
                var statement = rawStatement.Trim();
                if (statement.Length == 0 || statement.StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (currentEnum is { } e)
                {
                    if (EnumEnd.IsMatch(statement))
                    {
                        enums.Add(new VbaEnum(e.Name, e.Vis, e.Members, e.Line));
                        currentEnum = null;
                    }
                    else if (EnumMemberDecl.Match(statement) is { Success: true } m)
                    {
                        e.Members.Add(new VbaEnumMember(m.Groups["name"].Value, m.Groups["value"].Success ? m.Groups["value"].Value.Trim() : null));
                    }

                    continue;
                }

                if (currentType is { } t)
                {
                    if (TypeEnd.IsMatch(statement))
                    {
                        types.Add(new VbaUserType(t.Name, t.Vis, t.Fields, t.Line));
                        currentType = null;
                    }
                    else if (TypeField.Match(statement) is { Success: true } m)
                    {
                        t.Fields.Add(new VbaVariable(m.Groups["name"].Value, m.Groups["type"].Value, VbaVisibility.Public, false, m.Groups["arr"].Success, line, null));
                    }

                    continue;
                }

                if (proc is not null && ProcEnd.IsMatch(statement))
                {
                    procedures.Add(proc.Build(line));
                    proc = null;
                    continue;
                }

                if (ProcStart.Match(statement) is { Success: true } ps)
                {
                    if (proc is not null)
                    {
                        // Unterminated procedure - close it where the next one starts.
                        procedures.Add(proc.Build(line - 1));
                    }

                    var kindText = Regex.Replace(ps.Groups["kind"].Value, @"\s+", " ");
                    var procKind = kindText.ToUpperInvariant() switch
                    {
                        "SUB" => VbaProcedureKind.Sub,
                        "FUNCTION" => VbaProcedureKind.Function,
                        "PROPERTY GET" => VbaProcedureKind.PropertyGet,
                        "PROPERTY LET" => VbaProcedureKind.PropertyLet,
                        _ => VbaProcedureKind.PropertySet,
                    };
                    var (parameters, returnType) = ParseParametersAndReturn(ps.Groups["rest"].Value, ps.Groups["suffix"].Value);
                    proc = new ProcBuilder(ps.Groups["name"].Value, procKind, ParseVisibility(ps.Groups["vis"].Value, VbaVisibility.Public), parameters, returnType, line);
                    continue;
                }

                if (DeclareStart.Match(statement) is { Success: true } ds)
                {
                    var (parameters, returnType) = ParseParametersAndReturn(ds.Groups["rest"].Value, "");
                    var declKind = ds.Groups["kind"].Value.Equals("Sub", StringComparison.OrdinalIgnoreCase) ? VbaProcedureKind.DeclareSub : VbaProcedureKind.DeclareFunction;
                    procedures.Add(new VbaProcedure(ds.Groups["name"].Value, declKind, ParseVisibility(ds.Groups["vis"].Value, VbaVisibility.Public), parameters, returnType, line, line, []));
                    continue;
                }

                if (proc is null && EventDecl.Match(statement) is { Success: true } ev)
                {
                    var (parameters, _) = ParseParametersAndReturn(ev.Groups["rest"].Value, "");
                    procedures.Add(new VbaProcedure(ev.Groups["name"].Value, VbaProcedureKind.Event, ParseVisibility(ev.Groups["vis"].Value, VbaVisibility.Public), parameters, null, line, line, []));
                    continue;
                }

                if (ConstDecl.Match(statement) is { Success: true } cd)
                {
                    var visibility = ParseVisibility(cd.Groups["vis"].Value, VbaVisibility.Private);
                    foreach (var item in SplitTopLevel(cd.Groups["rest"].Value, ','))
                    {
                        if (ConstItem.Match(item.Trim()) is { Success: true } ci)
                        {
                            var variable = new VbaVariable(ci.Groups["name"].Value, TypeFromSuffix(ci.Groups["type"].Value, ci.Groups["suffix"].Value), visibility, true, false, line, proc?.Name);
                            (proc is null ? fields : proc.Locals).Add(variable);
                        }
                    }

                    continue;
                }

                if (proc is null && TypeStart.Match(statement) is { Success: true } ts)
                {
                    currentType = (ts.Groups["name"].Value, ParseVisibility(ts.Groups["vis"].Value, VbaVisibility.Public), new List<VbaVariable>(), line);
                    continue;
                }

                if (proc is null && EnumStart.Match(statement) is { Success: true } es)
                {
                    currentEnum = (es.Groups["name"].Value, ParseVisibility(es.Groups["vis"].Value, VbaVisibility.Public), new List<VbaEnumMember>(), line);
                    continue;
                }

                if (VarDecl.Match(statement) is { Success: true } vd && !VarDeclGuard.IsMatch(vd.Groups["rest"].Value))
                {
                    var scope = vd.Groups["scope"].Value;
                    var visibility = scope.Equals("Public", StringComparison.OrdinalIgnoreCase) || scope.Equals("Global", StringComparison.OrdinalIgnoreCase)
                        ? VbaVisibility.Public
                        : VbaVisibility.Private;
                    foreach (var item in SplitTopLevel(vd.Groups["rest"].Value, ','))
                    {
                        if (VarItem.Match(item.Trim()) is { Success: true } vi)
                        {
                            var variable = new VbaVariable(vi.Groups["name"].Value, TypeFromSuffix(vi.Groups["type"].Value, vi.Groups["suffix"].Value), visibility, false, vi.Groups["arr"].Success, line, proc?.Name);
                            (proc is null ? fields : proc.Locals).Add(variable);
                        }
                    }
                }
            }
        }

        if (proc is not null)
        {
            procedures.Add(proc.Build(lastLine));
        }

        return new VbaModule(name, kind, procedures, fields, enums, types);
    }

    private sealed class ProcBuilder(string name, VbaProcedureKind kind, VbaVisibility visibility, IReadOnlyList<VbaParameter> parameters, string? returnType, int startLine)
    {
        public string Name { get; } = name;
        public List<VbaVariable> Locals { get; } = [];

        public VbaProcedure Build(int endLine)
            => new(Name, kind, visibility, parameters, returnType, startLine, Math.Max(endLine, startLine), Locals);
    }

    private static (IReadOnlyList<VbaParameter> Parameters, string? ReturnType) ParseParametersAndReturn(string rest, string nameSuffix)
    {
        var parameters = new List<VbaParameter>();
        var after = rest.TrimStart();
        if (after.StartsWith('('))
        {
            var close = FindMatchingParen(after, 0);
            var inner = close > 0 ? after[1..close] : after[1..];
            after = close > 0 ? after[(close + 1)..] : string.Empty;
            foreach (var item in SplitTopLevel(inner, ','))
            {
                if (ParamItem.Match(item.Trim()) is { Success: true } pm)
                {
                    parameters.Add(new VbaParameter(
                        pm.Groups["name"].Value,
                        TypeFromSuffix(pm.Groups["type"].Value, pm.Groups["suffix"].Value),
                        pm.Groups["opt"].Success,
                        pm.Groups["mode"].Value.Equals("ByVal", StringComparison.OrdinalIgnoreCase),
                        pm.Groups["pa"].Success,
                        pm.Groups["arr"].Success,
                        pm.Groups["def"].Success ? pm.Groups["def"].Value.Trim() : null));
                }
            }
        }

        string? returnType = null;
        if (AsReturn.Match(after) is { Success: true } ar)
        {
            returnType = ar.Groups["type"].Value + (ar.Groups["arr"].Success ? "()" : "");
        }
        else if (nameSuffix.Length > 0)
        {
            returnType = TypeFromSuffix(null, nameSuffix);
        }

        return (parameters, returnType);
    }

    private static string? TypeFromSuffix(string? declaredType, string suffix)
    {
        if (!string.IsNullOrEmpty(declaredType))
        {
            return declaredType;
        }

        return suffix switch
        {
            "$" => "String",
            "%" => "Integer",
            "&" => "Long",
            "!" => "Single",
            "#" => "Double",
            "@" => "Currency",
            _ => null,
        };
    }

    private static VbaVisibility ParseVisibility(string text, VbaVisibility defaultVisibility) => text.ToUpperInvariant() switch
    {
        "PUBLIC" or "GLOBAL" => VbaVisibility.Public,
        "PRIVATE" => VbaVisibility.Private,
        "FRIEND" => VbaVisibility.Friend,
        _ => defaultVisibility,
    };

    /// <summary>
    /// Physical lines joined on trailing " _" continuations, with comments removed, each tagged with
    /// the 1-based physical line it started on.
    /// </summary>
    internal static List<(string Text, int Line)> LogicalLines(string code)
    {
        var physical = code.Replace("\r\n", "\n").Split('\n');
        var result = new List<(string, int)>();
        var sb = new StringBuilder();
        var start = 1;
        var continuing = false;

        for (var i = 0; i < physical.Length; i++)
        {
            if (!continuing)
            {
                sb.Clear();
                start = i + 1;
            }

            var stripped = StripComment(physical[i]).TrimEnd();
            if (stripped.EndsWith(" _", StringComparison.Ordinal) || stripped == "_")
            {
                sb.Append(stripped[..^1]);
                continuing = true;
                continue;
            }

            sb.Append(stripped);
            result.Add((sb.ToString(), start));
            continuing = false;
        }

        if (continuing)
        {
            result.Add((sb.ToString(), start));
        }

        return result;
    }

    internal static string StripComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("Rem", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3])))
        {
            return string.Empty;
        }

        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inString = !inString;
            }
            else if (c == '\'' && !inString)
            {
                return line[..i];
            }
        }

        return line;
    }

    /// <summary>Splits on <paramref name="separator"/> outside string literals and parentheses.</summary>
    internal static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var last = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString)
            {
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == separator && depth == 0)
                {
                    parts.Add(text[last..i]);
                    last = i + 1;
                }
            }
        }

        parts.Add(text[last..]);
        return parts;
    }

    internal static int FindMatchingParen(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString)
            {
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Same reduction AccessMCP applies to a VBComponent.Export file: drops the class VERSION/BEGIN..END
    /// header block and Attribute lines, leaving what CodeModule.Lines would return. A no-op for a
    /// bare code body.
    /// </summary>
    internal static string StripExportHeaders(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var start = 0;
        if (lines.Count > 0 && lines[0].StartsWith("VERSION ", StringComparison.OrdinalIgnoreCase))
        {
            var depth = 0;
            var i = 1;
            for (; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                }
                else if (trimmed.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    depth--;
                    if (depth <= 0)
                    {
                        i++;
                        break;
                    }
                }
            }

            start = i;
        }

        var body = new List<string>();
        for (var i = start; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase))
            {
                body.Add(lines[i]);
            }
        }

        return string.Join("\r\n", body);
    }
}

/// <summary>
/// Every module's declarations, keyed by module name. Built from an export folder (using its manifest
/// for module kinds when present) and updated with the module currently open in the editor.
/// </summary>
public sealed class VbaSymbolIndex
{
    private readonly Dictionary<string, VbaModule> _modules = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<VbaModule> Modules => _modules.Values;

    public VbaModule? GetModule(string name) => _modules.GetValueOrDefault(name);

    public void Upsert(VbaModule module) => _modules[module.Name] = module;

    /// <summary>Kind of a module as the export manifest recorded it, or a guess from its name/file when there's no manifest.</summary>
    public VbaModuleKind KindOf(string moduleName) => GetModule(moduleName)?.Kind ?? GuessKind(moduleName, ".cls");

    public static VbaSymbolIndex FromExportFolder(string folder)
    {
        var index = new VbaSymbolIndex();
        if (!Directory.Exists(folder))
        {
            return index;
        }

        var manifestPath = Path.Combine(folder, "_accessmcp_manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                foreach (var entry in doc.RootElement.GetProperty("modules").EnumerateArray())
                {
                    var name = entry.GetProperty("name").GetString();
                    var fileName = entry.GetProperty("fileName").GetString();
                    if (name is null || fileName is null)
                    {
                        continue;
                    }

                    var kind = entry.GetProperty("type").GetInt32() switch
                    {
                        1 => VbaModuleKind.Standard,
                        2 => VbaModuleKind.Class,
                        3 => VbaModuleKind.Form,
                        100 => VbaModuleKind.Document,
                        _ => VbaModuleKind.Standard,
                    };
                    var path = Path.Combine(folder, fileName);
                    if (File.Exists(path))
                    {
                        index.Upsert(VbaModuleParser.Parse(name, kind, File.ReadAllText(path)));
                    }
                }

                return index;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
            {
                // Fall through to the extension-based scan.
            }
        }

        foreach (var path in Directory.EnumerateFiles(folder).Where(p => p.EndsWith(".bas", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cls", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            index.Upsert(VbaModuleParser.Parse(name, GuessKind(name, Path.GetExtension(path)), File.ReadAllText(path)));
        }

        return index;
    }

    private static VbaModuleKind GuessKind(string name, string extension)
    {
        if (extension.Equals(".bas", StringComparison.OrdinalIgnoreCase))
        {
            return VbaModuleKind.Standard;
        }

        return name.StartsWith("Form_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Report_", StringComparison.OrdinalIgnoreCase)
            ? VbaModuleKind.Document
            : VbaModuleKind.Class;
    }
}
