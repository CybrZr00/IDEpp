using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Templates;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// Picks a project template and creates it - into the open solution when there is one, alongside a
/// new solution when there isn't.
///
/// <para>Creating happens here rather than being handed back to the caller, matching
/// <c>PublishDialogViewModel</c>: the work streams into the Output pane while the dialog is still
/// up, so there is nothing useful for a caller to do with a "result" except run it. What the caller
/// reads afterwards is only what it needs to reload and select.</para>
/// </summary>
public sealed partial class NewProjectDialogViewModel : ObservableObject
{
    private readonly TemplateCatalog _catalog;
    private readonly TemplateInstantiationService _instantiation;
    private readonly Action<string> _onOutputLine;

    private IReadOnlyList<TemplateGroup> _allTemplates = [];

    /// <summary>Stops the default name from overwriting one the user typed. Set by any change that
    /// did not come from <see cref="ApplyDefaultName"/>.</summary>
    private bool _nameEditedByUser;

    private bool _settingDefaultName;

    /// <param name="solutionPath">The open solution, or null - which is what decides whether this
    /// creates a solution as well as a project.</param>
    public NewProjectDialogViewModel(
        TemplateCatalog catalog,
        TemplateInstantiationService instantiation,
        string? solutionPath,
        Action<string> onOutputLine)
    {
        _catalog = catalog;
        _instantiation = instantiation;
        _onOutputLine = onOutputLine;
        SolutionPath = solutionPath;

        Location = solutionPath is not null
            ? Path.GetDirectoryName(solutionPath) ?? DefaultRepositoryRoot
            : DefaultRepositoryRoot;
    }

    /// <summary>Where Visual Studio puts new work, and so where someone will expect to find it.</summary>
    private static string DefaultRepositoryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");

    public string? SolutionPath { get; }

    public bool HasOpenSolution => SolutionPath is not null;

    public ObservableCollection<TemplateGroup> Templates { get; } = [];

    public ObservableCollection<string> Languages { get; } = [];

    public ObservableCollection<string> Frameworks { get; } = [];

    /// <summary>The template's own parameters, rendered from the cache - see
    /// <see cref="TemplateParameterViewModel"/>. Rebuilt whenever the template or language
    /// changes.</summary>
    public ObservableCollection<TemplateParameterViewModel> Parameters { get; } = [];

    [ObservableProperty]
    private TemplateGroup? selectedTemplate;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string? selectedLanguage;

    [ObservableProperty]
    private string? selectedFramework;

    [ObservableProperty]
    private string projectName = string.Empty;

    [ObservableProperty]
    private string location = string.Empty;

    /// <summary>Empty means the solution root. Left unset the CLI would derive a folder from the
    /// project's path on disk, which is rarely what was meant - see TemplateCreationArguments.</summary>
    [ObservableProperty]
    private string solutionFolder = string.Empty;

    /// <summary>Only meaningful with no solution open, where a project has to go somewhere.</summary>
    [ObservableProperty]
    private string solutionName = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "Loading templates...";

    /// <summary>The created project's file, once one exists. Null while nothing has been created.</summary>
    public string? ResultProjectPath { get; private set; }

    /// <summary>The solution the caller should open or reload afterwards - the one that was already
    /// open, or the one just created.</summary>
    public string? ResultSolutionPath { get; private set; }

    public bool Created => ResultProjectPath is not null || ResultSolutionPath is not null;

    /// <summary>
    /// A solution-kind template (aspire-starter, maui-multiproject) brings its own solution file and
    /// several projects, so it can never be added to the open one - it replaces it. The dialog says
    /// so before anything runs rather than after.
    /// </summary>
    public bool CreatesItsOwnSolution => SelectedTemplate?.Kind == TemplateKind.Solution;

    public bool NeedsSolutionName => !HasOpenSolution || CreatesItsOwnSolution;

    public string? SolutionNotice => (HasOpenSolution, CreatesItsOwnSolution) switch
    {
        (true, true) =>
            "This template creates its own solution with several projects in it, so it can't be added " +
            "to the open solution. The new solution will be opened instead.",
        (true, false) => null,
        (false, _) => "No solution is open, so a new one will be created alongside the project.",
    };

