using System.Text;

namespace IdePlusPlus.Connections;

/// <summary>Masks the credential-bearing segments of an ADO.NET-style "Key=Value;Key=Value"
/// connection string - Password/Pwd/User ID/Uid, case-insensitively - so a discovered connection can
/// be shown in the pane, logged, or written to the Output pane without exposing a live secret. Every
/// display surface should go through this; the one deliberate exception is an explicit,
/// user-initiated "copy full connection string" action, which reads the raw value directly.</summary>
public static class ConnectionStringRedactor
{
    private static readonly string[] SensitiveKeys = ["password", "pwd", "user id", "uid"];

    public static string Redact(string connectionString)
    {
        var segments = connectionString.Split(';');
        var builder = new StringBuilder();

        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }

            var segment = segments[i];
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                builder.Append(segment);
                continue;
            }

            var key = segment[..separatorIndex];
            if (Array.IndexOf(SensitiveKeys, key.Trim().ToLowerInvariant()) >= 0)
            {
                builder.Append(key).Append("=***");
            }
            else
            {
                builder.Append(segment);
            }
        }

        return builder.ToString();
    }
}
