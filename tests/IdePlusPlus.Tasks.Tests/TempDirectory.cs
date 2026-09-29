namespace IdePlusPlus.Tasks.Tests;

/// <summary>A throwaway directory tree on disk - the discovery/detection code under test reads real
/// files, so the fixtures have to be real files too.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ideplusplus-tasks-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes a file at a path relative to this directory, creating parent directories.</summary>
    public string WriteFile(string relativePath, string content = "")
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public string WritePackageJson(string json) => WriteFile("package.json", json);

    /// <summary>A path inside this directory for a file that does not exist yet.</summary>
    public string PathFor(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing an otherwise-passing test over.
        }
    }
}
