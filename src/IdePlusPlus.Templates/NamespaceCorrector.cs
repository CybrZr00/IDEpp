using System.Text;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Templates;

/// <summary>
/// Rewrites the namespace in a file <c>dotnet new</c> has just written, for the templates that give
/// no way to pass one.
///
/// <para>Templates split into two kinds. apicontroller, mvccontroller, razorcomponent, page and view
/// declare a <c>namespace</c> parameter, so the right value is passed in and none of this is needed -
/// though left unset they emit their parameter's literal default, <c>MyApp.Namespace</c>. class,
/// interface, enum, record and struct declare nothing: they bind to <c>msbuild:RootNamespace</c>,
/// which is folder-blind. For those the emitted namespace is known exactly - it is the project's
/// root namespace - so this replaces that one token rather than trying to parse C#.</para>
/// </summary>
public static partial class NamespaceCorrector
{
    /// <summary>
    /// Replaces <paramref name="boundNamespace"/> with <paramref name="desiredNamespace"/> on the
    /// file's namespace declaration. False when nothing needed changing, the file isn't C#, the
    /// declaration wasn't the expected one, or the file couldn't be read - a wrong namespace is
    /// worth fixing, not worth failing the whole creation over.
    /// </summary>
    public static bool TryCorrect(string filePath, string boundNamespace, string desiredNamespace)
    {
        if (boundNamespace == desiredNamespace ||
            string.IsNullOrWhiteSpace(boundNamespace) ||
            string.IsNullOrWhiteSpace(desiredNamespace) ||
            !string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var hasBom = HasUtf8Bom(bytes);
            var text = new UTF8Encoding(false).GetString(hasBom ? bytes[3..] : bytes);

            var corrected = Replace(text, boundNamespace, desiredNamespace);
            if (corrected == text)
            {
                return false;
            }

            // The BOM is preserved rather than normalized away: `dotnet new` writes one, and
            // silently changing a generated file's encoding is not this method's job.
            File.WriteAllBytes(filePath, new UTF8Encoding(hasBom).GetPreamble()
                .Concat(new UTF8Encoding(false).GetBytes(corrected))
                .ToArray());

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Exposed for testing: the pure text transform, with no file access.</summary>
    public static string Replace(string text, string boundNamespace, string desiredNamespace)
    {
        // Anchored to a line start and required to be followed by ';' (file-scoped) or '{'
        // (block-scoped), so a mention of the same name in a using directive, a type reference or a
        // comment is left alone. Only the first declaration is touched - a file with two namespaces
        // is not something a single-type item template produces.
        var pattern = $@"(?m)^(?<indent>[ \t]*)namespace[ \t]+{Regex.Escape(boundNamespace)}(?=[ \t]*[;{{]|[ \t]*$)";

        return Regex.Replace(
            text,
            pattern,
            match => $"{match.Groups["indent"].Value}namespace {desiredNamespace}",
            RegexOptions.None,
            TimeSpan.FromSeconds(2));
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
