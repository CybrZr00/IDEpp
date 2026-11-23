using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>What reading an artifact gave: the value, or the reason there is none. A bad file is a
/// result rather than an exception - a half-written or hand-edited artifact is something the run
/// should say and carry on from, not something that ends it.</summary>
public sealed record ArtifactReadResult<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The pipeline's run folder: the surveys, the design, each loop's verdicts and findings, and the
/// final report. The runner writes every file in it; an agent only ever reads, through a
/// <see cref="PipelineArtifactReader"/> that says which files that agent may see.
///
/// Every write goes to a temporary file in the same folder and is then moved over the target, so a
/// reader - or a run that is killed mid-write - sees the old file or the new one and never half of
/// either. JSON files carry a schema version so a later version of the app can read what an earlier
/// one left, or say plainly that it cannot.
/// </summary>
public sealed class PipelineArtifactStore
{
    /// <summary>The version every JSON artifact is written with. Bump it when a record changes shape
    /// in a way an older reader would misread.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The largest artifact read back. Nothing the pipeline writes comes near it; a file
    /// that does has been replaced by something else.</summary>
    public const long MaxArtifactBytes = 16 * 1024 * 1024;

    private const int MoveAttempts = 5;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Win32 treats these as devices wherever they appear as a file's base name: "con.json" is the
    // console, not a file, even with an extension.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // One gate per file, so two writers of the same artifact take turns rather than racing to replace it.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeGates = new(StringComparer.OrdinalIgnoreCase);

    public PipelineArtifactStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>
    /// <paramref name="path"/> with forward slashes and no leading "./", or null when it is not a plain
    /// relative path: empty, rooted, with a "." or ".." segment, a colon (a drive, or an alternate data
    /// stream), a segment ending in a dot or a space (Windows drops those, so two spellings would name
    /// one file), or a device name. Paths come from the runner and from tool arguments a model wrote,
    /// so this is the gate for both.
    /// </summary>
    public static string? NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains(':'))
        {
            return null;
        }

        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.EndsWith('/'))
        {
            return null;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || segment.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0
                || ReservedDeviceNames.Contains(segment.Split('.')[0].TrimEnd()))
            {
                return null;
            }
        }

        return normalized;
    }

    /// <summary>The full path of <paramref name="relativePath"/> inside the run folder, or null when it
    /// is not a safe path there (see <see cref="NormalizeRelativePath"/>).</summary>
    public string? Resolve(string? relativePath)
    {
        if (NormalizeRelativePath(relativePath) is not { } normalized)
        {
            return null;
        }

        var full = WorkspaceFileReader.ResolveWithinRoot(RootDirectory, normalized);
        return full is null || string.Equals(full, RootDirectory, StringComparison.OrdinalIgnoreCase) ? null : full;
    }

    public bool Exists(string relativePath) => Resolve(relativePath) is { } full && File.Exists(full);

    /// <summary>Writes <paramref name="value"/> as <c>{ "schemaVersion": n, "data": ... }</c>.</summary>
    /// <exception cref="ArgumentException">The path is not a safe relative path - a bug in the caller,
    /// since nothing the runner writes is named by a model.</exception>
    public Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken = default) =>
        WriteTextAsync(
            relativePath,
            JsonSerializer.Serialize(new Envelope<T>(CurrentSchemaVersion, value), PipelineJson.Options),
            cancellationToken);

    public async Task WriteTextAsync(string relativePath, string text, CancellationToken cancellationToken = default)
    {
        var full = Resolve(relativePath)
                   ?? throw new ArgumentException($"'{relativePath}' is not a safe path inside the run folder.", nameof(relativePath));
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);

        var gate = _writeGates.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, Utf8, cancellationToken);
            await MoveOverAsync(temporary, full, cancellationToken);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with the finished temporary file. Windows can refuse that for
    /// a moment - a virus scanner or the search indexer has the file open - so a refusal is retried a few
    /// times before it is allowed to fail the write.
    /// </summary>
    private static async Task MoveOverAsync(string temporary, string target, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), cancellationToken);
            }
        }
    }

    public async Task<ArtifactReadResult<T>> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken = default)
    {
        var read = await ReadTextCoreAsync(relativePath, cancellationToken);
        if (read.Error is not null)
        {
            return new ArtifactReadResult<T>(default, read.Error);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(read.Text!, PipelineJson.Options);
            if (envelope is null)
            {
                return new ArtifactReadResult<T>(default, $"'{relativePath}' is empty.");
            }

            if (envelope.SchemaVersion > CurrentSchemaVersion)
            {
                return new ArtifactReadResult<T>(
                    default,
                    $"'{relativePath}' has schema version {envelope.SchemaVersion}, newer than this version of the app "
                    + $"understands ({CurrentSchemaVersion}).");
            }

            return envelope.Data is null
                ? new ArtifactReadResult<T>(default, $"'{relativePath}' has no data.")
                : new ArtifactReadResult<T>(envelope.Data, null);
        }
        catch (JsonException ex)
        {
            return new ArtifactReadResult<T>(default, $"'{relativePath}' is not valid: {ex.Message}");
        }
    }

    /// <summary>The file's text, or null when it does not exist, is not a safe path, or is too large.</summary>
    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default) =>
        (await ReadTextCoreAsync(relativePath, cancellationToken)).Text;

    /// <summary>
    /// The artifacts under <paramref name="relativeDirectory"/> (the whole folder when empty) as
    /// relative paths with forward slashes, oldest name first. Leftover temporary files from a write
    /// that was cut short are not artifacts and are left out.
    /// </summary>
    public IReadOnlyList<string> List(string relativeDirectory = "")
    {
        var start = RootDirectory;
        if (relativeDirectory.Length > 0)
        {
            if (Resolve(relativeDirectory) is not { } directory)
            {
                return [];
            }

            start = directory;
        }

        if (!Directory.Exists(start))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
                .Where(file => !Path.GetFileName(file).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                               || !Path.GetFileName(file).StartsWith('.'))
                .Select(file => Path.GetRelativePath(RootDirectory, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal),
        ];
    }

    private async Task<(string? Text, string? Error)> ReadTextCoreAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (Resolve(relativePath) is not { } full)
        {
            return (null, $"'{relativePath}' is not a safe path inside the run folder.");
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return (null, $"'{relativePath}' does not exist.");
            }

            return info.Length > MaxArtifactBytes
                ? (null, $"'{relativePath}' is larger than {MaxArtifactBytes / (1024 * 1024)} MB.")
                : (await ReadSharedAsync(full, cancellationToken), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"'{relativePath}' could not be read: {ex.Message}");
        }
    }

    /// <summary>Reads without blocking a writer: with FileShare.Delete a replace of the file goes ahead
    /// while it is open here, and this reader keeps the version it started on.</summary>
    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        using var reader = new StreamReader(stream, Utf8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless and is filtered out of List.
        }
    }

    private sealed record Envelope<T>(int SchemaVersion, T? Data);
}
