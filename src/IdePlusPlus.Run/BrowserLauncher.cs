using System.Diagnostics;

namespace IdePlusPlus.Run;

public static class BrowserLauncher
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort - failing to open a browser shouldn't be treated as a Run/Debug failure.
        }
    }
}
