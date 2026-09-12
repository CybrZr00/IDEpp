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
            SetStatus($"Pulled '{moduleName}' ({code.Length} chars).");
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
        CompileCheckButton.IsEnabled = !busy;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        _host.LogInfo($"[Access VBA] {message}");
    }
}
