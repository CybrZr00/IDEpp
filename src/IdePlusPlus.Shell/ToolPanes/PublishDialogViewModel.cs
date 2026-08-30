using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Publish;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>Display labels for the tri-state deployment mode. SelfContained is deliberately nullable
/// on the model - an absent SelfContained isn't "framework-dependent", it's "whatever the project
/// says" - so the picker needs a third option rather than collapsing null onto false.</summary>
public static class DeploymentModeDisplay
{
    public const string ProjectDefault = "(Project default)";
    public const string FrameworkDependent = "Framework-dependent";
    public const string SelfContained = "Self-contained";

    public static readonly IReadOnlyList<string> All = [ProjectDefault, FrameworkDependent, SelfContained];

    public static string From(bool? selfContained) => selfContained switch
    {
        true => SelfContained,
        false => FrameworkDependent,
        null => ProjectDefault,
    };

    public static bool? To(string display) => display switch
    {
        SelfContained => true,
        FrameworkDependent => false,
        _ => null,
    };
}

/// <summary>
/// One publish profile being edited in the dialog.
///
/// Every editable field is flattened onto this view model rather than binding through to the record,
/// because a record's `with` semantics and WPF two-way binding don't mix - and because a profile the
/// IDE can't run still has to be displayed, which means the view model has to exist for shapes
/// <see cref="ToModel"/> would refuse to build.
/// </summary>
public sealed partial class PublishProfileEditorViewModel : ObservableObject
{
    private readonly PublishProfile _original;

    public PublishProfileEditorViewModel(PublishProfile profile, IReadOnlyList<string> runtimeIdentifiers)
    {
        _original = profile;
        AvailableRuntimeIdentifiers = runtimeIdentifiers;

        name = profile.Name;
        kind = profile.Kind;
        configuration = profile.Configuration;
        targetFramework = profile.TargetFramework ?? "";
        runtimeIdentifier = profile.RuntimeIdentifier ?? "";
        deploymentMode = DeploymentModeDisplay.From(profile.SelfContained);
        publishDir = profile.PublishDir ?? "";
        publishSingleFile = profile.PublishSingleFile;
        publishReadyToRun = profile.PublishReadyToRun;
        publishTrimmed = profile.PublishTrimmed;
        containerRepository = profile.ContainerRepository ?? "";
        containerImageTag = profile.ContainerImageTag ?? "";
        containerRegistry = profile.ContainerRegistry ?? "";
        containerBaseImage = profile.ContainerBaseImage ?? "";
    }

    /// <summary>Where the profile was read from, so a rename can find the old file to move. Null for
    /// a profile created in this dialog and not yet saved.</summary>
    public string? FilePath => _original.FilePath;

