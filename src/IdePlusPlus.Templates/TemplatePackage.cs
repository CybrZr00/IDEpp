namespace IdePlusPlus.Templates;

/// <summary>
/// One installed template package, as the engine records it.
/// </summary>
/// <param name="MountPointUri">The .nupkg on disk. Packages that ship with the SDK live under
/// Program Files and are not the engine's to remove; ones installed by the user live under its own
/// packages folder - which is what <see cref="IsUserInstalled"/> tells apart.</param>
public sealed record TemplatePackage(
    string PackageId,
    string? Version,
    string? Author,
    string? MountPointUri)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Version) ? PackageId : $"{PackageId}  {Version}";

    /// <summary>
    /// Whether this package was installed with <c>dotnet new install</c> rather than shipped with the
    /// SDK. Only these can be uninstalled - the SDK's own templates come from its installation
    /// directory and are managed by the SDK installer.
    /// </summary>
    public bool IsUserInstalled =>
        MountPointUri is { } uri &&
        uri.Contains(".templateengine", StringComparison.OrdinalIgnoreCase);
}
