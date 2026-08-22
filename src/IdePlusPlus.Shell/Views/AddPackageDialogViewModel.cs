using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.NuGet;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// Backs AddPackageDialog: search nuget.org, pick a result, optionally type an exact version. The
/// owning Window reads SelectedResult/SelectedVersion directly off this DataContext when its own
/// Install button is clicked and sets its own DialogResult - no extra confirmation event needed.
/// </summary>
public sealed partial class AddPackageDialogViewModel : ObservableObject
{
    private readonly NuGetSearchClient _nuGetSearchClient;

    /// <summary>Cancels any prior in-flight search so an old slow search's results can't clobber a
    /// newer search's results if it happens to complete later.</summary>
    private CancellationTokenSource? _searchCts;

    public AddPackageDialogViewModel(NuGetSearchClient nuGetSearchClient)
    {
        _nuGetSearchClient = nuGetSearchClient;
    }

    public ObservableCollection<NuGetSearchResult> Results { get; } = [];

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private bool isSearching;

    [ObservableProperty]
    private NuGetSearchResult? selectedResult;

    /// <summary>Blank means "let dotnet pick the latest version".</summary>
    [ObservableProperty]
    private string selectedVersion = "";

    [ObservableProperty]
    private string? statusMessage;

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        IsSearching = true;
        StatusMessage = null;
        try
        {
            var (results, error) = await _nuGetSearchClient.SearchAsync(SearchText.Trim(), cts.Token);
            if (!ReferenceEquals(_searchCts, cts))
            {
                return; // superseded by a newer search
            }

            Results.Clear();
            foreach (var result in results)
            {
                Results.Add(result);
            }

            StatusMessage = error;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
            }
        }
    }
}