    /// <summary>Set by any edit, cleared by <see cref="MarkSaved"/>. A profile is written to disk only
    /// when this is true, so opening the dialog and closing it again never rewrites a file - which
    /// matters because rewriting is the one operation that can lose formatting.</summary>
    public bool IsDirty { get; private set; }

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderTarget))]
    [NotifyPropertyChangedFor(nameof(IsContainerTarget))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(UnsupportedMessage))]
    private PublishKind kind;

    [ObservableProperty]
    private string configuration;

    [ObservableProperty]
    private string targetFramework;

    [ObservableProperty]
    private string runtimeIdentifier;

    [ObservableProperty]
    private string deploymentMode;

    [ObservableProperty]
    private string publishDir;

    [ObservableProperty]
    private bool publishSingleFile;

    [ObservableProperty]
    private bool publishReadyToRun;

    [ObservableProperty]
    private bool publishTrimmed;

    [ObservableProperty]
    private string containerRepository;

    [ObservableProperty]
    private string containerImageTag;

    [ObservableProperty]
    private string containerRegistry;

    [ObservableProperty]
    private string containerBaseImage;

    /// <summary>Only the kinds IDE++ can run are offered - except for a profile that already is
    /// something else, where the list is just that, so the picker shows what the file actually says
    /// instead of appearing blank.</summary>
    public IReadOnlyList<PublishKind> AvailableKinds =>
        _original.Kind == PublishKind.Unsupported ? [PublishKind.Unsupported] : [PublishKind.Folder, PublishKind.Container];

    public IReadOnlyList<string> AvailableConfigurations { get; } = ["Debug", "Release"];

    public IReadOnlyList<string> AvailableRuntimeIdentifiers { get; }

    public IReadOnlyList<string> AvailableDeploymentModes => DeploymentModeDisplay.All;

    public bool IsEditable => Kind != PublishKind.Unsupported;

    public bool IsFolderTarget => Kind == PublishKind.Folder;

    public bool IsContainerTarget => Kind == PublishKind.Container;

    public string UnsupportedMessage =>
        IsEditable
            ? ""
            : "This profile publishes with a protocol IDE++ cannot run, so it is shown read-only. " +
              "Editing it here would discard the settings for that protocol - use Visual Studio for it instead.";

    public PublishProfile ToModel() => _original with
    {
        Name = Name.Trim(),
        Kind = Kind,
        Configuration = string.IsNullOrWhiteSpace(Configuration) ? "Release" : Configuration.Trim(),
        TargetFramework = Blank(TargetFramework),
        RuntimeIdentifier = Blank(RuntimeIdentifier),
        SelfContained = DeploymentModeDisplay.To(DeploymentMode),
        PublishDir = Blank(PublishDir),
        PublishSingleFile = PublishSingleFile,
        PublishReadyToRun = PublishReadyToRun,
        PublishTrimmed = PublishTrimmed,
        ContainerRepository = Blank(ContainerRepository),
        ContainerImageTag = Blank(ContainerImageTag),
        ContainerRegistry = Blank(ContainerRegistry),
        ContainerBaseImage = Blank(ContainerBaseImage),
    };

    public void MarkSaved() => IsDirty = false;

    /// <summary>Marks a profile created in this dialog as needing a write. The constructor assigns the
    /// backing fields directly rather than through the generated setters, so a brand new profile would
    /// otherwise look unchanged and never reach disk.</summary>
    public void MarkNewlyCreated() => IsDirty = true;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Every generated setter routes through here, so one hook covers the whole form rather than
        // a partial OnXChanged per field.
        if (e.PropertyName is not (nameof(IsDirty) or nameof(IsFolderTarget) or nameof(IsContainerTarget)
            or nameof(IsEditable) or nameof(UnsupportedMessage)))
        {
            IsDirty = true;
        }
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Backs the Publish dialog: list of the project's profiles on the left, the selected one's settings
/// on the right, and Save/Publish at the bottom.
///
/// Deliberately free of any WPF type so it is testable in IdePlusPlus.Shell.Tests, which only covers
/// view-model logic and never constructs an Application. Output goes out through the
/// <see cref="Action{T}"/> the caller supplies - MainWindowViewModel passes its existing
/// OnBuildOutputLine, which already marshals to the dispatcher and feeds the Error List, so publish
/// diagnostics land there for free.
///
/// Unlike the Startup Configuration Manager, this dialog writes on Save rather than on OK, and stays
/// open while publishing so its progress and the resulting output folder have somewhere to appear.
/// </summary>
public sealed partial class PublishDialogViewModel : ObservableObject
{
    /// <summary>Offered in the runtime-identifier picker, which is editable so anything else can still
    /// be typed. Enumerating the real RID graph would be both slow and far longer than is useful.</summary>
    private static readonly string[] CommonRuntimeIdentifiers =
    [
        "win-x64", "win-x86", "win-arm64",
        "linux-x64", "linux-arm64", "linux-musl-x64",
        "osx-x64", "osx-arm64",
    ];

    private readonly string _projectPath;
    private readonly string? _projectTargetFramework;
    private readonly PublishService _publishService;
    private readonly Action<string> _onOutputLine;
    private readonly List<string> _deletedProfileNames = [];

    public PublishDialogViewModel(
        string projectPath,
        string? projectTargetFramework,
        IReadOnlyList<PublishProfile> profiles,
        PublishService publishService,
        Action<string> onOutputLine)
    {
        _projectPath = projectPath;
        _projectTargetFramework = projectTargetFramework;
        _publishService = publishService;
        _onOutputLine = onOutputLine;

        ProjectName = Path.GetFileNameWithoutExtension(projectPath);
        foreach (var profile in profiles)
        {
            Profiles.Add(new PublishProfileEditorViewModel(profile, CommonRuntimeIdentifiers));
        }

        selectedProfile = Profiles.FirstOrDefault();
        statusMessage = Profiles.Count == 0
            ? "This project has no publish profiles yet. Choose New to create one."
            : "";
    }

    public string ProjectName { get; }

    /// <summary>Used by the Browse button to make a picked folder relative to the project, since that
    /// is how PublishDir is conventionally written.</summary>
    public string ProjectDirectory => Path.GetDirectoryName(_projectPath) ?? "";

    public ObservableCollection<PublishProfileEditorViewModel> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    private PublishProfileEditorViewModel? selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    private bool isPublishing;

    [ObservableProperty]
    private string statusMessage;

    /// <summary>Set only after a publish that succeeded and whose output folder was found, which is
    /// what gates the "open folder" button - a guessed path that turned out not to exist is not worth
    /// offering.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenPublishFolderCommand))]
    private string? lastPublishDirectory;

    public bool HasUnsavedChanges => _deletedProfileNames.Count > 0 || Profiles.Any(p => p.IsDirty);

    /// <summary>The profile names as they now stand on disk, for the status-bar picker to refresh
    /// from once the dialog closes.</summary>
    public IReadOnlyList<string> ResultProfileNames => Profiles.Select(p => p.Name).ToList();

    public string? ResultSelectedProfileName => SelectedProfile?.Name;

