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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchStatusText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnstageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
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
    private string commitMessage = "";

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(StageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnstageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
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

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private Task PushAsync() => RunGitActionAsync("Pushing...", () => _gitService.PushAsync(_repositoryDirectory!));

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private Task PullAsync() => RunGitActionAsync("Pulling...", () => _gitService.PullAsync(_repositoryDirectory!));

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private Task FetchAsync() => RunGitActionAsync("Fetching...", () => _gitService.FetchAsync(_repositoryDirectory!));

    [RelayCommand(CanExecute = nameof(CanRunGitCommand))]
    private Task CheckoutBranchAsync(GitBranch branch) =>
        branch.IsCurrent
            ? Task.CompletedTask
            : RunGitActionAsync("Checking out...", () => _gitService.CheckoutBranchAsync(_repositoryDirectory!, branch.Name));

    /// <summary>Sets StatusMessage to <paramref name="inProgressMessage"/> up front, before the git
    /// process even starts, rather than only once it finishes - push/pull/fetch hit the network and
    /// can take a while, and a pane that shows nothing until the command completes reads as "did my
    /// click even register?" even though it's just IsBusy-disabled buttons doing their job.</summary>
    private async Task RunGitActionAsync(string inProgressMessage, Func<Task<GitCommandResult>> action)
    {
        if (_repositoryDirectory is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = inProgressMessage;
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
