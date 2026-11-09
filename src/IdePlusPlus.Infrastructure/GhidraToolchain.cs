namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Resolves the Ghidra headless launcher and the environment it needs (GHIDRA_INSTALL_DIR, JAVA_HOME)
/// from whatever copy of Ghidra and a JDK the <see cref="ExternalToolchainStore"/> found or installed.
/// Like <see cref="NativeToolchain"/>, this is just resolution - running analyzeHeadless is the
/// backend's job.
/// </summary>
public sealed class GhidraToolchain
{
    private readonly ExternalToolchainStore _store;

    public GhidraToolchain(ExternalToolchainStore store) => _store = store;

    public string? GhidraRoot => _store.ResolveToolRoot(ExternalToolchainStore.GhidraId);

    public string? JdkRoot => _store.ResolveToolRoot(ExternalToolchainStore.Jdk21Id);

    /// <summary>The analyzeHeadless launcher, or null if Ghidra is not available.</summary>
    public string? AnalyzeHeadlessPath =>
        GhidraRoot is { } root ? Path.Combine(root, "support", "analyzeHeadless.bat") : null;

    /// <summary>True when both Ghidra's launcher and a JDK are present, so the backend can run.</summary>
    public bool IsAvailable =>
        AnalyzeHeadlessPath is { } launcher && File.Exists(launcher) && JdkRoot is not null;

    /// <summary>The environment variables analyzeHeadless needs. Empty when the toolchain is not
    /// available (callers should check <see cref="IsAvailable"/> first).</summary>
    public IReadOnlyDictionary<string, string> Environment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (GhidraRoot is { } ghidra)
        {
            environment["GHIDRA_INSTALL_DIR"] = ghidra;
        }

        if (JdkRoot is { } jdk)
        {
            environment["JAVA_HOME"] = jdk;
        }

        return environment;
    }
}
