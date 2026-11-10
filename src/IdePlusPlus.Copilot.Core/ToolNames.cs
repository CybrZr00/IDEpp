using System.Text;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Makes names safe to send as a chat-completions function name. DeepSeek (and OpenAI proper)
/// reject any name that does not match <c>^[a-zA-Z0-9_-]+$</c> with a 400 for the whole request;
/// OpenRouter is lenient, which is why the same tool list only fails on some providers. MCP server
/// names and tool names come from user config and third-party servers, so they routinely carry
/// dots, colons, spaces and the like.
/// </summary>
public static class ToolNames
{
    /// <summary>The longest function name OpenAI-style APIs accept.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Replaces every character outside <c>[a-zA-Z0-9_-]</c> with an underscore and caps the length at
    /// <see cref="MaxLength"/>. Deterministic, so the same input always maps to the same name and a
    /// model's tool call can be routed back by comparing against the sanitized form.
    /// </summary>
    public static string Sanitize(string name)
    {
        var builder = new StringBuilder(Math.Min(name.Length, MaxLength));
        foreach (var c in name)
        {
            if (builder.Length == MaxLength)
            {
                break;
            }

            builder.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }
}
