using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.SourceControl;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Status/stage/commit/push/pull/branch UI backed by GitService, plus a diff view that reuses the
/// same DiffComputer the AI edit-proposal flow already uses - one diff renderer, two callers.
/// </summary>
public sealed partial class SourceControlPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "SourceControl";

    private readonly GitService _gitService;
    private readonly EditApplicationService _editApplicationService;
    private string? _repositoryDirectory;

    public SourceControlPaneViewModel(GitService gitService, EditApplicationService editApplicationService)
        : base(PaneContentId, "Git")
    {
        _gitService = gitService;
        _editApplicationService = editApplicationService;
    }

    public ObservableCollection<GitFileChange> StagedFiles { get; } = [];

    public ObservableCollection<GitFileChange> UnstagedFiles { get; } = [];

    public ObservableCollection<GitBranch> Branches { get; } = [];

    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    /// <summary>Raised once, right after <see cref="SetRepositoryDirectoryAsync"/>'s initial refresh,
    /// when the newly opened solution's repo has any pending changes - lets the shell bring this
    /// pane's tab to the front automatically, since it's easy to miss tabbed behind Solution Explorer
    /// otherwise (see MainWindow.xaml.cs's subscription).</summary>
    public event Action? PendingChangesDetected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchStatusText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnstageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckoutBranchCommand))]
    private bool isGitRepository;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchStatusText))]
    private string? currentBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchStatusText))]
    private int ahead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchStatusText))]
    private int behind;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAllCommand))]
    private string commitMessage = "";

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnstageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckoutBranchCommand))]
    private bool isBusy;

    [ObservableProperty]
    private GitFileChange? selectedFile;

    public string BranchStatusText
    {
        get
        {
            if (!IsGitRepository)
            {
                return "";
            }

            var branchText = CurrentBranch ?? "(detached)";
            return Ahead > 0 || Behind > 0 ? $"{branchText} ↑{Ahead} ↓{Behind}" : branchText;
        }
    }

    public async Task SetRepositoryDirectoryAsync(string? repositoryDirectory)
    {
        _repositoryDirectory = repositoryDirectory;
        StagedFiles.Clear();
        UnstagedFiles.Clear();
        Branches.Clear();
        DiffLines.Clear();
        SelectedFile = null;
        CurrentBranch = null;
        Ahead = 0;
        Behind = 0;
        StatusMessage = null;

        IsGitRepository = repositoryDirectory is not null && await _gitService.IsGitRepositoryAsync(repositoryDirectory);
        if (IsGitRepository)
        {
            await RefreshAsync();
            if (StagedFiles.Count > 0 || UnstagedFiles.Count > 0)
            {
                PendingChangesDetected?.Invoke();
            }
        }
    }

    private bool CanRunGitCommand() => IsGitRepository && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private async Task RefreshAsync()
    {
        if (_repositoryDirectory is null)
        {
            return;
        }

        var status = await _gitService.GetStatusAsync(_repositoryDirectory);

        StagedFiles.Clear();
        foreach (var change in status.Staged)
        {
            StagedFiles.Add(change);
        }

        UnstagedFiles.Clear();
        foreach (var change in status.Unstaged)
        {
            UnstagedFiles.Add(change);
        }

        CurrentBranch = status.Branch;
        Ahead = status.Ahead;
        Behind = status.Behind;

        var branches = await _gitService.GetBranchesAsync(_repositoryDirectory);
        Branches.Clear();
        foreach (var branch in branches)
        {
            Branches.Add(branch);
        }

        CommitCommand.NotifyCanExecuteChanged();
        CommitAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private async Task StageAsync(GitFileChange file)
    {
        if (_repositoryDirectory is null)
        {
            return;
        }

        await _gitService.StageAsync(_repositoryDirectory, file.Path);
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private async Task UnstageAsync(GitFileChange file)
    {
        if (_repositoryDirectory is null)
        {
            return;
        }

        await _gitService.UnstageAsync(_repositoryDirectory, file.Path);
        await RefreshAsync();
    }

    private bool CanCommit() => CanRunGitCommand() && StagedFiles.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (_repositoryDirectory is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _gitService.CommitAsync(_repositoryDirectory, CommitMessage);
            StatusMessage = result.Success ? "Committed." : result.Output;
            if (result.Success)
            {
                CommitMessage = "";
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private bool CanCommitAll() =>
        CanRunGitCommand() && (StagedFiles.Count > 0 || UnstagedFiles.Count > 0) && !string.IsNullOrWhiteSpace(CommitMessage);

    /// <summary>Stages every pending change and commits in one step, mirroring Visual Studio's Git
    /// Changes window "Commit All" - the discoverable default for someone who doesn't want to
    /// individually Stage each file first. <see cref="CommitAsync"/> (commit only what's already
    /// staged) stays available for anyone who wants to commit a subset.</summary>
    [RelayCommand(CanExecute = nameof(CanCommitAll))]
    private async Task CommitAllAsync()
    {
        if (_repositoryDirectory is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var stageResult = await _gitService.StageAllAsync(_repositoryDirectory);
            if (!stageResult.Success)
            {
                StatusMessage = stageResult.Output;
                return;
            }

            var result = await _gitService.CommitAsync(_repositoryDirectory, CommitMessage);
            StatusMessage = result.Success ? "Committed." : result.Output;
            if (result.Success)
            {
                CommitMessage = "";
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private Task PushAsync() => RunGitActionAsync(() => _gitService.PushAsync(_repositoryDirectory!));

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private Task PullAsync() => RunGitActionAsync(() => _gitService.PullAsync(_repositoryDirectory!));

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private Task FetchAsync() => RunGitActionAsync(() => _gitService.FetchAsync(_repositoryDirectory!));

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private Task CheckoutBranchAsync(GitBranch branch) =>
        branch.IsCurrent ? Task.CompletedTask : RunGitActionAsync(() => _gitService.CheckoutBranchAsync(_repositoryDirectory!, branch.Name));

    private async Task RunGitActionAsync(Func<Task<GitCommandResult>> action)
    {
        if (_repositoryDirectory is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await action();
            StatusMessage = result.Output.Length > 0 ? result.Output : (result.Success ? "Done." : "Failed.");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    partial void OnSelectedFileChanged(GitFileChange? value) => _ = LoadDiffAsync(value);

    /// <summary>Diffs the file's HEAD content against its current content (the live open-editor
    /// buffer if it's open, otherwise disk) - the same "prefer the live buffer" rule
    /// EditApplicationService already applies when computing an AI edit-proposal diff.</summary>
    private async Task LoadDiffAsync(GitFileChange? file)
    {
        DiffLines.Clear();
        if (file is null || _repositoryDirectory is null)
        {
            return;
        }

        var fullPath = Path.GetFullPath(Path.Combine(_repositoryDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        var headContent = await _gitService.GetFileAtHeadAsync(_repositoryDirectory, file.Path);
        var currentContent = _editApplicationService.GetCurrentContent(fullPath);

        if (!ReferenceEquals(file, SelectedFile))
        {
            return; // the selection moved on while the diff was loading
        }

        foreach (var line in DiffComputer.Compute(headContent, currentContent))
        {
            DiffLines.Add(line);
        }
    }
}
