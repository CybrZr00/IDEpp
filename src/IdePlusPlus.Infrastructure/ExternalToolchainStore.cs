using System.IO.Compression;
using System.Security.Cryptography;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Detects and installs the external reverse-engineering toolchains (Ghidra, a JDK) on demand.
/// Detect-first: if a usable copy is already on the machine (the common case - this box has Ghidra
/// 12.1.4 and a Temurin JDK 21 already), nothing is downloaded. Otherwise a pinned, SHA-256-verified
/// archive is fetched into <c>%LOCALAPPDATA%\IdePlusPlus\tools\&lt;id&gt;</c> and unzipped.
///
/// The download and the install-from-archive steps are separate so the archive handling can be tested
/// without the network.
/// </summary>
public sealed class ExternalToolchainStore
{
    public const string GhidraId = "ghidra";
    public const string Jdk21Id = "jdk21";

    private readonly string _toolsRoot;
    private readonly IReadOnlyList<ExternalTool> _tools;
    private readonly Func<string, string?> _detect;

    public ExternalToolchainStore(
        string? toolsRoot = null, IReadOnlyList<ExternalTool>? tools = null, Func<string, string?>? detect = null)
    {
        _toolsRoot = toolsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdePlusPlus", "tools");
        _tools = tools ?? DefaultTools;
        _detect = detect ?? DetectDefault;
    }

    /// <summary>The Ghidra and JDK downloads are pinned to the versions this machine already runs
    /// (Ghidra 12.1.4, Temurin JDK 21) - Ghidra 12.2 would need JDK 25 and REA only verifies the
    /// 12.1.x line, so we deliberately stay here. Detect-first means these URLs are a fallback.</summary>
    public static IReadOnlyList<ExternalTool> DefaultTools { get; } =
    [
        new(
            GhidraId,
            "Ghidra",
            "NSA Ghidra 12.1.4 - the native decompiler backend. Apache-2.0.",
            "12.1.4",
            "https://github.com/NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip",
            "ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db",
            571_000_000,
            Path.Combine("support", "analyzeHeadless.bat")),
        new(
            Jdk21Id,
            "Temurin JDK 21",
            "Eclipse Temurin JDK 21 (x64) - the Java runtime Ghidra needs. GPLv2+CE.",
            "21.0.13+9",
            "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_x64_windows_hotspot_21.0.13_9.zip",
            "a4dbaa05fca4bd89d84584e58c459bd732efebb1722af80ab13c6516832004f9",
            205_000_000,
            Path.Combine("bin", "java.exe")),
    ];

    public IReadOnlyList<ExternalTool> Tools => _tools;

    public ExternalTool? Find(string id) => _tools.FirstOrDefault(t => t.Id == id);

    public string InstallDirectory(string id) => Path.Combine(_toolsRoot, id);

    public bool IsInstalled(string id) => File.Exists(InstalledMarker(id));

    /// <summary>A usable copy already on the machine (env var or known path), or null.</summary>
    public string? DetectExternal(string id) => _detect(id);

    public ToolchainState State(string id) =>
        IsInstalled(id) ? ToolchainState.Installed
        : DetectExternal(id) is not null ? ToolchainState.DetectedExternally
        : ToolchainState.NotInstalled;

    /// <summary>The usable root of the tool - a detected external copy, or inside our install directory
    /// (handling a zip whose contents sit under a single top-level folder) - or null if unavailable.</summary>
    public string? ResolveToolRoot(string id)
    {
        if (DetectExternal(id) is { } external)
        {
            return external;
        }

        if (!IsInstalled(id) || Find(id) is not { } tool)
        {
            return null;
        }

        var directory = InstallDirectory(id);
        if (File.Exists(Path.Combine(directory, tool.ProbeRelativePath)))
        {
            return directory;
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(directory))
        {
            if (File.Exists(Path.Combine(subdirectory, tool.ProbeRelativePath)))
            {
                return subdirectory;
            }
        }

        return directory;
    }

    /// <summary>Downloads the tool's pinned archive, verifies its SHA-256, and unzips it into the tools
    /// directory. Progress is reported in MB.</summary>
    public async Task InstallAsync(string id, HttpClient httpClient, Action<string> onProgress, CancellationToken cancellationToken)
    {
        if (Find(id) is not { } tool)
        {
            throw new ArgumentException($"Unknown tool '{id}'.", nameof(id));
        }

        Directory.CreateDirectory(_toolsRoot);
        var archivePath = Path.Combine(_toolsRoot, id + ".zip.part");
        try
        {
            onProgress($"Downloading {tool.DisplayName} {tool.Version}...");
            using (var response = await httpClient.GetAsync(tool.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? tool.DownloadBytes;

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = File.Create(archivePath);
                var buffer = new byte[1 << 20];
                long received = 0;
                var lastReport = 0L;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (received - lastReport >= 10_000_000)
                    {
                        lastReport = received;
                        onProgress($"Downloading {tool.DisplayName}: {received / 1_000_000} of {total / 1_000_000} MB...");
                    }
                }
            }

            onProgress($"Verifying {tool.DisplayName}...");
            await using (var check = File.OpenRead(archivePath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken));
                if (!hash.Equals(tool.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{tool.DisplayName} did not match its expected checksum.");
                }
            }

            onProgress($"Extracting {tool.DisplayName}...");
            InstallFromArchive(id, archivePath);
            onProgress($"{tool.DisplayName} installed.");
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    /// <summary>Extracts an already-downloaded archive into place and writes the installed marker.
    /// Internal so the extract/move/marker flow can be tested without a download.</summary>
    internal void InstallFromArchive(string id, string archivePath)
    {
        if (Find(id) is not { } tool)
        {
            throw new ArgumentException($"Unknown tool '{id}'.", nameof(id));
        }

        var directory = InstallDirectory(id);
        var staging = directory + ".extract";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        ZipFile.ExtractToDirectory(archivePath, staging);

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.Move(staging, directory);
        File.WriteAllText(InstalledMarker(id), tool.Version);
    }

    private string InstalledMarker(string id) => Path.Combine(InstallDirectory(id), ".ideplusplus-installed");

    private static string? DetectDefault(string id) => id switch
    {
        GhidraId => DetectGhidra(),
        Jdk21Id => DetectJdk21(),
        _ => null,
    };

    private static string? DetectGhidra()
    {
        bool HasHeadless(string? dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "support", "analyzeHeadless.bat"));

        if (Environment.GetEnvironmentVariable("GHIDRA_INSTALL_DIR") is { } fromEnv && HasHeadless(fromEnv))
        {
            return fromEnv;
        }

        foreach (var root in new[] { @"C:\ghidra", @"C:\Program Files" })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var candidate in SafeEnumerateDirectories(root, "ghidra_*"))
            {
                if (HasHeadless(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? DetectJdk21()
    {
        bool IsJdk21(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "bin", "java.exe")))
            {
                return false;
            }

            var release = Path.Combine(dir, "release");
            // A JAVA_HOME pointing at some other major version must not be mistaken for 21.
            return !File.Exists(release) || File.ReadAllText(release).Contains("JAVA_VERSION=\"21");
        }

        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { } fromEnv && IsJdk21(fromEnv))
        {
            return fromEnv;
        }

        var adoptium = @"C:\Program Files\Eclipse Adoptium";
        if (Directory.Exists(adoptium))
        {
            foreach (var candidate in SafeEnumerateDirectories(adoptium, "jdk-21*"))
            {
                if (IsJdk21(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(root, pattern);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
