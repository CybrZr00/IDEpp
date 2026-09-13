using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Xml;
using AccessMCP;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using IdePlusPlus.PluginContracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessVbaPlugin;

public partial class AccessVbaToolWindow : UserControl
{
    private readonly IPluginHost _host;
    private readonly AccessMcpOptions _options;
    private readonly AccessTools _tools;
    private string? _currentModuleName;
    private string? _lastPulledCode;

    public AccessVbaToolWindow(IPluginHost host)
    {
        InitializeComponent();
        _host = host;
        _options = LoadOptions();
        _tools = new AccessTools(_options, NullLoggerFactory.Instance);

        ExportFolderTextBox.Text = DefaultExportFolder(_options);
        Editor.SyntaxHighlighting = LoadVbaHighlighting();

        Loaded += async (_, _) => await RefreshModulesAsync();
    }

    /// <summary>
    /// Loads Vba.xshd (shipped alongside the plugin DLL) directly into this window's own AvalonEdit
    /// instance - not via IPluginHost.RegisterHighlighting, which only applies to file-based
    /// documents opened in IDE++'s main editor. Falls back to no highlighting (not a crash) if the
    /// file is missing or malformed, since this is cosmetic, not load-bearing.
    /// </summary>
    private IHighlightingDefinition? LoadVbaHighlighting()
    {
        try
        {
            var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var xshdPath = Path.Combine(pluginDirectory, "Vba.xshd");
            using var reader = XmlReader.Create(xshdPath);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex)
        {
            SetStatus($"VBA syntax highlighting unavailable: {ex.Message}");
            return null;
        }
    }

    private static string DefaultExportFolder(AccessMcpOptions options)
    {
        var dbDirectory = options.DefaultDatabasePath is { Length: > 0 }
            ? Path.GetDirectoryName(options.DefaultDatabasePath)
            : null;

        return Path.Combine(dbDirectory ?? Environment.CurrentDirectory, "_vba_export");
    }

    /// <summary>
    /// Reuses the same config file AccessMCP.exe reads, so there's one source of truth (database
    /// path, workgroup file, ReadOnly, EnableComAutomation) for both the standalone MCP server and
    /// this in-process plugin.
    /// </summary>
    private static AccessMcpOptions LoadOptions()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdePlusPlus", "accessmcp-config.json");