    /// <summary>Null when everything needed is present and valid - which is also what enables Create.</summary>
    public string? ValidationMessage
    {
        get
        {
            if (SelectedTemplate is null)
            {
                return "Choose a template.";
            }

            if (TemplateNameValidator.ValidateProjectName(ProjectName) is { } nameError)
            {
                return nameError;
            }

            if (string.IsNullOrWhiteSpace(Location))
            {
                return "Choose a location.";
            }

            if (NeedsSolutionName && TemplateNameValidator.ValidateProjectName(SolutionName) is { } solutionError)
            {
                return solutionError.Replace("project name", "solution name");
            }

            if (Directory.Exists(TargetDirectory) && Directory.EnumerateFileSystemEntries(TargetDirectory).Any())
            {
                // dotnet new refuses to overwrite with exit code 73 and a prose message rather than a
                // hard error, so catching it here keeps the complaint next to the field that caused it.
                return $"'{TargetDirectory}' already exists and isn't empty.";
            }

            return TemplateParameters.AllRequiredAnswered(Parameters.Select(p => p.ToValue()))
                ? null
                : "Fill in the required options.";
        }
    }

    public bool CanCreate => !IsBusy && ValidationMessage is null;

    /// <summary>
    /// Where the template's own output goes. With no solution open the project is nested inside the
    /// new solution's directory, which is the layout `dotnet new sln` + `dotnet solution add`
    /// produces and what Visual Studio does too.
    /// </summary>
    public string TargetDirectory => NeedsSolutionName
        ? Path.Combine(Location, SolutionName, CreatesItsOwnSolution ? string.Empty : ProjectName)
        : Path.Combine(Location, ProjectName);

