using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdePlusPlus.NuGet;

/// <summary>Queries nuget.org's public v3 search endpoint through a caller-supplied, shared
/// HttpClient (this app has exactly one shared singleton HttpClient registered in DI - this class
/// never creates its own).</summary>
public sealed class NuGetSearchClient
{
    private const string SearchUrl = "https://azuresearch-usnc.nuget.org/query";
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;

    public NuGetSearchClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<(IReadOnlyList<NuGetSearchResult> Results, string? Error)> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(SearchTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}&take=25";

        try
        {
            using var response = await _httpClient.GetAsync(url, linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return ([], $"NuGet search returned {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(linkedCts.Token);
            var root = JsonNode.Parse(body);
            var data = root?["data"] as JsonArray;
            if (data is null)
            {
                return ([], null);
            }

            var results = new List<NuGetSearchResult>();
            foreach (var item in data)
            {
                if (item is null)
                {
                    continue;
                }

                results.Add(new NuGetSearchResult(
                    Id: item["id"]?.GetValue<string>() ?? string.Empty,
                    LatestVersion: item["version"]?.GetValue<string>() ?? string.Empty,
                    TotalDownloads: item["totalDownloads"]?.GetValue<long>() ?? 0,
                    Description: item["description"]?.GetValue<string>() ?? string.Empty));
            }

            return (results, null);
        }
        catch (HttpRequestException ex)
        {
            return ([], $"Request failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return ([], "NuGet search timed out or was cancelled.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return ([], $"Unexpected response from NuGet: {ex.Message}");
        }
    }
}
