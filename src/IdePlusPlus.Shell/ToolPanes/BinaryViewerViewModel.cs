using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.ReverseEngineering;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// The shared logic behind the binary viewer, used both by the dockable Binary Explorer pane and by a
/// binary document tab. Loads a .NET assembly (types, decompiled in-process and instantly) or a native
/// binary (functions, decompiled through Ghidra); picking an item shows its code.
/// </summary>
public sealed partial class BinaryViewerViewModel : ObservableObject
{
    private const string DefaultStatus =
        "Enter a binary path and Load. .NET assemblies open in-process; native binaries use Ghidra (install it in Settings).";

    private ManagedDecompiler? _managedDecompiler;
    private GhidraHeadlessBackend? _ghidra;
    private GhidraSession? _session;
    private string _loadedPath = "";
    private bool _isManaged;
    private CancellationTokenSource? _decompileCts;

    /// <summary>Marshals off-thread UI updates to the dispatcher when there is one (the app), or runs
    /// inline when there isn't (tests).</summary>
    public Action<Action> Post { get; set; } = action =>
    {
        var application = System.Windows.Application.Current;
        if (application is not null)
        {
            application.Dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    };

    [ObservableProperty]
    private string binaryPath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = DefaultStatus;

    [ObservableProperty]
    private string itemsHeader = "";

    [ObservableProperty]
    private string code = "";

    [ObservableProperty]
    private string? selectedItem;

    public ObservableCollection<string> Items { get; } = [];

    private ManagedDecompiler ManagedDecompiler => _managedDecompiler ??= new ManagedDecompiler();

    private GhidraHeadlessBackend Ghidra =>
        _ghidra ??= new GhidraHeadlessBackend(new GhidraToolchain(new ExternalToolchainStore()));

    private bool CanLoad => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        var path = (BinaryPath ?? "").Trim().Trim('"');
        if (!File.Exists(path))
        {
            StatusText = $"No file found at '{path}'.";
            return;
        }

        IsBusy = true;
        StatusText = "Loading...";
        Items.Clear();
        Code = "";
        SelectedItem = null;
        ItemsHeader = "";
        _session = null;
        _loadedPath = path;

        try
        {
            bool managed;
            try
            {
                ManagedDecompiler.Overview(path);
                managed = true;
            }
            catch (InvalidOperationException)
            {
                managed = false;
            }

            _isManaged = managed;
            if (managed)
            {
                var types = await Task.Run(() => ManagedDecompiler.ListTypes(path, null, 5000));
                Post(() =>
                {
                    foreach (var type in types)
                    {
                        Items.Add(type);
                    }

                    ItemsHeader = "Types";
                    StatusText = $"{types.Count} type(s) - select one to decompile.";
                });
            }
            else if (!Ghidra.IsAvailable)
            {
                Post(() => StatusText = GhidraHeadlessBackend.UnavailableMessage);
            }
            else
            {
                var open = await Ghidra.OpenAsync(path, line => Post(() => StatusText = line), CancellationToken.None);
                if (open.Session is null)
                {
                    Post(() => StatusText = open.Text);
                }
                else
                {
                    _session = open.Session;
                    var functions = await Ghidra.ListFunctionsStructuredAsync(open.Session, null, 5000, _ => { }, CancellationToken.None);
                    Post(() =>
                    {
                        foreach (var function in functions)
                        {
                            Items.Add(function.Name);
                        }

                        ItemsHeader = "Functions";
                        StatusText = $"{functions.Count} function(s) - select one to decompile.";
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Post(() => StatusText = $"Failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedItemChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _ = DecompileSelectedAsync(value);
        }
    }

    private async Task DecompileSelectedAsync(string item)
    {
        _decompileCts?.Cancel();
        var cts = new CancellationTokenSource();
        _decompileCts = cts;

        Post(() =>
        {
            Code = "";
            StatusText = $"Decompiling {item}...";
        });

        try
        {
            if (_isManaged)
            {
                var code = await Task.Run(() => ManagedDecompiler.DecompileType(_loadedPath, item), cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    Post(() => { Code = code; StatusText = item; });
                }
            }
            else if (_session is { } session)
            {
                var (isError, text) = await Ghidra.DecompileAsync(session, item, _ => { }, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    Post(() => { Code = text; StatusText = isError ? $"Error decompiling {item}" : item; });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                Post(() => StatusText = $"Failed: {ex.Message}");
            }
        }
    }
}