    public async Task LoadAsync()
    {
        var contextDirectory = SolutionPath is not null ? Path.GetDirectoryName(SolutionPath) : null;

        var projects = await _catalog.GetGroupsAsync(TemplateKind.Project, contextDirectory);
        var solutions = await _catalog.GetGroupsAsync(TemplateKind.Solution, contextDirectory);

        _allTemplates = projects.Concat(solutions)
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ApplyFilter();

        StatusMessage = _allTemplates.Count > 0
            ? $"{_allTemplates.Count} templates installed."
            : "No templates found. Check that the .NET SDK is installed and on PATH.";

        SelectedTemplate ??= Templates.FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedTemplateChanged(TemplateGroup? value)
    {
        Languages.Clear();
        foreach (var language in value?.Languages ?? [])
        {
            Languages.Add(language);
        }

        SelectedLanguage = Languages.FirstOrDefault();

        // Follows the template until the user types a name of their own - picking Class Library
        // after looking at Console App should not leave "ConsoleApp1" in the box.
        if (value is not null && !_nameEditedByUser)
        {
            ApplyDefaultName(value.Primary.DefaultName ?? value.Name.Replace(" ", string.Empty));
        }

        NotifyTargetChanged();
        OnPropertyChanged(nameof(CreatesItsOwnSolution));
        OnPropertyChanged(nameof(NeedsSolutionName));
        OnPropertyChanged(nameof(SolutionNotice));
    }

    partial void OnSelectedLanguageChanged(string? value)
    {
        Frameworks.Clear();
        foreach (var framework in SelectedTemplate?.FrameworkChoicesFor(value) ?? [])
        {
            Frameworks.Add(framework);
        }

        // The first entry is the highest-precedence band's default, which is what plain
        // `dotnet new <template>` would pick.
        SelectedFramework = Frameworks.FirstOrDefault();

        RebuildParameters();
    }

    /// <summary>Parameters belong to the resolved template entry, not the group - a different band or
    /// language can declare different ones, so this follows the framework as well as the
    /// template.</summary>
    private void RebuildParameters()
    {
        foreach (var existing in Parameters)
        {
            existing.Changed -= NotifyTargetChanged;
        }

        Parameters.Clear();

        if (SelectedTemplate?.Resolve(SelectedLanguage, SelectedFramework) is not { } template)
        {
            return;
        }

        foreach (var parameter in TemplateParameters.UserEditable(template))
        {
            var viewModel = new TemplateParameterViewModel(parameter);
            viewModel.Changed += NotifyTargetChanged;
            Parameters.Add(viewModel);
        }
    }

    partial void OnSelectedFrameworkChanged(string? value) => RebuildParameters();

    partial void OnProjectNameChanged(string value)
    {
        if (!_settingDefaultName)
        {
            _nameEditedByUser = true;
        }

        if (NeedsSolutionName && string.IsNullOrWhiteSpace(SolutionName))
        {
            SolutionName = value;
        }

        NotifyTargetChanged();
    }

    partial void OnLocationChanged(string value) => NotifyTargetChanged();

    partial void OnSolutionNameChanged(string value) => NotifyTargetChanged();

    partial void OnIsBusyChanged(bool value) => CreateCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (SelectedTemplate?.Resolve(SelectedLanguage, SelectedFramework) is not { } template)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = $"Creating {ProjectName}...";

            var solutionPath = NeedsSolutionName
                ? await CreateSolutionAsync()
                : SolutionPath;

            if (NeedsSolutionName && solutionPath is null)
            {
                StatusMessage = "Couldn't create the solution - see the Output pane.";
                return;
            }

            // A solution-kind template writes its own solution file, so there is nothing to create
            // beforehand and nothing to add afterwards.
            if (CreatesItsOwnSolution)
            {
                await CreateSolutionTemplateAsync(template);
                return;
            }

            await CreateProjectAsync(template, solutionPath!);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateProjectAsync(TemplateInfo template, string solutionPath)
    {
        var request = BuildRequest(template, TargetDirectory);
        var projectPath = Path.Combine(TargetDirectory, $"{ProjectName}{ProjectExtensionFor(SelectedLanguage)}");

        var result = await _instantiation.CreateAndAddToSolutionAsync(
            request,
            solutionPath,
            string.IsNullOrWhiteSpace(SolutionFolder) ? null : SolutionFolder,
            projectPath,
            _onOutputLine);

        if (!result.Succeeded)
        {
            StatusMessage = DescribeFailure(result);
            return;
        }

        // The template names its own output, and for a multi-project template it may not be the one
        // predicted above - so the created file wins where there is one.
        ResultProjectPath = result.CreatedFiles.FirstOrDefault(IsProjectFile) ?? projectPath;
        ResultSolutionPath = solutionPath;
        StatusMessage = $"Created {Path.GetFileName(ResultProjectPath)}.";
    }

    private async Task CreateSolutionTemplateAsync(TemplateInfo template)
    {
        var directory = Path.Combine(Location, SolutionName);
        var result = await _instantiation.CreateAsync(BuildRequest(template, directory), _onOutputLine);

        if (!result.Succeeded)
        {
            StatusMessage = DescribeFailure(result);
            return;
        }

        ResultSolutionPath = result.CreatedFiles.FirstOrDefault(IsSolutionFile);
        ResultProjectPath = result.CreatedFiles.FirstOrDefault(IsProjectFile);
        StatusMessage = ResultSolutionPath is not null
            ? $"Created {Path.GetFileName(ResultSolutionPath)}."
            : "The template ran, but didn't produce a solution file.";
    }

    /// <summary>Creates the containing solution. `dotnet new sln` defaults to .slnx on the .NET 10
    /// SDK, which SolutionLoader already reads.</summary>
    private async Task<string?> CreateSolutionAsync()
    {
        var directory = Path.Combine(Location, SolutionName);
        var result = await _instantiation.CreateAsync(
            new TemplateCreationRequest("sln", SolutionName, directory), _onOutputLine);

        return result.Succeeded ? result.CreatedFiles.FirstOrDefault(IsSolutionFile) : null;
    }

    private TemplateCreationRequest BuildRequest(TemplateInfo template, string outputDirectory) =>
        new(
            template.ShortName,
            CreatesItsOwnSolution ? SolutionName : ProjectName,
            outputDirectory,
            Languages.Count > 1 ? SelectedLanguage : null,
            template.FrameworkChoices.Count > 0 ? SelectedFramework : null,
            Parameters: TemplateParameters.ToArguments(Parameters.Select(p => p.ToValue())));

    private static string DescribeFailure(TemplateCreationResult result) =>
        result.WouldOverwriteExistingFiles
            ? "That would overwrite files that already exist. Choose a different name or location."
            : "Creation failed - see the Output pane.";

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj";

    private static bool IsSolutionFile(string path) => Path.GetExtension(path) is ".slnx" or ".sln";

    private static string ProjectExtensionFor(string? language) => language switch
    {
        "F#" => ".fsproj",
        "VB" => ".vbproj",
        _ => ".csproj",
    };

    private void ApplyFilter()
    {
        Templates.Clear();
        foreach (var group in TemplateGrouping.Search(_allTemplates, SearchText))
        {
            Templates.Add(group);
        }

        // Not guarded on SelectedTemplate being non-null: clearing the collection above drives the
        // ListBox's SelectedItem to null through the two-way binding, so by this point it always is.
        // Without this, typing in the search box leaves nothing selected and the whole right-hand
        // side blank until something is clicked.
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
            ProjectName = name;
        }
        finally
        {
            _settingDefaultName = false;
        }
    }

    private void NotifyTargetChanged()
    {
        OnPropertyChanged(nameof(TargetDirectory));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();
    }
}
