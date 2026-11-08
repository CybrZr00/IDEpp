namespace IdePlusPlus.Infrastructure;

/// <summary>Where a reverse-engineering toolchain stands on this machine.</summary>
public enum ToolchainState
{
    /// <summary>Neither detected on the machine nor downloaded by IDE++.</summary>
    NotInstalled,

    /// <summary>Found already installed elsewhere (an env var or a known path), so no download is needed.</summary>
    DetectedExternally,

    /// <summary>Downloaded by IDE++ into its own tools directory.</summary>
    Installed,
}

/// <summary>
/// One external tool IDE++ can install on demand (Ghidra, a JDK, ...). The download is pinned to a
/// version and a SHA-256, so a corrupt or substituted archive is rejected rather than run - the same
/// guard <see cref="IdePlusPlus.Copilot.Embeddings"/>'s model download uses. ProbeRelativePath is the
/// file that proves the archive extracted to a usable layout (and finds the real root inside a zip
/// that has a single top-level folder).
/// </summary>
public sealed record ExternalTool(
    string Id,
    string DisplayName,
    string Description,
    string Version,
    string DownloadUrl,
    string Sha256,
    long DownloadBytes,
    string ProbeRelativePath);
