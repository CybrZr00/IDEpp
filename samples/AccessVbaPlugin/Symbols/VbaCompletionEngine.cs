using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace AccessVbaPlugin.Symbols;

public enum VbaCompletionKind
{
    Keyword,
    Builtin,
    DataType,
    Constant,
    Procedure,
    Property,
    Variable,
    Parameter,
    Module,
    Class,
    Enum,
    EnumMember,
    UserType,
    Member,
    Event,
}

public sealed record VbaCompletionItem(string Text, VbaCompletionKind Kind, string Description, int Priority);

/// <summary>The language's own vocabulary, read from the same Vba.xshd the editor highlights with so the two never drift apart.</summary>
public sealed record VbaLanguage(IReadOnlyList<string> Keywords, IReadOnlyList<string> Builtins, IReadOnlyList<string> Constants, IReadOnlyList<string> DataTypes)
{
    public static VbaLanguage Minimal { get; } = new(
        ["Sub", "Function", "End", "If", "Then", "Else", "ElseIf", "For", "Next", "Do", "Loop", "While", "Wend", "Dim", "Set", "Public", "Private", "Const", "With", "Select", "Case", "Exit", "On", "Error", "Resume", "GoTo", "Call", "New", "Nothing", "Is", "Not", "And", "Or", "To", "Step", "Each", "In", "ReDim", "Preserve", "Static", "Optional", "ByVal", "ByRef", "Property", "Get", "Let", "Type", "Enum", "Declare"],
        ["MsgBox", "InputBox", "IIf", "Nz", "DLookup", "DCount", "DSum", "CurrentDb", "DoCmd", "Len", "Left", "Right", "Mid", "InStr", "Trim", "UCase", "LCase", "Format", "CStr", "CInt", "CLng", "CDbl", "IsNull", "Now", "Date"],
        ["True", "False", "Null", "Empty", "vbCrLf", "vbTab", "vbNullString"],
        ["Boolean", "Byte", "Currency", "Date", "Double", "Integer", "Long", "Object", "Single", "String", "Variant"]);

    public static VbaLanguage FromXshd(string path)
    {
        try
        {
            var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using var reader = XmlReader.Create(path);
            string? currentColor = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name == "Keywords")
                {
                    currentColor = reader.GetAttribute("color");
                    lists.TryAdd(currentColor ?? "", []);
                }
                else if (reader.Name == "Word" && currentColor is not null)
                {
                    lists[currentColor].Add(reader.ReadElementContentAsString());
                }
            }

            return new VbaLanguage(
                lists.GetValueOrDefault("Keyword") ?? Minimal.Keywords.ToList(),
                lists.GetValueOrDefault("BuiltinFunction") ?? Minimal.Builtins.ToList(),
                lists.GetValueOrDefault("Constant") ?? Minimal.Constants.ToList(),
                lists.GetValueOrDefault("DataType") ?? Minimal.DataTypes.ToList());
        }
        catch (Exception ex) when (ex is IOException or XmlException)
        {
            return Minimal;
        }
    }
}

