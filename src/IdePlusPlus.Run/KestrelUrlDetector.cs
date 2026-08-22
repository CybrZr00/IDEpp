using System.Text.RegularExpressions;

namespace IdePlusPlus.Run;

/// <summary>
/// Recognizes ASP.NET Core's Kestrel startup log line ("Now listening on: https://...") in a
/// process's stdout, so Run/Debug know when - and at what URL - to open a browser. Using the actual
/// bound URL from output rather than trusting a launch profile's applicationUrl verbatim, since the
/// latter doesn't reflect a dynamically-assigned port.
/// </summary>
public static partial class KestrelUrlDetector
{
    public static string? TryExtractListeningUrl(string outputLine)
    {
        var match = ListeningRegex().Match(outputLine);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Now listening on:\s*(\S+)")]
    private static partial Regex ListeningRegex();
}
