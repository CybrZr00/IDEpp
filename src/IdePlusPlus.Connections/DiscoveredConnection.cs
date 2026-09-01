namespace IdePlusPlus.Connections;

/// <summary>One connection string found in (or manually added on top of) the open solution's config
/// files. RawConnectionString is a live secret in most cases - never log or display it directly, use
/// ConnectionStringRedactor.Redact instead; the one deliberate exception is an explicit,
/// user-initiated "copy full connection string" action.</summary>
public sealed record DiscoveredConnection(
    string Name,
    string RawConnectionString,
    string SourceFilePath,
    string? ProjectPath,
    ConnectionSourceKind SourceKind);