/// <summary>
/// Members of the Access/DAO objects real Access VBA leans on constantly. Not from any parser - a
/// curated list so "rs." and "DoCmd." offer something useful even though those types live in COM type
/// libraries this plugin never reads.
/// </summary>
public static class VbaWellKnown
{
    private static readonly Dictionary<string, string> GlobalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DoCmd"] = "DoCmd",
        ["Err"] = "Err",
        ["Debug"] = "Debug",
        ["Application"] = "Application",
        ["CurrentDb"] = "Database",
        ["CurrentProject"] = "CurrentProject",
        ["Forms"] = "Forms",
        ["Reports"] = "Reports",
        ["Screen"] = "Screen",
    };

    private static readonly Dictionary<string, string> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TextBox"] = "Control", ["ComboBox"] = "Control", ["ListBox"] = "Control", ["CommandButton"] = "Control",
        ["Label"] = "Control", ["CheckBox"] = "Control", ["OptionButton"] = "Control", ["OptionGroup"] = "Control",
        ["SubForm"] = "Control", ["TabControl"] = "Control", ["Image"] = "Control",
        ["Recordset2"] = "Recordset", ["Field2"] = "Field", ["Access.Form"] = "Form",
    };

    private static readonly Dictionary<string, string[]> Members = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DoCmd"] = ["OpenForm", "Close", "OpenReport", "OpenQuery", "OpenTable", "RunSQL", "RunCommand", "RunMacro", "SetWarnings", "GoToRecord", "GoToControl", "Hourglass", "Requery", "Echo", "TransferSpreadsheet", "TransferText", "TransferDatabase", "OutputTo", "SendObject", "ApplyFilter", "ShowAllRecords", "Maximize", "Minimize", "Restore", "Save", "Rename", "CopyObject", "DeleteObject", "FindRecord", "FindNext", "Beep", "CancelEvent", "SelectObject", "Quit", "SetParameter", "BrowseTo", "NavigateTo", "OpenModule", "PrintOut", "RepaintObject", "SetProperty"],
        ["Err"] = ["Number", "Description", "Source", "Clear", "Raise", "HelpFile", "HelpContext", "LastDllError"],
        ["Debug"] = ["Print", "Assert"],
        ["Application"] = ["CurrentDb", "CurrentProject", "CurrentData", "DoCmd", "Forms", "Reports", "Screen", "Run", "Quit", "Echo", "SetOption", "GetOption", "Version", "Visible", "UserControl", "Eval", "hWndAccessApp", "FollowHyperlink", "CurrentUser", "RefreshDatabaseWindow", "SysCmd", "CodeDb", "DBEngine", "References", "VBE", "TempVars", "Printer", "Printers", "BuildCriteria", "AccessError", "NewCurrentDatabase", "OpenCurrentDatabase", "CloseCurrentDatabase"],
        ["CurrentProject"] = ["AllForms", "AllReports", "AllModules", "AllMacros", "AllDataAccessPages", "Path", "FullName", "Name", "Connection", "Properties", "IsConnected", "Application", "Resources"],
        ["Database"] = ["OpenRecordset", "Execute", "QueryDefs", "TableDefs", "Relations", "Containers", "Close", "Name", "Properties", "CreateQueryDef", "CreateTableDef", "CreateRelation", "CreateProperty", "RecordsAffected", "Connect", "Recordsets", "Version", "Updatable", "Transactions"],
        ["Recordset"] = ["MoveFirst", "MoveLast", "MoveNext", "MovePrevious", "Move", "EOF", "BOF", "Fields", "Edit", "Update", "AddNew", "Delete", "Close", "RecordCount", "FindFirst", "FindNext", "FindLast", "FindPrevious", "NoMatch", "Bookmark", "Bookmarkable", "Requery", "Seek", "Index", "Filter", "Sort", "AbsolutePosition", "PercentPosition", "Clone", "CancelUpdate", "GetRows", "Name", "Restartable", "Updatable", "LockEdits", "EditMode", "Type", "DateCreated", "LastUpdated", "LastModified", "Transactions", "Connection"],
        ["Field"] = ["Name", "Value", "Type", "Size", "Required", "AllowZeroLength", "DefaultValue", "OrdinalPosition", "Properties", "SourceTable", "SourceField", "Attributes", "ValidationRule", "ValidationText", "OriginalValue", "VisibleValue"],
        ["Fields"] = ["Count", "Item", "Append", "Delete", "Refresh"],
        ["QueryDef"] = ["SQL", "Name", "Parameters", "OpenRecordset", "Execute", "Close", "Fields", "RecordsAffected", "Connect", "ReturnsRecords", "Type", "DateCreated", "LastUpdated"],
        ["Parameters"] = ["Count", "Item", "Refresh"],
        ["Parameter"] = ["Name", "Value", "Type"],
        ["TableDef"] = ["Name", "Fields", "Indexes", "OpenRecordset", "Connect", "SourceTableName", "RecordCount", "Attributes", "CreateField", "CreateIndex", "RefreshLink", "ValidationRule", "Properties"],
        ["TableDefs"] = ["Count", "Item", "Append", "Delete", "Refresh"],
        ["QueryDefs"] = ["Count", "Item", "Append", "Delete", "Refresh"],
        ["Form"] = ["Recordset", "RecordSource", "Requery", "Refresh", "Repaint", "Controls", "Dirty", "NewRecord", "CurrentRecord", "Visible", "Caption", "Filter", "FilterOn", "OrderBy", "OrderByOn", "Name", "Undo", "SetFocus", "Section", "Parent", "Modal", "PopUp", "AllowEdits", "AllowAdditions", "AllowDeletions", "DataEntry", "RecordsetClone", "Bookmark", "ActiveControl", "hWnd", "Painting", "TimerInterval", "Tag", "Detail", "FormHeader", "FormFooter", "Form", "Module", "OpenArgs", "Properties", "GoToPage", "Move", "Cycle", "Recalc", "CurrentView", "SubdatasheetExpanded", "ScrollBars", "NavigationButtons", "RecordSelectors", "DividingLines", "Width", "InsideWidth", "InsideHeight", "WindowWidth", "WindowHeight", "WindowTop", "WindowLeft"],
        ["Report"] = ["Recordset", "RecordSource", "Requery", "Controls", "Caption", "Filter", "FilterOn", "OrderBy", "OrderByOn", "Name", "Section", "Visible", "Printer", "Pages", "Page", "MoveLayout", "NextRecord", "PrintSection", "Detail", "ReportHeader", "ReportFooter", "PageHeader", "PageFooter", "GroupLevel", "OpenArgs", "Report", "Module", "Tag", "hWnd", "Width", "Circle", "Line", "Print", "Scale", "CurrentX", "CurrentY", "FontName", "FontSize", "ForeColor", "DrawWidth", "TextWidth", "TextHeight"],
        ["Control"] = ["Value", "Name", "Visible", "Enabled", "Locked", "SetFocus", "Caption", "ControlSource", "RowSource", "RowSourceType", "DefaultValue", "Tag", "BackColor", "ForeColor", "FontBold", "FontName", "FontSize", "Requery", "Text", "SelStart", "SelLength", "SelText", "ListIndex", "ListCount", "Column", "ItemData", "AddItem", "RemoveItem", "Selected", "ItemsSelected", "BoundColumn", "ColumnCount", "ColumnWidths", "Parent", "ControlType", "Left", "Top", "Width", "Height", "TabIndex", "TabStop", "Format", "InputMask", "ValidationRule", "ValidationText", "StatusBarText", "ControlTipText", "OldValue", "Properties", "Form", "Report", "SourceObject", "LinkChildFields", "LinkMasterFields", "Dropdown", "Undo", "Pages", "Picture", "HyperlinkAddress", "Hyperlink", "OnClick", "OnChange", "AfterUpdate", "BeforeUpdate", "Move", "SizeToFit", "Section"],
        ["Forms"] = ["Count", "Item", "Application", "Parent"],
        ["Reports"] = ["Count", "Item", "Application", "Parent"],
        ["Screen"] = ["ActiveForm", "ActiveReport", "ActiveControl", "ActiveDatasheet", "PreviousControl", "MousePointer", "Application", "Parent"],
        ["Collection"] = ["Add", "Count", "Item", "Remove"],
        ["Dictionary"] = ["Add", "Exists", "Item", "Items", "Keys", "Remove", "RemoveAll", "Count", "CompareMode", "Key"],
        ["FileSystemObject"] = ["FileExists", "FolderExists", "GetFile", "GetFolder", "CreateFolder", "DeleteFile", "DeleteFolder", "CopyFile", "CopyFolder", "MoveFile", "MoveFolder", "OpenTextFile", "CreateTextFile", "BuildPath", "GetBaseName", "GetExtensionName", "GetFileName", "GetParentFolderName", "GetTempName", "Drives", "GetDrive", "GetSpecialFolder"],
        ["TextStream"] = ["ReadLine", "ReadAll", "Read", "WriteLine", "Write", "WriteBlankLines", "AtEndOfStream", "AtEndOfLine", "Close", "Line", "Column", "Skip", "SkipLine"],
        ["Connection"] = ["Open", "Close", "Execute", "BeginTrans", "CommitTrans", "RollbackTrans", "ConnectionString", "State", "Errors", "Provider", "CommandTimeout", "ConnectionTimeout"],
        ["Command"] = ["ActiveConnection", "CommandText", "CommandType", "Execute", "Parameters", "CreateParameter", "CommandTimeout", "Prepared"],
    };

    public static string? GlobalType(string identifier) => GlobalTypes.GetValueOrDefault(identifier);

    public static IEnumerable<string> GlobalNames => GlobalTypes.Keys;

    public static IReadOnlyList<string>? MembersOf(string typeName)
    {
        var normalized = NormalizeType(typeName);
        return Members.TryGetValue(normalized, out var members) ? members : null;
    }

    /// <summary>Strips library qualifiers (DAO., ADODB., Access., VBA., Scripting.) and maps control types onto Control.</summary>
    public static string NormalizeType(string typeName)
    {
        var name = typeName;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        return TypeAliases.GetValueOrDefault(name) ?? name;
    }
}

