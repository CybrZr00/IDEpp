using System.Net.Http;
using IdePlusPlus.Copilot.Anthropic;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The Ultra conventions setting: the user's standing rules for code, sent to every Ultra agent. It is a setting of its own and
/// not the chat's system prompt - that one is written for the chat's tools (propose_edit, fetch_url), which an Ultra agent
/// does not have - and a run that never sets it has to behave exactly as it did.
/// </summary>
public sealed class UltraConventionsSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-conventions-settings-{Guid.NewGuid():N}");

    public UltraConventionsSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private AppSettingsStore NewSettings() => new(Path.Combine(_root, "settings.json"));

    private SettingsViewModel CreateViewModel(AppSettingsStore settings) => new(
        new SecureSettingsStore(Path.Combine(_root, "secrets.json")),
        settings,
        new McpServerManager(
            new McpServerConfigStore(Path.Combine(_root, "mcp-servers.json")),
            new McpProjectTrustStore(Path.Combine(_root, "mcp-trust.json"))),
        new McpServerConfigStore(Path.Combine(_root, "mcp-servers.json")),
        new DeepSeekFimClient(new HttpClient(), new SecureSettingsStore(Path.Combine(_root, "secrets.json"))),
        new ExternalToolchainStore(Path.Combine(_root, "tools")),
        new AnthropicChatClient(new HttpClient(), new SecureSettingsStore(Path.Combine(_root, "secrets.json"))));

    [Fact]
    public void NothingSet_SendsNothing_SoAnExistingRunIsUnchanged()
    {
        CopilotChatPaneViewModel.ReadUltraConventions(NewSettings()).ShouldBeNull();
    }

    [Fact]
    public void WhatIsStored_IsTrimmed_AndBlankIsNothing()
    {
        var settings = NewSettings();

        settings.Set(CopilotChatPaneViewModel.UltraConventionsSettingKey, "  Nullable is on.\r\n ");
        CopilotChatPaneViewModel.ReadUltraConventions(settings).ShouldBe("Nullable is on.");

        settings.Set(CopilotChatPaneViewModel.UltraConventionsSettingKey, " \r\n ");
        CopilotChatPaneViewModel.ReadUltraConventions(settings).ShouldBeNull();
    }

    [Fact]
    public void AStoredTextOverTheLimit_IsCutWhenRead_SoAHandEditedFileCannotSpendAnAgentsWholePrompt()
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraConventionsSettingKey, new string('x', TeamConventions.MaxChars * 3));

        CopilotChatPaneViewModel.ReadUltraConventions(settings)!.Length.ShouldBe(TeamConventions.MaxChars + " [...]".Length);
    }

    [Fact]
    public void TheChatsSystemPrompt_IsNeverSentToUltra_ItNamesToolsAnUltraAgentDoesNotHave()
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.SystemPromptSettingKey, "Always finish with propose_edit.");

        CopilotChatPaneViewModel.ReadUltraConventions(settings).ShouldBeNull();
    }

    [Fact]
    public void TheSettingsWindow_ShowsWhatIsStored_AndSavesWhatIsTyped_WithoutTouchingTheChatsSystemPrompt()
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraConventionsSettingKey, "Nullable is on.");
        settings.Set(CopilotChatPaneViewModel.SystemPromptSettingKey, "Chat rules.");
        var viewModel = CreateViewModel(settings);

        viewModel.UltraConventions.ShouldBe("Nullable is on.");
        viewModel.UltraConventionsMaxLength.ShouldBe(TeamConventions.MaxChars);

        viewModel.UltraConventions = "Async all the way.";
        viewModel.SaveUltraConventionsCommand.Execute(null);

        settings.Get(CopilotChatPaneViewModel.UltraConventionsSettingKey).ShouldBe("Async all the way.");
        settings.Get(CopilotChatPaneViewModel.SystemPromptSettingKey).ShouldBe("Chat rules.");
        viewModel.StatusMessage.ShouldContain("saved");
    }

    [Fact]
    public void SavingABlankBox_ClearsTheConventions_AndSaysSo()
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraConventionsSettingKey, "Nullable is on.");
        var viewModel = CreateViewModel(settings);

        viewModel.UltraConventions = "  ";
        viewModel.SaveUltraConventionsCommand.Execute(null);

        CopilotChatPaneViewModel.ReadUltraConventions(settings).ShouldBeNull();
        viewModel.StatusMessage.ShouldContain("cleared");
    }
}
