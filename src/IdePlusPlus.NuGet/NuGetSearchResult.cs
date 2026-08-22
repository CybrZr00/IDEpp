namespace IdePlusPlus.NuGet;

/// <summary>One row from nuget.org's public v3 search endpoint.</summary>
public sealed record NuGetSearchResult(string Id, string LatestVersion, long TotalDownloads, string Description);