    [RelayCommand(CanExecute = nameof(IsNotPublishing))]
    private void NewProfile()
    {
        var profile = PublishProfile.CreateFolderDefault(GenerateUniqueName("FolderProfile"), _projectTargetFramework);
        var editor = new PublishProfileEditorViewModel(profile, CommonRuntimeIdentifiers);
        editor.MarkNewlyCreated();
        Profiles.Add(editor);
        SelectedProfile = editor;
        StatusMessage = "";
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedProfile))]
    private void DuplicateProfile()
    {
        if (SelectedProfile is not { } source)
        {
            return;
        }

        // FilePath is dropped so the copy is written as a new file rather than moving the original,
        // but everything else - including the properties IDE++ does not model - comes along.
        var profile = source.ToModel() with { Name = GenerateUniqueName(source.Name + "-Copy"), FilePath = null };
        var editor = new PublishProfileEditorViewModel(profile, CommonRuntimeIdentifiers);
        editor.MarkNewlyCreated();
        Profiles.Add(editor);
        SelectedProfile = editor;
        StatusMessage = "";
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } toRemove)
        {
            return;
        }

        var index = Profiles.IndexOf(toRemove);
        Profiles.Remove(toRemove);
        if (toRemove.FilePath is not null)
        {
            _deletedProfileNames.Add(toRemove.Name);
        }

        SelectedProfile = Profiles.Count > index ? Profiles[index] : Profiles.LastOrDefault();
        StatusMessage = $"Deleted {toRemove.Name}. Choose Save to apply.";
    }

    [RelayCommand(CanExecute = nameof(IsNotPublishing))]
    private void Save()
    {
        if (SaveAll() is { } error)
        {
            StatusMessage = error;
            return;
        }

        StatusMessage = "Saved.";
    }

    /// <summary>
    /// Writes every pending change, then publishes the selected profile.
    ///
    /// Saving first isn't a convenience: the publish is driven by `-p:PublishProfile=name`, so what
    /// runs is whatever the .pubxml on disk says. Publishing without saving would silently ignore the
    /// edits visible on screen.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task PublishAsync()
    {
        if (SelectedProfile is not { } editor)
        {
            return;
        }

        if (SaveAll() is { } error)
        {
            StatusMessage = error;
            return;
        }

        var profile = editor.ToModel();
        IsPublishing = true;
        LastPublishDirectory = null;
        StatusMessage = $"Publishing {profile.Name}...";
        _onOutputLine($"Publish started ({profile.Configuration}): {_projectPath} [{profile.Name}]");

        try
        {
            var exitCode = await _publishService.PublishAsync(
                _projectPath, profile, _onOutputLine, _projectTargetFramework);
            _onOutputLine($"Publish finished with exit code {exitCode}.");

            if (exitCode != 0)
            {
                StatusMessage = $"Publish failed with exit code {exitCode}. See the Output pane.";
                return;
            }

            StatusMessage = $"Published {profile.Name}.";
            if (PublishArguments.ResolvePublishDirectory(_projectPath, profile, _projectTargetFramework) is { } directory
                && Directory.Exists(directory))
            {
                LastPublishDirectory = directory;
                _onOutputLine($"Published to {directory}");
            }
        }
        catch (InvalidOperationException ex)
        {
            // Either the profile turned out not to be runnable, or a build won the race for the guard.
            StatusMessage = ex.Message;
            _onOutputLine($"Publish skipped - {ex.Message}");
        }
        finally
        {
            IsPublishing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasPublishFolder))]
    private void OpenPublishFolder()
    {
        if (LastPublishDirectory is not { } directory)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            StatusMessage = $"Could not open {directory}: {ex.Message}";
        }
    }

    /// <summary>
    /// Applies every pending change to disk, returning a message to show on failure or null on
    /// success. Deletes run first so renaming A to B while deleting the old B lands the right way up.
    /// </summary>
    public string? SaveAll()
    {
        if (Profiles.FirstOrDefault(p => PublishProfileStore.ValidateName(p.Name) is not null) is { } invalid)
        {
            return PublishProfileStore.ValidateName(invalid.Name);
        }

        if (Profiles.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            return $"There is more than one profile named '{duplicate.Key}'.";
        }

        try
        {
            foreach (var name in _deletedProfileNames)
            {
                PublishProfileStore.Delete(_projectPath, name);
            }

            _deletedProfileNames.Clear();

            foreach (var editor in Profiles.Where(p => p.IsDirty))
            {
                if (!editor.IsEditable)
                {
                    // Shown read-only, so it should never be dirty - but never write one even if it is.
                    continue;
                }

                PublishProfileStore.Save(_projectPath, editor.ToModel());
                editor.MarkSaved();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Could not save publish profiles: {ex.Message}";
        }

        return null;
    }

    private bool IsNotPublishing() => !IsPublishing;

    private bool CanEditSelectedProfile() => !IsPublishing && SelectedProfile is not null;

    private bool CanDeleteSelectedProfile() => CanEditSelectedProfile();

    private bool CanPublish() => !IsPublishing && SelectedProfile is { IsEditable: true };

    private bool HasPublishFolder() => LastPublishDirectory is not null;

    private string GenerateUniqueName(string baseName)
    {
        if (Profiles.All(p => !string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        var suffix = 2;
        while (Profiles.Any(p => string.Equals(p.Name, $"{baseName}{suffix}", StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
        }

        return $"{baseName}{suffix}";
    }
}