public sealed record VbaHoverInfo(string Text);

/// <summary>
/// Turns the caret's context - the identifier being typed, what it's a member of, the enclosing
/// procedure, any With block - plus the symbol index into completion candidates or hover text.
/// </summary>
public sealed class VbaCompletionEngine(VbaSymbolIndex index, VbaLanguage language)
{
    private static readonly Regex MemberContext = new(@"(?<chain>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\.(?<prefix>[A-Za-z_]?\w*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WithContext = new(@"(?<![\w.)\]!])\.(?<prefix>[A-Za-z_]?\w*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GlobalContext = new(@"(?<prefix>[A-Za-z_]\w*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WithStart = new(@"^\s*With\s+(?<expr>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WithEnd = new(@"^\s*End\s+With\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public VbaSymbolIndex Index { get; } = index;

    public IReadOnlyList<VbaCompletionItem> GetCompletions(VbaModule current, string text, int caretOffset, out int replaceStart)
    {
        replaceStart = caretOffset;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, caretOffset - 1)) + 1;
        var lineText = text[lineStart..caretOffset];
        if (IsInStringOrComment(lineText))
        {
            return [];
        }

        var line = LineNumberAt(text, caretOffset);
        var procedure = current.ProcedureAtLine(line);

        if (MemberContext.Match(lineText) is { Success: true } member)
        {
            var prefix = member.Groups["prefix"].Value;
            replaceStart = caretOffset - prefix.Length;
            return Filter(MembersOfChain(member.Groups["chain"].Value.Split('.'), current, procedure), prefix);
        }

        if (WithContext.Match(lineText) is { Success: true } withMatch)
        {
            var prefix = withMatch.Groups["prefix"].Value;
            replaceStart = caretOffset - prefix.Length;
            var receiver = FindWithExpression(text, lineStart);
            return receiver is null ? [] : Filter(MembersOfChain(receiver.Split('.'), current, procedure), prefix);
        }

        var globalPrefix = GlobalContext.Match(lineText).Groups["prefix"].Value;
        var before = lineText[..^globalPrefix.Length];
        if (before.EndsWith('!') || before.EndsWith('.'))
        {
            return [];
        }

        replaceStart = caretOffset - globalPrefix.Length;
        return Filter(GlobalCandidates(current, procedure), globalPrefix);
    }

    public VbaHoverInfo? GetHover(VbaModule current, string text, int offset)
    {
        if (offset < 0 || offset > text.Length)
        {
            return null;
        }

        var start = offset;
        while (start > 0 && IsIdentifierChar(text[start - 1])) start--;
        var end = offset;
        while (end < text.Length && IsIdentifierChar(text[end])) end++;
        if (start == end)
        {
            return null;
        }

        var word = text[start..end];
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (IsInStringOrComment(text[lineStart..start]))
        {
            return null;
        }

        var procedure = current.ProcedureAtLine(LineNumberAt(text, start));

        if (start > 0 && text[start - 1] == '.')
        {
            var chainMatch = MemberContext.Match(text[lineStart..start]);
            IEnumerable<string>? chain = chainMatch.Success ? chainMatch.Groups["chain"].Value.Split('.') : FindWithExpression(text, lineStart)?.Split('.');
            if (chain is not null)
            {
                var item = MembersOfChain(chain, current, procedure).FirstOrDefault(i => i.Text.Equals(word, StringComparison.OrdinalIgnoreCase));
                return item is null ? null : new VbaHoverInfo(item.Description);
            }
        }

        var candidate = GlobalCandidates(current, procedure).FirstOrDefault(i => i.Text.Equals(word, StringComparison.OrdinalIgnoreCase) && i.Kind is not (VbaCompletionKind.Keyword or VbaCompletionKind.Builtin or VbaCompletionKind.DataType));
        return candidate is null ? null : new VbaHoverInfo(candidate.Description);
    }

    private IEnumerable<VbaCompletionItem> GlobalCandidates(VbaModule current, VbaProcedure? procedure)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<VbaCompletionItem> Emit(IEnumerable<VbaCompletionItem> items)
        {
            foreach (var item in items)
            {
                if (seen.Add(item.Text))
                {
                    yield return item;
                }
            }
        }

        if (procedure is not null)
        {
            foreach (var item in Emit(procedure.Parameters.Select(p => new VbaCompletionItem(p.Name, VbaCompletionKind.Parameter, $"{p.Display} - parameter of {procedure.Name}", 3))))
                yield return item;
            foreach (var item in Emit(procedure.Locals.Select(v => new VbaCompletionItem(v.Name, v.IsConst ? VbaCompletionKind.Constant : VbaCompletionKind.Variable, $"{v.Display} - local in {procedure.Name}", 3))))
                yield return item;
        }

        foreach (var item in Emit(ModuleMembers(current, includePrivate: true, priority: 2)))
            yield return item;

        if (current.IsClassLike)
        {
            foreach (var item in Emit([new VbaCompletionItem("Me", VbaCompletionKind.Variable, $"Me - this {current.Kind} module ({current.Name})", 2)]))
                yield return item;
        }

        foreach (var module in Index.Modules)
        {
            if (module.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = module.Kind == VbaModuleKind.Standard ? VbaCompletionKind.Module : VbaCompletionKind.Class;
            foreach (var item in Emit([new VbaCompletionItem(module.Name, kind, $"{module.Kind} module {module.Name} - {module.Procedures.Count} procedures", 1)]))
                yield return item;

            if (module.Kind == VbaModuleKind.Standard)
            {
                foreach (var item in Emit(ModuleMembers(module, includePrivate: false, priority: 1)))
                    yield return item;
            }
            else
            {
                // Class enums and types are still reachable unqualified; procedures need an instance.
                foreach (var item in Emit(EnumAndTypeMembers(module, includePrivate: false, priority: 1)))
                    yield return item;
            }
        }

        foreach (var item in Emit(VbaWellKnown.GlobalNames.Select(g => new VbaCompletionItem(g, VbaCompletionKind.Builtin, $"{g} - Access object", 1))))
            yield return item;

        foreach (var item in Emit(language.Builtins.Select(b => new VbaCompletionItem(b, VbaCompletionKind.Builtin, $"{b} - built-in function", 0))))
            yield return item;
        foreach (var item in Emit(language.Constants.Select(c => new VbaCompletionItem(c, VbaCompletionKind.Constant, $"{c} - intrinsic constant", 0))))
            yield return item;
        foreach (var item in Emit(language.DataTypes.Select(t => new VbaCompletionItem(t, VbaCompletionKind.DataType, $"{t} - data type", 0))))
            yield return item;
        foreach (var item in Emit(language.Keywords.Select(k => new VbaCompletionItem(k, VbaCompletionKind.Keyword, $"{k} - keyword", 0))))
            yield return item;
    }

    private static IEnumerable<VbaCompletionItem> ModuleMembers(VbaModule module, bool includePrivate, int priority)
    {
        var location = $" - {module.Name}";
        var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in module.Procedures)
        {
            if (!includePrivate && proc.Visibility == VbaVisibility.Private)
            {
                continue;
            }

            if (proc.IsProperty && !seenProperties.Add(proc.Name))
            {
                continue;
            }

            var kind = proc.Kind == VbaProcedureKind.Event ? VbaCompletionKind.Event : proc.IsProperty ? VbaCompletionKind.Property : VbaCompletionKind.Procedure;
            yield return new VbaCompletionItem(proc.Name, kind, proc.Signature + location, priority);
        }

        foreach (var field in module.Fields)
        {
            if (includePrivate || field.Visibility == VbaVisibility.Public)
            {
                yield return new VbaCompletionItem(field.Name, field.IsConst ? VbaCompletionKind.Constant : VbaCompletionKind.Variable, field.Display + location, priority);
            }
        }

        foreach (var item in EnumAndTypeMembers(module, includePrivate, priority))
        {
            yield return item;
        }
    }

    private static IEnumerable<VbaCompletionItem> EnumAndTypeMembers(VbaModule module, bool includePrivate, int priority)
    {
        var location = $" - {module.Name}";
        foreach (var e in module.Enums)
        {
            if (!includePrivate && e.Visibility == VbaVisibility.Private)
            {
                continue;
            }

            yield return new VbaCompletionItem(e.Name, VbaCompletionKind.Enum, $"Enum {e.Name} ({e.Members.Count} members){location}", priority);
            foreach (var member in e.Members)
            {
                yield return new VbaCompletionItem(member.Name, VbaCompletionKind.EnumMember, $"{e.Name}.{member.Name}{(member.Value is null ? "" : " = " + member.Value)}{location}", priority);
            }
        }

        foreach (var t in module.Types)
        {
            if (includePrivate || t.Visibility == VbaVisibility.Public)
            {
                yield return new VbaCompletionItem(t.Name, VbaCompletionKind.UserType, $"Type {t.Name} ({t.Fields.Count} fields){location}", priority);
            }
        }
    }

    private IEnumerable<VbaCompletionItem> MembersOfChain(IEnumerable<string> chain, VbaModule current, VbaProcedure? procedure)
    {
        var parts = chain.ToList();
        if (parts.Count == 0)
        {
            return [];
        }

        var resolved = ResolveIdentifier(parts[0], current, procedure);
        for (var i = 1; i < parts.Count && resolved is not null; i++)
        {
            resolved = MemberType(resolved, parts[i]);
        }

        return resolved is null ? [] : MembersOf(resolved, current);
    }

    /// <summary>Resolution result: a module (by name), a well-known type, an enum, or a user type.</summary>
    private sealed record Resolved(string Name, string Category);

    private Resolved? ResolveIdentifier(string identifier, VbaModule current, VbaProcedure? procedure)
    {
        if (identifier.Equals("Me", StringComparison.OrdinalIgnoreCase) && current.IsClassLike)
        {
            return new Resolved(current.Name, "module");
        }

        var declaredType = procedure?.Parameters.FirstOrDefault(p => p.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))?.Type
            ?? procedure?.Locals.FirstOrDefault(v => v.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))?.Type
            ?? current.Fields.FirstOrDefault(v => v.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))?.Type
            ?? Index.Modules.Where(m => m.Kind == VbaModuleKind.Standard)
                .SelectMany(m => m.Fields.Where(f => f.Visibility == VbaVisibility.Public))
                .FirstOrDefault(f => f.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))?.Type;
        if (declaredType is not null)
        {
            return ResolveTypeName(declaredType);
        }

        if (Index.GetModule(identifier) is { } module)
        {
            return new Resolved(module.Name, "module");
        }

        if (current.Enums.Concat(Index.Modules.SelectMany(m => m.Enums)).Any(e => e.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase)))
        {
            return new Resolved(identifier, "enum");
        }

        var wellKnown = VbaWellKnown.GlobalType(identifier);
        return wellKnown is null ? null : new Resolved(wellKnown, "wellknown");
    }

    private Resolved? ResolveTypeName(string typeName)
    {
        var normalized = VbaWellKnown.NormalizeType(typeName.TrimEnd('(', ')'));
        if (Index.GetModule(normalized) is { } module)
        {
            return new Resolved(module.Name, "module");
        }

        if (Index.Modules.SelectMany(m => m.Enums).Any(e => e.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return new Resolved(normalized, "enum");
        }

        if (Index.Modules.SelectMany(m => m.Types).Any(t => t.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return new Resolved(normalized, "type");
        }

        return VbaWellKnown.MembersOf(normalized) is null ? null : new Resolved(normalized, "wellknown");
    }

    private Resolved? MemberType(Resolved receiver, string member)
    {
        if (receiver.Category == "module" && Index.GetModule(receiver.Name) is { } module)
        {
            var proc = module.Procedures.FirstOrDefault(p => p.Name.Equals(member, StringComparison.OrdinalIgnoreCase) && p.ReturnType is not null);
            if (proc is not null)
            {
                return ResolveTypeName(proc.ReturnType!);
            }

            var field = module.Fields.FirstOrDefault(f => f.Name.Equals(member, StringComparison.OrdinalIgnoreCase));
            return field?.Type is null ? null : ResolveTypeName(field.Type);
        }

        if (receiver.Category == "type")
        {
            var field = Index.Modules.SelectMany(m => m.Types).FirstOrDefault(t => t.Name.Equals(receiver.Name, StringComparison.OrdinalIgnoreCase))?
                .Fields.FirstOrDefault(f => f.Name.Equals(member, StringComparison.OrdinalIgnoreCase));
            return field?.Type is null ? null : ResolveTypeName(field.Type);
        }

        if (receiver.Category == "wellknown")
        {
            // A few high-traffic return types; everything else stops here.
            return (receiver.Name, member.ToUpperInvariant()) switch
            {
                ("Application", "CURRENTDB") => new Resolved("Database", "wellknown"),
                ("Application", "CURRENTPROJECT") => new Resolved("CurrentProject", "wellknown"),
                ("Application", "DOCMD") => new Resolved("DoCmd", "wellknown"),
                ("Application", "SCREEN") => new Resolved("Screen", "wellknown"),
                ("Database", "OPENRECORDSET") => new Resolved("Recordset", "wellknown"),
                ("Database", "QUERYDEFS") => new Resolved("QueryDefs", "wellknown"),
                ("Database", "TABLEDEFS") => new Resolved("TableDefs", "wellknown"),
                ("Database", "CREATEQUERYDEF") => new Resolved("QueryDef", "wellknown"),
                ("QueryDef", "OPENRECORDSET") => new Resolved("Recordset", "wellknown"),
                ("TableDef", "OPENRECORDSET") => new Resolved("Recordset", "wellknown"),
                ("Recordset", "FIELDS") => new Resolved("Fields", "wellknown"),
                ("Recordset", "CLONE") => new Resolved("Recordset", "wellknown"),
                ("Form", "RECORDSET") or ("Form", "RECORDSETCLONE") => new Resolved("Recordset", "wellknown"),
                ("Form", "FORM") => new Resolved("Form", "wellknown"),
                ("Control", "FORM") => new Resolved("Form", "wellknown"),
                ("Screen", "ACTIVEFORM") => new Resolved("Form", "wellknown"),
                ("Screen", "ACTIVECONTROL") => new Resolved("Control", "wellknown"),
                _ => null,
            };
        }

        return null;
    }

    private IEnumerable<VbaCompletionItem> MembersOf(Resolved resolved, VbaModule current)
    {
        switch (resolved.Category)
        {
            case "module":
            {
                var module = Index.GetModule(resolved.Name) ?? (resolved.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase) ? current : null);
                if (module is null)
                {
                    yield break;
                }

                var isSelf = module.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase);
                foreach (var item in ModuleMembers(module, includePrivate: isSelf, priority: 2))
                {
                    yield return item;
                }

                var wellKnown = module.Kind == VbaModuleKind.Document
                    ? (module.Name.StartsWith("Report_", StringComparison.OrdinalIgnoreCase) ? "Report" : "Form")
                    : null;
                if (wellKnown is not null)
                {
                    foreach (var member in VbaWellKnown.MembersOf(wellKnown) ?? [])
                    {
                        yield return new VbaCompletionItem(member, VbaCompletionKind.Member, $"{member} - {wellKnown} member", 1);
                    }
                }

                break;
            }

            case "enum":
            {
                var e = current.Enums.Concat(Index.Modules.SelectMany(m => m.Enums)).FirstOrDefault(x => x.Name.Equals(resolved.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var member in e?.Members ?? [])
                {
                    yield return new VbaCompletionItem(member.Name, VbaCompletionKind.EnumMember, $"{e!.Name}.{member.Name}{(member.Value is null ? "" : " = " + member.Value)}", 2);
                }

                break;
            }

            case "type":
            {
                var t = Index.Modules.SelectMany(m => m.Types).FirstOrDefault(x => x.Name.Equals(resolved.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var field in t?.Fields ?? [])
                {
                    yield return new VbaCompletionItem(field.Name, VbaCompletionKind.Member, $"{field.Name} As {field.Type ?? "Variant"} - field of Type {t!.Name}", 2);
                }

                break;
            }

            default:
            {
                foreach (var member in VbaWellKnown.MembersOf(resolved.Name) ?? [])
                {
                    yield return new VbaCompletionItem(member, VbaCompletionKind.Member, $"{member} - {resolved.Name} member", 2);
                }

                break;
            }
        }
    }

    private static IReadOnlyList<VbaCompletionItem> Filter(IEnumerable<VbaCompletionItem> items, string prefix)
        => items
            .Where(i => prefix.Length == 0 || i.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? FindWithExpression(string text, int lineStart)
    {
        var depth = 0;
        var position = lineStart;
        while (position > 0)
        {
            var previousEnd = position - 1;
            var previousStart = text.LastIndexOf('\n', Math.Max(0, previousEnd - 1)) + 1;
            if (previousEnd < previousStart)
            {
                break;
            }

            var line = VbaModuleParser.StripComment(text[previousStart..previousEnd]);
            if (WithEnd.IsMatch(line))
            {
                depth++;
            }
            else if (WithStart.Match(line) is { Success: true } m)
            {
                if (depth == 0)
                {
                    return m.Groups["expr"].Value.Trim();
                }

                depth--;
            }

            position = previousStart;
        }

        return null;
    }

    private static bool IsInStringOrComment(string lineText)
    {
        var inString = false;
        foreach (var c in lineText)
        {
            if (c == '"')
            {
                inString = !inString;
            }
            else if (c == '\'' && !inString)
            {
                return true;
            }
        }

        return inString;
    }

    private static int LineNumberAt(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
