using System.IO;
using System.Text;

namespace IdePlusPlus.Editor;

/// <summary>
/// Writes a file so a reader (or a crash) sees either the old content or the new content, never a
/// half-written file.
///
/// File.WriteAllText truncates the target and then writes into it, so a power cut, a killed process
/// or a full disk part-way through leaves the user's source file empty or cut short - and the editor
/// has already marked it saved. Here the new text goes to a temp file beside the target (same
/// directory, so the final swap stays on one volume), is flushed through to the disk, and only then
/// replaces the target in a single operation. If anything fails before the swap the original has not
/// been touched.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Same encoding as File.WriteAllText (UTF-8, no BOM), so switching to this does not
    /// change a single byte of what gets written.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);

        // A symlink target must be written through: replacing it would swap the link itself for a
        // regular file and leave the real file it pointed at untouched.
        if (File.Exists(fullPath) && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            File.WriteAllText(fullPath, contents, Utf8NoBom);
            return;
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Utf8NoBom.GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                // Keeps the target's attributes and permissions, which a delete-and-rename would not.
                File.Replace(tempPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception)
            {
                // The write's own failure is what the caller needs to see, not a leftover temp file.
            }

            throw;
        }
    }
}
