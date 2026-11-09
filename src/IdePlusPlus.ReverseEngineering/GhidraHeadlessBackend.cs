using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.ReverseEngineering;

/// <summary>An opened native binary: the cached Ghidra project and the program name inside it.</summary>
public sealed record GhidraSession(string BinaryPath, string ProgramName, string ProjectDirectory, string ProjectName);

/// <summary>One function in a native binary.</summary>
public sealed record GhidraFunction(string Name, string Address);

/// <summary>The result of opening a binary: the session (null on failure) and a message (the overview, or the error).</summary>
public sealed record GhidraOpenResult(GhidraSession? Session, bool IsError, string Text);

/// <summary>
/// Drives Ghidra headless (analyzeHeadless) against a per-binary cached project. The first open
/// imports and analyzes the binary and saves the project; every later operation runs a fast
/// -process -noanalysis pass that invokes <c>IdePpOp.java</c> and reads back its JSON. Read operations
/// pass -readOnly; rename and comment do not, so they are saved and persist across runs - what REA
/// cannot do on Windows. This is deliberately one process per call (no long-lived JVM) for simplicity;
/// a persistent bridge can replace it later if the ~seconds of JVM start-up per call matters.
/// </summary>
public sealed class GhidraHeadlessBackend
{
    public const string ProjectName = "ideplusplus";

    private const int MaxLogChars = 20_000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(15);

    private readonly GhidraToolchain _toolchain;
    private readonly string _projectsRoot;
    private readonly string _scriptDirectory;

    public GhidraHeadlessBackend(GhidraToolchain toolchain, string? projectsRoot = null, string? scriptDirectory = null)
    {
        _toolchain = toolchain;
        _projectsRoot = projectsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdePlusPlus", "tools", "ghidra-projects");
        _scriptDirectory = scriptDirectory ?? Path.Combine(
            Path.GetDirectoryName(typeof(GhidraHeadlessBackend).Assembly.Location) ?? ".", "GhidraScripts");
    }

    public bool IsAvailable => _toolchain.IsAvailable;

    public static string UnavailableMessage =>
        "Ghidra is not installed. Install it in Settings - Reverse engineering tools, then try again.";

    public async Task<GhidraOpenResult> OpenAsync(string binaryPath, Action<string> onProgress, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return new GhidraOpenResult(null, true, UnavailableMessage);
        }

        if (!File.Exists(binaryPath))
        {
            return new GhidraOpenResult(null, true, $"No file found at '{binaryPath}'.");
        }

        var session = SessionFor(binaryPath);
        var marker = Path.Combine(session.ProjectDirectory, ".imported");
        if (!File.Exists(marker))
        {
            Directory.CreateDirectory(session.ProjectDirectory);
            onProgress($"Importing {session.ProgramName} into Ghidra - the first open analyzes it, which can take a while...");
            var import = await RunAsync(ImportArguments(session), onProgress, cancellationToken);
            if (import.ExitCode != 0)
            {
                return new GhidraOpenResult(null, true, $"Ghidra import failed (exit {import.ExitCode}).\n{import.Output}");
            }

            await File.WriteAllTextAsync(marker, binaryPath, cancellationToken);
        }