        if (!File.Exists(path))
        {
            return new AccessMcpOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<AccessMcpOptions>(File.ReadAllText(path)) ?? new AccessMcpOptions();
        }
        catch (JsonException)
        {
            return new AccessMcpOptions();
        }
    }

    private async void RefreshModules_Click(object sender, System.Windows.RoutedEventArgs e) => await RefreshModulesAsync();

    private async Task RefreshModulesAsync()
    {
        SetStatus("Loading module list...");
        try
        {
            var json = await _tools.ListVbeModulesAsync();
            using var doc = JsonDocument.Parse(json);

            var names = new List<string>();
            foreach (var module in doc.RootElement.GetProperty("modules").EnumerateArray())
            {
                var name = module.GetProperty("name").GetString();
                if (name is not null)
                {
                    names.Add(name);
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);

            ModuleComboBox.ItemsSource = names;
            SetStatus($"{names.Count} modules found.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to list modules: {ex.Message}");
        }
    }

    private async void ModuleComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModuleComboBox.SelectedItem is string name)
        {
            await PullModuleAsync(name);
        }
    }

    private async void Pull_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_currentModuleName is not null)
        {
            await PullModuleAsync(_currentModuleName);
        }
    }

    private async Task PullModuleAsync(string moduleName)
    {
        SetBusy(true);
        SetStatus($"Pulling '{moduleName}'...");
        try
        {
            var json = await _tools.GetVbeModuleCodeAsync(moduleName);
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.GetProperty("code").GetString() ?? string.Empty;

            _currentModuleName = moduleName;
            _lastPulledCode = code;
            Editor.Text = code;

            var status = $"Pulled '{moduleName}' ({code.Length} chars).";
            var baseHash = TryGetExportBaseHash(ExportFolderTextBox.Text, moduleName);
            if (baseHash is not null)
            {
                status += string.Equals(baseHash, Sha256Hex(code), StringComparison.OrdinalIgnoreCase)
                    ? " Matches the export folder's copy."
                    : " NOTE: changed in Access since the export folder was written - that copy is stale.";
            }
            SetStatus(status);
        }
        catch (Exception ex)
        {
            SetStatus($"Pull failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Push_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_currentModuleName is null)
        {
            SetStatus("Pull a module before pushing.");
            return;
        }

        SetBusy(true);
        SetStatus($"Pushing '{_currentModuleName}'...");
        try
        {
            var json = await _tools.ImportModuleCodeAsync(
                _currentModuleName,
                Editor.Text,
                compile: CompileCheckBox.IsChecked == true,
                expectedPreviousCode: _lastPulledCode);

            using var doc = JsonDocument.Parse(json);
            var backupPath = doc.RootElement.TryGetProperty("backupPath", out var backupProp) ? backupProp.GetString() : null;

            _lastPulledCode = Editor.Text;
            SetStatus($"Pushed '{_currentModuleName}'. Backup: {backupPath}");
        }
        catch (Exception ex)
        {
            // Unlike going through the MCP tool surface, an in-process call like this one keeps the
            // real exception message - including the self-verifying-write/rollback outcome from
            // AccessTools.ImportModuleCodeAsync - so this is worth showing in full, not summarizing.
            SetStatus($"Push FAILED: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExportAll_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var folderPath = ExportFolderTextBox.Text;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            SetStatus("Enter a folder to export into.");
            return;
        }

        SetBusy(true);
        SetStatus($"Exporting all modules to '{folderPath}' (this takes a minute or two for a large project)...");
        try
        {
            var json = await _tools.ExportAllModulesToFolderAsync(folderPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var count = root.GetProperty("exportedCount").GetInt32();
            var preserved = root.GetProperty("preservedLocalChanges").EnumerateArray().Select(e => e.GetString()).ToList();
            var notSafe = root.GetProperty("notRoundTripSafe").GetArrayLength();

            var status = $"Exported {count} modules to '{folderPath}'.";
            if (preserved.Count > 0)
            {
                status += $" {preserved.Count} file(s) with unpushed local edits were left alone ({string.Join(", ", preserved.Take(5))}{(preserved.Count > 5 ? ", ..." : "")}) - import them first.";
            }
            if (notSafe > 0)
            {
                status += $" {notSafe} module(s) can't be round-tripped from their files and will be skipped on import.";
            }
            SetStatus(status);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportChanged_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var folderPath = ExportFolderTextBox.Text;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            SetStatus("Enter the folder to import from.");
            return;
        }

        SetBusy(true);
        SetStatus($"Importing changed modules from '{folderPath}'...");
        try
        {
            var json = await _tools.ImportChangedModulesFromFolderAsync(folderPath, compile: CompileCheckBox.IsChecked == true);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var updated = root.GetProperty("updated").EnumerateArray().Select(u => u.GetProperty("module").GetString()).ToList();
            var stale = root.GetProperty("stale").EnumerateArray().Select(s => s.GetString()).ToList();
            var failed = root.GetProperty("failed").EnumerateArray()
                .Select(f => $"{f.GetProperty("module").GetString()}: {f.GetProperty("error").GetString()}").ToList();
            var unchanged = root.GetProperty("unchangedCount").GetInt32();

            var status = $"Imported {updated.Count} changed module(s), {unchanged} unchanged.";
            if (stale.Count > 0)
            {
                status += $" REFUSED as changed in Access since export: {string.Join(", ", stale)} - re-export and merge by hand.";
            }
            if (failed.Count > 0)
            {
                status += $" FAILED: {string.Join(" | ", failed)}";
            }
            SetStatus(status);

            if (_currentModuleName is not null && updated.Contains(_currentModuleName))
            {
                await PullModuleAsync(_currentModuleName);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Import failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CheckDrift_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var folderPath = ExportFolderTextBox.Text;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            SetStatus("Enter the export folder to check against.");
            return;
        }

        SetBusy(true);
        SetStatus("Checking for drift between Access and the export folder...");
        try
        {
            var json = await _tools.CheckExportDriftAsync(folderPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            static string Names(JsonElement root, string property)
            {
                var names = root.GetProperty(property).EnumerateArray().Select(e => e.GetString()).ToList();
                return names.Count == 0 ? "" : $" [{string.Join(", ", names.Take(8))}{(names.Count > 8 ? ", ..." : "")}]";
            }

            var drifted = root.GetProperty("driftedInAccess").GetArrayLength();
            var edited = root.GetProperty("editedLocally").GetArrayLength();
            var conflicts = root.GetProperty("conflicts").GetArrayLength();
            var matches = root.GetProperty("localMatchesLive").GetArrayLength();
            var added = root.GetProperty("newInAccess").GetArrayLength();
            var removed = root.GetProperty("removedFromAccess").GetArrayLength();
            var unchanged = root.GetProperty("unchangedCount").GetInt32();

            var status = drifted + conflicts + added + removed == 0 && edited == 0 && matches == 0
                ? $"No drift: all {unchanged} modules match the export folder."
                : $"{drifted} changed in Access since export{Names(root, "driftedInAccess")}; " +
                  $"{edited} edited locally{Names(root, "editedLocally")}; " +
                  $"{conflicts} CONFLICT (both){Names(root, "conflicts")}; " +
                  $"{matches} local already matches Access{Names(root, "localMatchesLive")}; " +
                  $"{added} new in Access{Names(root, "newInAccess")}; {removed} removed from Access{Names(root, "removedFromAccess")}; {unchanged} unchanged.";
            SetStatus(status);
        }
        catch (Exception ex)
        {
            SetStatus($"Drift check failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void PullDrifted_Click(object sender, System.Windows.RoutedEventArgs e) => await PullDriftedAsync(markResolved: null);

    private async void MarkResolved_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ModuleComboBox.SelectedItem is not string name)
        {
            SetStatus("Select the module you've merged by hand first.");
            return;
        }

        await PullDriftedAsync(markResolved: [name]);
    }

    private async Task PullDriftedAsync(string[]? markResolved)
    {
        var folderPath = ExportFolderTextBox.Text;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            SetStatus("Enter the export folder to pull into.");
            return;
        }

        SetBusy(true);
        SetStatus(markResolved is null
            ? "Pulling modules that changed in Access into the export folder..."
            : $"Marking '{markResolved[0]}' as merged (adopting the Access version as its base)...");
        try
        {
            var json = await _tools.PullDriftedModulesAsync(folderPath, markResolved);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            static List<string?> Names(JsonElement root, string property)
                => root.GetProperty(property).EnumerateArray().Select(e => e.GetString()).ToList();

            var updated = Names(root, "updated");
            var added = Names(root, "newExported");
            var resolved = Names(root, "resolvedAutomatically").Concat(Names(root, "markedResolved")).ToList();
            var edited = Names(root, "editedLocally");
            var removed = Names(root, "removedFromAccess");
            var conflicts = root.GetProperty("conflicts").EnumerateArray().Select(c => c.GetProperty("module").GetString()).ToList();

            var parts = new List<string>();
            if (updated.Count > 0) parts.Add($"updated {updated.Count} from Access [{string.Join(", ", updated.Take(8))}]");
            if (added.Count > 0) parts.Add($"{added.Count} new module(s) exported");
            if (resolved.Count > 0) parts.Add($"{resolved.Count} resolved [{string.Join(", ", resolved)}]");
            if (conflicts.Count > 0) parts.Add($"{conflicts.Count} CONFLICT(S) - live version written as <file>.live for merging, then Mark Selected Resolved [{string.Join(", ", conflicts)}]");
            if (edited.Count > 0) parts.Add($"{edited.Count} with local edits left alone");
            if (removed.Count > 0) parts.Add($"{removed.Count} no longer in Access (dropped from manifest, files kept)");
            SetStatus(parts.Count == 0 ? "Nothing to pull - export folder already matches Access." : "Pull: " + string.Join("; ", parts) + ".");

            if (_currentModuleName is not null && updated.Contains(_currentModuleName))
            {
                await PullModuleAsync(_currentModuleName);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Pull failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string? TryGetExportBaseHash(string folderPath, string moduleName)
    {
        try
        {
            var manifestPath = Path.Combine(folderPath, "_accessmcp_manifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var module in doc.RootElement.GetProperty("modules").EnumerateArray())
            {
                if (string.Equals(module.GetProperty("name").GetString(), moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return module.GetProperty("bodySha256").GetString();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
        {
        }

        return null;
    }

    private static string Sha256Hex(string text)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private async void CompileCheck_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SetBusy(true);
        SetStatus("Compiling all modules...");
        try
        {
            await _tools.CompileVbaAsync();
            SetStatus("Compile check passed - all modules compiled and saved.");
        }
        catch (Exception ex)
        {
            SetStatus($"Compile check FAILED: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        PullButton.IsEnabled = !busy;
        PushButton.IsEnabled = !busy;
        ModuleComboBox.IsEnabled = !busy;
        ExportAllButton.IsEnabled = !busy;
        ImportChangedButton.IsEnabled = !busy;
        CheckDriftButton.IsEnabled = !busy;
        PullDriftedButton.IsEnabled = !busy;
        MarkResolvedButton.IsEnabled = !busy;
        CompileCheckButton.IsEnabled = !busy;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        _host.LogInfo($"[Access VBA] {message}");
    }
}
