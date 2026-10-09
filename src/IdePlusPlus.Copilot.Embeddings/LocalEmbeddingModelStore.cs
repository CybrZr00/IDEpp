using System.Security.Cryptography;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Where the built-in embedding model lives on disk, and how it gets there. The model is
/// bge-small-en-v1.5 (BAAI, MIT licence), int8-quantised ONNX export, 384 dimensions. It is not part
/// of the repo or the installer: it is downloaded once, on the first index run, into
/// %LOCALAPPDATA%\IdePlusPlus\models. Both files are pinned to a Hugging Face commit and checked
/// against a SHA-256, so a changed or truncated download is rejected instead of being loaded.
/// </summary>
public sealed class LocalEmbeddingModelStore
{
    public const string ModelName = "bge-small-en-v1.5-int8";

    private const string Revision = "ea104dacec62c0de699686887e3f920caeb4f3e3";
    private const string BaseUrl = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/" + Revision;

    private static readonly ModelFile[] Files =
    [
        new("model.onnx", $"{BaseUrl}/onnx/model_quantized.onnx", "6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4"),
        new("vocab.txt", $"{BaseUrl}/vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
    ];

    private readonly string _directory;

    public LocalEmbeddingModelStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdePlusPlus", "models", ModelName);
    }

    public string ModelPath => Path.Combine(_directory, "model.onnx");

    public string VocabPath => Path.Combine(_directory, "vocab.txt");

    public bool IsInstalled => Files.All(f => File.Exists(Path.Combine(_directory, f.Name)));

    /// <summary>Downloads whatever is missing. Each file goes to a .part file first and is only moved
    /// into place once its hash matches, so a cancelled or failed download leaves nothing that
    /// <see cref="IsInstalled"/> would mistake for a model.</summary>
    public async Task DownloadAsync(HttpClient httpClient, Action<string> onProgress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);

        foreach (var file in Files)
        {
            var target = Path.Combine(_directory, file.Name);
            if (File.Exists(target))
            {
                continue;
            }

            var partial = target + ".part";
            try
            {
                using var response = await httpClient.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;

                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = File.Create(partial))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var lastReport = 0L;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        received += read;
                        if (total is > 0 && received - lastReport >= 1_000_000)
                        {
                            lastReport = received;
                            onProgress($"Downloading embedding model {file.Name}: {received / 1_000_000} of {total / 1_000_000} MB...");
                        }
                    }
                }

                await using (var check = File.OpenRead(partial))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken));
                    if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"{file.Name} did not match its expected checksum.");
                    }
                }

                File.Move(partial, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
        }
    }

    private sealed record ModelFile(string Name, string Url, string Sha256);
}
