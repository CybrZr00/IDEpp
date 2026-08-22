namespace IdePlusPlus.NuGet;

/// <summary>One row from `dotnet list package --format json`. RequestedVersion is null when the
/// project's PackageReference has no explicit version and Central Package Management supplies it
/// centrally (or when the field is simply absent from the tool's output).</summary>
public sealed record InstalledPackage(string Id, string ResolvedVersion, string? RequestedVersion, bool IsTransitive);
