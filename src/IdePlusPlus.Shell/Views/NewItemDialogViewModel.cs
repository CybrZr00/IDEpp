using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Templates;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// Where a new item is going: the project that will own it, and the directory it lands in. Resolved
/// by the shell before the dialog opens - see MainWindowViewModel.ResolveItemTarget.
/// </summary>
public sealed record NewItemTarget(string ProjectPath, string ProjectDirectory, string RootNamespace, string Directory)
{
    public string ProjectName => Path.GetFileNameWithoutExtension(ProjectPath);
}

/// <summary>
/// Adds one file from an item template. The namespace shown is the one the file will actually get -
/// computed here rather than left to <c>dotnet new</c>, which would give the project's root
/// namespace regardless of folder (see <see cref="NamespaceResolver"/>).
/// </summary>
public sealed partial class NewItemDialogViewModel : ObservableObject
{
    private readonly TemplateCatalog _catalog;
    private readonly TemplateInstantiationService _instantiation;
    private readonly Action<string> _onOutputLine;

    private IReadOnlyList<TemplateGroup> _allTemplates = [];
    private bool _nameEditedByUser;
    private bool _settingDefaultName;

    public NewItemDialogViewModel(
        TemplateCatalog catalog,
        TemplateInstantiationService instantiation,
        NewItemTarget target,
        Action<string> onOutputLine,
        string? preselectShortName = null)
    {
        _catalog = catalog;
        _instantiation = instantiation;
        _onOutputLine = onOutputLine;
        Target = target;
        PreselectShortName = preselectShortName;
        directory = target.Directory;
    }

    public NewItemTarget Target { get; }

    /// <summary>Set when the dialog was opened from a File &gt; New shortlist entry, which picks the
    /// template for the user - all that's left is the name.</summary>
    public string? PreselectShortName { get; }

    public ObservableCollection<TemplateGroup> Templates { get; } = [];

    public ObservableCollection<string> Languages { get; } = [];

    [ObservableProperty]
    private TemplateGroup? selectedTemplate;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string? selectedLanguage;

    [ObservableProperty]
    private string itemName = string.Empty;

    [ObservableProperty]
    private string directory;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "Loading templates...";

    /// <summary>The files that were created, once something has been. The caller opens these and
    /// adds them to the tree.</summary>
    public IReadOnlyList<string> ResultFiles { get; private set; } = [];

    public bool Created => ResultFiles.Count > 0;

    public string TargetSummary => $"{Target.ProjectName} — {Directory}";

    /// <summary>What the file will actually contain, shown so the folder-derived part is visible
    /// before anything is created rather than discovered afterwards.</summary>
    public string ResolvedNamespace =>
        NamespaceResolver.Resolve(Target.RootNamespace, Target.ProjectDirectory, Directory);

    public string? ValidationMessage
    {
        get
        {
            if (SelectedTemplate is null)
            {
                return "Choose a template.";
            }

            // A Class name becomes a type name and has to be an identifier; an .editorconfig name
            // only has to be a usable file name.
            var nameError = SelectedTemplate.Primary.NameBecomesTypeName
                ? TemplateNameValidator.ValidateTypeName(ItemName)
                : TemplateNameValidator.ValidateFileName(ItemName);

            if (nameError is not null)
            {
                return nameError;
            }

            return string.IsNullOrWhiteSpace(Directory) ? "Choose a folder." : null;
        }
    }

    public bool CanCreate => !IsBusy && ValidationMessage is null;

    public async Task LoadAsync()
    {
        _allTemplates = await _catalog.GetGroupsAsync(TemplateKind.Item, Target.ProjectDirectory);
        ApplyFilter();

        if (PreselectShortName is { } shortName)
        {
            SelectedTemplate = _allTemplates.FirstOrDefault(group =>
                group.Templates.Any(t => t.ShortNames.Contains(shortName, StringComparer.OrdinalIgnoreCase)))
                ?? SelectedTemplate;
        }

        StatusMessage = _allTemplates.Count > 0
            ? $"{_allTemplates.Count} item templates installed."
            : "No item templates found. Check that the .NET SDK is installed and on PATH.";
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(ResolvedNamespace));
        OnPropertyChanged(nameof(TargetSummary));
        NotifyValidationChanged();
    }

    partial void OnSelectedTemplateChanged(TemplateGroup? value)
    {
        Languages.Clear();
        foreach (var language in value?.Languages ?? [])
        {
            Languages.Add(language);
        }

        SelectedLanguage = Languages.FirstOrDefault();

        if (value is not null && !_nameEditedByUser)
        {
            ApplyDefaultName(value.Primary.DefaultName ?? value.Name.Replace(" ", string.Empty));
        }

        NotifyValidationChanged();
    }

    partial void OnItemNameChanged(string value)
    {
        if (!_settingDefaultName)
        {
            _nameEditedByUser = true;
        }

        NotifyValidationChanged();
    }

    partial void OnIsBusyChanged(bool value) => CreateCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (SelectedTemplate?.Resolve(SelectedLanguage) is not { } template)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Creating {ItemName}...";

            var plan = ItemCreationPlan.For(
                template,
                ItemName,
                Directory,
                Target.ProjectDirectory,
                Target.RootNamespace,
                Languages.Count > 1 ? SelectedLanguage : null,
                Target.ProjectPath);

            var result = await _instantiation.CreateItemAsync(plan, _onOutputLine);

            if (!result.Succeeded)
            {
                StatusMessage = result.WouldOverwriteExistingFiles
                    ? "A file with that name already exists here. Choose a different name."
                    : "Creation failed - see the Output pane.";
                return;
            }

            ResultFiles = result.CreatedFiles;
            StatusMessage = $"Created {string.Join(", ", result.CreatedFiles.Select(Path.GetFileName))}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Templates.Clear();
        foreach (var group in TemplateGrouping.Search(_allTemplates, SearchText))
        {
            Templates.Add(group);
        }

        // Same reason as the New Project dialog: clearing the collection drives the ListBox's
        // SelectedItem to null through the binding, so this is never a no-op guard.
        if (SelectedTemplate is null || !Templates.Contains(SelectedTemplate))
        {
            SelectedTemplate = Templates.FirstOrDefault();
        }
    }

    private void ApplyDefaultName(string name)
    {
        _settingDefaultName = true;
        try
        {
            ItemName = name;
        }
        finally
        {
            _settingDefaultName = false;
        }
    }

    private void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();
    }
}
