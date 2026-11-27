using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Serilog;

namespace IdePlusPlus.Shell;

/// <summary>
/// The small OS-facing helpers behind the Solution Explorer's "Copy Full Path" and "Open in File
/// Explorer" items.
///
/// <para>Both return a bool instead of throwing: they are invoked straight from a context-menu click
/// on the UI thread, where a clipboard another process is holding open, or a path that was deleted
/// since the tree was built, must show up as an Output-pane line rather than as an unhandled
/// exception that takes the IDE down.</para>
/// </summary>
public static class ShellIntegration
{
    private const int ClipboardAttempts = 3;

    /// <summary>Short enough to be unnoticeable when the clipboard frees up on the second try,
    /// long enough that a clipboard manager that holds it for a few milliseconds normally does.</summary>
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Starts a process for <see cref="TryRevealInExplorer"/> and reports whether it started. A seam
    /// so tests can assert the exact explorer.exe argument list without spawning a window (Shell's
    /// csproj has InternalsVisibleTo for the test assembly).
    /// </summary>
    internal static Func<ProcessStartInfo, bool> ProcessStarter { get; set; } = static startInfo =>
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    };

    /// <summary>
    /// Copies <paramref name="text"/> to the clipboard. Retried a few times because the clipboard is
    /// a shared, single-owner Win32 resource: another process (WPF's own clipboard handling, Office, a
    /// clipboard manager) routinely holds it open for a few milliseconds, which surfaces as
    /// COMException CLIPBRD_E_CANT_OPEN rather than as "wait a moment".
    /// </summary>
    /// <returns>True when the text was placed on the clipboard; false (never an exception) otherwise.</returns>
    public static bool TryCopyToClipboard(string text)
    {
        for (var attempt = 1; attempt <= ClipboardAttempts; attempt++)
        {
            try
            {
                // copy: true flushes the data into the clipboard on return, so the text outlives this
                // process - without it the clipboard can end up holding a dead reference.
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException ex)
            {
                if (attempt == ClipboardAttempts)
                {
                    Log.Warning(ex, "Could not copy to the clipboard - another process kept it open for {Attempts} attempts", ClipboardAttempts);
                    return false;
                }

                Thread.Sleep(ClipboardRetryDelay);
            }
            catch (Exception ex)
            {
                // ExternalException/ThreadStateException are outside the COMException contract but
                // equally survivable; a clipboard failure must not reach the UI thread unhandled.
                Log.Warning(ex, "Could not copy to the clipboard");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Shows <paramref name="path"/> in Windows File Explorer: a file is revealed with its parent
    /// folder open and the file selected, a directory is simply opened.
    /// </summary>
    /// <returns>False when the path no longer exists, contains a quote (which cannot be passed
    /// through explorer.exe's command line without mangling it), or could not be launched.</returns>
    public static bool TryRevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // A quote would close the quoting explorer.exe /select relies on, turning the rest of the path
        // into extra arguments - refuse rather than spawn something with a mangled argument list.
        if (path.Contains('"'))
        {
            Log.Warning("Cannot open {Path} in File Explorer - it contains a quote", path);
            return false;
        }

        if (File.Exists(path))
        {
            return StartExplorer($"/select,{Quote(path)}", path);
        }

        if (Directory.Exists(path))
        {
            return StartExplorer(QuoteIfNeeded(path), path);
        }

        Log.Warning("Cannot open {Path} in File Explorer - it no longer exists", path);
        return false;
    }

    private static bool StartExplorer(string arguments, string path)
    {
        // UseShellExecute=false: explorer.exe is an executable in its own right, and this way a
        // launch failure surfaces as a Win32Exception we can report instead of a shell error dialog.
        var startInfo = new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false };

        try
        {
            return ProcessStarter(startInfo);
        }
        catch (Win32Exception ex)
        {
            Log.Warning(ex, "Could not start File Explorer for {Path}", path);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start File Explorer for {Path}", path);
            return false;
        }
    }

    private static string Quote(string path) => $"\"{path}\"";

    /// <summary>Quoted only when it needs to be, so a plain path reaches explorer.exe as one
    /// unadorned argument.</summary>
    private static string QuoteIfNeeded(string path) =>
        path.Any(char.IsWhiteSpace) ? Quote(path) : path;
}