        var (isError, text) = await OverviewAsync(session, onProgress, cancellationToken);
        // The project is imported, so the session is usable even if this one overview pass failed.
        return new GhidraOpenResult(session, isError, text);
    }

    public Task<(bool IsError, string Text)> OverviewAsync(GhidraSession session, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunOperationAsync(session, "overview", [], onProgress, cancellationToken);

    public Task<(bool IsError, string Text)> FunctionsAsync(GhidraSession session, string? filter, int limit, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunOperationAsync(session, "functions", new Dictionary<string, string?> { ["filter"] = filter, ["limit"] = limit.ToString() }, onProgress, cancellationToken);

    public Task<(bool IsError, string Text)> DecompileAsync(GhidraSession session, string target, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunOperationAsync(session, "decompile", new Dictionary<string, string?> { ["target"] = target }, onProgress, cancellationToken);

    public Task<(bool IsError, string Text)> XrefsAsync(GhidraSession session, string target, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunOperationAsync(session, "xrefs", new Dictionary<string, string?> { ["target"] = target }, onProgress, cancellationToken);

    public Task<(bool IsError, string Text)> CallGraphAsync(GhidraSession session, string target, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunOperationAsync(session, "callgraph", new Dictionary<string, string?> { ["target"] = target }, onProgress, cancellationToken);

    public Task<(bool IsError, string Text)> RenameAsync(GhidraSession session, string target, string newName, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunOperationAsync(session, "rename", new Dictionary<string, string?> { ["target"] = target, ["newName"] = newName }, onProgress, cancellationToken);

    public Task<(bool IsError, string Text)> CommentAsync(GhidraSession session, string target, string comment, Action<string> onProgress, CancellationToken cancellationToken) =>
        RunOperationAsync(session, "comment", new Dictionary<string, string?> { ["target"] = target, ["comment"] = comment }, onProgress, cancellationToken);

    private GhidraSession SessionFor(string binaryPath)
    {
        using var stream = File.OpenRead(binaryPath);
        var hash = Convert.ToHexString(SHA256.HashData(stream))[..12].ToLowerInvariant();
        return new GhidraSession(binaryPath, Path.GetFileName(binaryPath), Path.Combine(_projectsRoot, hash), ProjectName);
    }

    /// <summary>Runs an operation and returns its formatted, model-ready text.</summary>
    private async Task<(bool IsError, string Text)> RunOperationAsync(
        GhidraSession session, string op, Dictionary<string, string?> input, Action<string> onProgress, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return (true, UnavailableMessage);
        }

        var (json, error) = await RunRawAsync(session, op, input, onProgress, cancellationToken);
        return json is null ? (true, error) : GhidraResultFormatter.Format(op, json);
    }

    /// <summary>The function list as structured data (for the Binary Explorer), rather than formatted text.</summary>
    public async Task<IReadOnlyList<GhidraFunction>> ListFunctionsStructuredAsync(
        GhidraSession session, string? filter, int limit, Action<string> onProgress, CancellationToken cancellationToken)
    {
        var (json, _) = await RunRawAsync(
            session, "functions", new Dictionary<string, string?> { ["filter"] = filter, ["limit"] = limit.ToString() }, onProgress, cancellationToken);
        if (json is null)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("functions", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                return array.EnumerateArray()
                    .Select(e => new GhidraFunction(
                        e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        e.TryGetProperty("address", out var a) ? a.GetString() ?? "" : ""))
                    .Where(f => f.Name.Length > 0)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Fall through to empty.
        }

        return [];
    }

    private async Task<(string? Json, string Error)> RunRawAsync(
        GhidraSession session, string op, Dictionary<string, string?> input, Action<string> onProgress, CancellationToken cancellationToken)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"ideppre-{Guid.NewGuid():N}.in");
        var outputPath = Path.Combine(Path.GetTempPath(), $"ideppre-{Guid.NewGuid():N}.out");
        try
        {
            await File.WriteAllTextAsync(inputPath, EncodeInput(input), cancellationToken);
            var mutating = op is "rename" or "comment";
            var result = await RunAsync(OperationArguments(session, op, inputPath, outputPath, mutating), onProgress, cancellationToken);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                return (null, $"Ghidra produced no result (exit {result.ExitCode}).\n{result.Output}");
            }

            return (await File.ReadAllTextAsync(outputPath, cancellationToken), "");
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private List<string> ImportArguments(GhidraSession session) =>
    [
        _toolchain.AnalyzeHeadlessPath!,
        session.ProjectDirectory,
        session.ProjectName,
        "-import", session.BinaryPath,
        "-overwrite",
        "-log", Path.Combine(session.ProjectDirectory, "ghidra.log"),
    ];

    private List<string> OperationArguments(GhidraSession session, string op, string inputPath, string outputPath, bool mutating)
    {
        var arguments = new List<string>
        {
            _toolchain.AnalyzeHeadlessPath!,
            session.ProjectDirectory,
            session.ProjectName,
            "-process", session.ProgramName,
            "-noanalysis",
            "-scriptPath", _scriptDirectory,
            "-postScript", "IdePpOp.java", op, inputPath, outputPath,
            "-log", Path.Combine(session.ProjectDirectory, "ghidra.log"),
        };

        if (!mutating)
        {
            arguments.Add("-readOnly");
        }

        return arguments;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> parts, Action<string> onProgress, CancellationToken cancellationToken)
    {
        // analyzeHeadless is a .bat, so it runs through cmd. /s /c "<inner>" makes cmd strip only the
        // outer quotes and run the inner verbatim, so the per-path quotes inside survive.
        var inner = string.Join(" ", parts.Select(Quote));
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/s /c \"{inner}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var (key, value) in _toolchain.Environment())
        {
            startInfo.Environment[key] = value;
        }

        var output = new StringBuilder();
        void Append(string line)
        {
            lock (output)
            {
                if (output.Length < MaxLogChars)
                {
                    output.AppendLine(line);
                }
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onProgress(e.Data);
                Append(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Append(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (-1, output + "\n[Ghidra timed out and was stopped.]");
        }

        return (process.ExitCode, output.ToString());
    }

    private static string EncodeInput(Dictionary<string, string?> input)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in input)
        {
            if (value is null)
            {
                continue;
            }

            // "limit" is a plain integer; everything else is Base64 so arbitrary text survives.
            builder.Append(key).Append('=')
                .Append(key == "limit" ? value : Convert.ToBase64String(Encoding.UTF8.GetBytes(value)))
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string Quote(string part) =>
        part.Contains(' ') || part.Contains('\t') ? $"\"{part}\"" : part;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Temp file; leaking one is harmless.
        }
    }
}
