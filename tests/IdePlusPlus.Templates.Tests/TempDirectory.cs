namespace IdePlusPlus.Templates.Tests;

/// <summary>A throwaway directory tree on disk - the scanner and locator read real files, so the
/// fixtures have to be real files too.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ideplusplus-templates-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string WriteFile(string relativePath, string content = "")
    {
        var fullPath = PathFor(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public string CreateDirectory(string relativePath)
    {
        var fullPath = PathFor(relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

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
