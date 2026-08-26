using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace IdePlusPlus.Infrastructure;

public sealed record WebFetchResult(bool Success, string Text);

/// <summary>
/// Fetches one public webpage and returns its readable text, for the fetch_url tool.
///
/// Every hop of the request - the first request and every redirect it follows - resolves its own
/// host, rejects the request outright if <see cref="WebHostGuard"/> blocks any resolved address,
/// and then connects to that exact validated address via a per-request SocketsHttpHandler.ConnectCallback
/// rather than letting the handler re-resolve the hostname at connect time. That second point is
/// what closes DNS rebinding: a naive "resolve, check, then let HttpClient connect normally" leaves
/// a window where the second (real) resolution can legally return a different, unblocked address.
/// TLS validation still checks the certificate against the original hostname - ConnectCallback only
/// replaces how the transport connects, not the hostname used for SNI/certificate checks.
///
/// Automatic redirects are disabled for the same reason: a public URL redirecting to a private one
/// is the same rebinding shape, one HTTP hop later.
/// </summary>
public sealed class WebContentFetcher
{
    public const int MaxOutputChars = 20_000;

    private const int MaxRedirects = 5;
    private const int MaxDownloadBytes = 5_000_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var current = url;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return new WebFetchResult(false, $"'{current}' is not a valid http:// or https:// URL.");
            }

            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return new WebFetchResult(false, $"Could not resolve host '{uri.Host}': {ex.Message}");
            }

            if (addresses.Length == 0)
            {
                return new WebFetchResult(false, $"Host '{uri.Host}' did not resolve to any address.");
            }

            if (Array.Exists(addresses, WebHostGuard.IsBlocked))
            {
                return new WebFetchResult(
                    false,
                    $"Refused: '{uri.Host}' resolves to a local, private-network, or link-local address. "
                    + "fetch_url never accesses these, regardless of confirmation.");
            }

            var pinnedAddress = addresses[0];

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = RequestTimeout,
                ConnectCallback = async (context, ct) =>
                {
                    var socket = new Socket(pinnedAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(pinnedAddress, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };

            using var client = new HttpClient(handler) { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IdePlusPlus-Copilot/1.0 (+fetch_url tool)");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            HttpResponseMessage response;
            try
            {
                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
            {
                return new WebFetchResult(false, $"Could not fetch '{current}': {ex.Message}");
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        return new WebFetchResult(false, $"'{current}' redirected without a Location header.");
                    }

                    current = (location.IsAbsoluteUri ? location : new Uri(uri, location)).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new WebFetchResult(false, $"'{current}' returned HTTP {(int)response.StatusCode} {response.StatusCode}.");
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var bytes = await ReadCappedAsync(stream, MaxDownloadBytes, cancellationToken).ConfigureAwait(false);
                var raw = Encoding.UTF8.GetString(bytes);

                var text = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    ? HtmlTextExtractor.ExtractReadableText(raw)
                    : raw;

                return new WebFetchResult(true, Cap(text.Trim()));
            }
        }

        return new WebFetchResult(false, $"'{url}' redirected more than {MaxRedirects} times.");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Stops reading as soon as the cap is hit instead of buffering the whole body first -
    /// a multi-gigabyte response should cost a bounded amount of memory, not an unbounded one.</summary>
    private static async Task<byte[]> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        int read;
        while (buffer.Length < maxBytes
               && (read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var remaining = (int)(maxBytes - buffer.Length);
            buffer.Write(chunk, 0, Math.Min(read, remaining));
        }

        return buffer.ToArray();
    }

    private static string Cap(string text) =>
        text.Length <= MaxOutputChars
            ? text
            : text[..MaxOutputChars]
              + $"\n\n[truncated - the page is {text.Length} chars, showing the first {MaxOutputChars}.]";
}
