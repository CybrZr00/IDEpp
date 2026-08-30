using System.Text.Json;

namespace IdePlusPlus.Templates;

/// <summary>
/// Reads the engine's record of installed template packages - <c>packages.json</c>, beside the
/// per-SDK template caches.
///
/// <para>Same reasoning as <see cref="TemplateCacheReader"/>, and the same posture: parsing
/// <c>dotnet new uninstall</c>'s listing would mean reading a localized, tick-and-cross decorated
/// prose block, while this file states the same thing in JSON. And the same caveat - the format
/// belongs to the engine, so anything unreadable degrades to nothing rather than throwing.</para>
/// </summary>
public static class TemplatePackageReader
{
    /// <summary>Beside the per-SDK cache directories rather than inside one: package installs are not
    /// per-SDK-version.</summary>
    public static string PathFor(string cliHome) =>
        Path.Combine(cliHome, ".templateengine", "packages.json");

    /// <summary>Null when the file is unusable. An empty list means it was read and holds no
    /// packages - a machine with only the SDK's own templates, which is the common case.</summary>
    public static IReadOnlyList<TemplatePackage>? Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var results = new List<TemplatePackage>();
            foreach (var element in packages.EnumerateArray())
            {
                if (ReadPackage(element) is { } package)
                {
                    results.Add(package);
                }
            }

            return results;
        }
    }

    /// <summary>Reads from disk. A missing file is not a failure - it means nothing has ever been
    /// installed, which is an empty list rather than an error.</summary>
    public static IReadOnlyList<TemplatePackage> ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return Read(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static TemplatePackage? ReadPackage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("Details", out var details) ||
            details.ValueKind != JsonValueKind.Object ||
            ReadString(details, "PackageId") is not { } packageId)
        {
            return null;
        }

        return new TemplatePackage(
            packageId,
            ReadString(details, "Version"),
            ReadString(details, "Author"),
            ReadString(element, "MountPointUri"));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
