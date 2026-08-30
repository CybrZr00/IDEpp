namespace IdePlusPlus.Templates;

/// <summary>
/// Validates the name typed into the New Project / New Item dialogs, before anything is spawned.
///
/// <para>Pre-checking rather than letting <c>dotnet new</c> refuse matters because of how it refuses:
/// a name that would overwrite an existing file produces prose on stdout and exit code 73, not a
/// crash, which is easy to mistake for success. Catching what can be caught in the dialog keeps the
/// error next to the field that caused it.</para>
/// </summary>
public static class TemplateNameValidator
{
    /// <summary>Reserved on Windows with or without an extension - CON.cs is as unusable as CON. Kept
    /// as the DOS device list rather than being derived, since that is what it is.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A project name becomes a directory and an assembly name, so dots are fine
    /// (IdePlusPlus.Templates) but path separators are not.</summary>
    public static string? ValidateProjectName(string? name) => ValidatePathSegment(name, "project name");

    /// <summary>For item templates that produce something other than a type - a .editorconfig, a
    /// .proto file - where the name only has to be a usable file name.</summary>
    public static string? ValidateFileName(string? name) => ValidatePathSegment(name, "name");

    /// <summary>
    /// For item templates whose name becomes a type name: Class, Interface, Record, API Controller.
    /// The file-name rules still apply on top, since the name is also the file it lands in.
    /// </summary>
    public static string? ValidateTypeName(string? name)
    {
        if (ValidatePathSegment(name, "name") is { } error)
        {
            return error;
        }

        return IsIdentifier(name!) ? null : "A type name must start with a letter or underscore and contain only letters, digits and underscores.";
    }

    private static string? ValidatePathSegment(string? name, string noun)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return $"Enter a {noun}.";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return $"A {noun} can't contain any of \\ / : * ? \" < > |";
        }

        // Windows silently strips these when creating the file, so the project would end up at a path
        // other than the one the dialog showed.
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return $"A {noun} can't end with a dot or a space.";
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(name);
        if (ReservedDeviceNames.Contains(withoutExtension) || ReservedDeviceNames.Contains(name))
        {
            return $"'{name}' is a reserved Windows device name.";
        }

        return null;
    }

    private static bool IsIdentifier(string name)
    {
        if (!char.IsLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        return name.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
