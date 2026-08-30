namespace IdePlusPlus.Templates;

/// <summary>
/// Works out the namespace a new file should get: the project's root namespace plus the folders
/// between the project and the file.
///
/// <para>This has to exist because <c>dotnet new</c> will not do it. Measured on the 10.0.301 SDK:
/// <c>dotnet new class -n Widget -o Models</c> inside a project whose RootNamespace is <c>Demo</c>
/// emits <c>namespace Demo;</c>, not <c>Demo.Models</c> - the Class template binds its namespace to
/// <c>msbuild:RootNamespace</c>, which knows nothing about the subfolder, and passing
/// <c>--project</c> does not change it. Visual Studio gets this right only because Add &gt; Class
/// uses VS's own .vstemplate items rather than the CLI.</para>
/// </summary>
public static class NamespaceResolver
{
    /// <summary>
    /// C# keywords can't be used as a namespace segment bare. A folder genuinely named "event" or
    /// "object" is unusual but entirely legal on disk, and the verbatim form (<c>@event</c>) is the
    /// one thing that both keeps the folder's name and compiles. Visual Studio emits the bare
    /// keyword here and produces a file that doesn't build.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    /// <summary>
    /// <paramref name="rootNamespace"/> plus each folder between the project and the target,
    /// sanitized to identifiers. A target outside the project directory - or the project directory
    /// itself - yields the root namespace unchanged.
    /// </summary>
    public static string Resolve(string rootNamespace, string projectDirectory, string targetDirectory)
    {
        var segments = FolderSegments(projectDirectory, targetDirectory);

        return segments.Count == 0
            ? rootNamespace
            : $"{rootNamespace}.{string.Join('.', segments)}";
    }

    private static List<string> FolderSegments(string projectDirectory, string targetDirectory)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(projectDirectory, targetDirectory);
        }
        catch (ArgumentException)
        {
            return [];
        }

        // "." is the project directory itself; ".." means the file is being added outside the
        // project, where there is no folder chain to fold in and the root namespace is the best
        // answer available.
        if (relative is "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return [];
        }

        return relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeSegment)
            .Where(segment => segment.Length > 0)
            .ToList();
    }

    /// <summary>
    /// One folder name as a namespace segment: anything that isn't a letter, digit or underscore
    /// becomes an underscore, a leading digit gets one prefixed, and a C# keyword gets the verbatim
    /// <c>@</c>.
    /// </summary>
    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        var characters = segment
            .Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')
            .ToArray();

        var sanitized = new string(characters);

        if (char.IsDigit(sanitized[0]))
        {
            sanitized = '_' + sanitized;
        }

        return Keywords.Contains(sanitized) ? '@' + sanitized : sanitized;
    }
}
