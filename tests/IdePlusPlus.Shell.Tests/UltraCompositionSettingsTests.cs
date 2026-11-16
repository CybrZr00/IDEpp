using System.Net.Http;
using IdePlusPlus.Copilot.Anthropic;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The team composition settings: the preset, the builder and per-lane counts, and the two team-wide
/// choices that go with them. A settings file from before the roster has only a worker count - every
/// worker then was a builder - so reading one has to land on Speed with that many builders, and a run
/// that never opens this page has to behave exactly as it did.
/// </summary>
public sealed class UltraCompositionSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-composition-{Guid.NewGuid():N}");

    public UltraCompositionSettingsTests() => Directory.CreateDirectory(_root);

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
    public void NothingSet_IsSpeed_SoAnExistingRunIsUnchanged()
    {
        var composition = CopilotChatPaneViewModel.ReadUltraComposition(NewSettings());

        CopilotChatPaneViewModel.ReadUltraCompositionPreset(NewSettings()).ShouldBe(TeamCompositionPreset.Speed);
        composition.Builders.ShouldBe(CopilotChatPaneViewModel.DefaultUltraWorkerCount);
        composition.SpecialistTotal.ShouldBe(0);
        composition.Validate().ShouldBeNull();
    }

    [Fact]
    public void AFileWithOnlyAWorkerCount_ReadsAsThatManyBuilders()
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraWorkerCountSettingKey, "6");

        CopilotChatPaneViewModel.ReadUltraBuilders(settings).ShouldBe(6, "every worker was a builder before the roster");
        CopilotChatPaneViewModel.ReadUltraComposition(settings).Builders.ShouldBe(6);

        // The builder key, once written, wins - and is clamped like every other count.
        settings.Set(CopilotChatPaneViewModel.UltraBuildersSettingKey, "3");
        CopilotChatPaneViewModel.ReadUltraBuilders(settings).ShouldBe(3);
        settings.Set(CopilotChatPaneViewModel.UltraBuildersSettingKey, "0");
        CopilotChatPaneViewModel.ReadUltraBuilders(settings).ShouldBe(1, "a team needs a builder");
        settings.Set(CopilotChatPaneViewModel.UltraBuildersSettingKey, "999");
        CopilotChatPaneViewModel.ReadUltraBuilders(settings).ShouldBe(CopilotChatPaneViewModel.MaxUltraWorkerCount);
    }

    [Fact]
    public void ALaneWithNoCountOfItsOwn_ComesFromThePreset()
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraCompositionPresetSettingKey, nameof(TeamCompositionPreset.Balanced));

        CopilotChatPaneViewModel.ReadUltraSpecialistCount(settings, TeamRoleKind.MutationTester).ShouldBe(1);
        CopilotChatPaneViewModel.ReadUltraSpecialistCount(settings, TeamRoleKind.Adversary).ShouldBe(1);
        CopilotChatPaneViewModel.ReadUltraSpecialistCount(settings, TeamRoleKind.Conformity).ShouldBe(1);
        CopilotChatPaneViewModel.ReadUltraSpecialistCount(settings, TeamRoleKind.GapFinder).ShouldBe(1);
        CopilotChatPaneViewModel.ReadUltraSpecialistCount(settings, TeamRoleKind.FollowUpScout)
            .ShouldBe(0, "Balanced leaves the follow-up lane out - it is a report lane, not a review one");
    }

    [Fact]
    public void ALaneCount_IsClampedToTheTwoALaneCanUse()
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraAdversariesSettingKey, "9");
        CopilotChatPaneViewModel.ReadUltraSpecialistCount(settings, TeamRoleKind.Adversary)
            .ShouldBe(TeamComposition.MaxSpecialistsPerKind);

        settings.Set(CopilotChatPaneViewModel.UltraAdversariesSettingKey, "-2");
        CopilotChatPaneViewModel.ReadUltraSpecialistCount(settings, TeamRoleKind.Adversary).ShouldBe(0);
    }

    [Fact]
    public void Balanced_WithThreeBuilders_IsThreeBuildersAndOneOfEachReviewLane()
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraBuildersSettingKey, "3");
        settings.Set(CopilotChatPaneViewModel.UltraCompositionPresetSettingKey, nameof(TeamCompositionPreset.Balanced));

        var composition = CopilotChatPaneViewModel.ReadUltraComposition(settings);

        composition.Builders.ShouldBe(3);
        composition.SpecialistTotal.ShouldBe(4);
        composition.Total.ShouldBe(7);
        composition.Validate().ShouldBeNull();
        composition.Workers.Count.ShouldBe(7);
        composition.Workers.Select(worker => worker.Name).ShouldBe(["b1", "b2", "b3", "mut1", "adv1", "lint1", "gap1"]);
    }

    [Fact]
    public void LeadReviewMode_IsBlockingUntilAdvisoryIsChosen()
    {
        var settings = NewSettings();
        CopilotChatPaneViewModel.ReadUltraLeadReviewMode(settings).ShouldBe(LeadReviewMode.Blocking);

        settings.Set(CopilotChatPaneViewModel.UltraLeadReviewModeSettingKey, "advisory");
        CopilotChatPaneViewModel.ReadUltraLeadReviewMode(settings).ShouldBe(LeadReviewMode.Advisory);

        settings.Set(CopilotChatPaneViewModel.UltraLeadReviewModeSettingKey, "sideways");
        CopilotChatPaneViewModel.ReadUltraLeadReviewMode(settings).ShouldBe(LeadReviewMode.Blocking);
    }

    [Fact]
    public void SpecialistEffort_IsUnsetUntilChosen_SoEachRoleKeepsItsOwnDefault()
    {
        var settings = NewSettings();
        CopilotChatPaneViewModel.ReadUltraSpecialistEffort(settings).ShouldBeNull();

        settings.Set(CopilotChatPaneViewModel.UltraSpecialistEffortSettingKey, nameof(ChatEffort.Low));
        CopilotChatPaneViewModel.ReadUltraSpecialistEffort(settings).ShouldBe(ChatEffort.Low);

        settings.Set(CopilotChatPaneViewModel.UltraSpecialistEffortSettingKey, nameof(ChatEffort.Ultra));
        CopilotChatPaneViewModel.ReadUltraSpecialistEffort(settings).ShouldBeNull("Ultra is the whole team's effort, not a specialist's");
    }

    [Fact]
    public void ChoosingAPreset_FillsTheCountsIn_AndTheCardSaysWhatTheTeamWillCost()
    {
        var viewModel = CreateViewModel(NewSettings());
        viewModel.UltraWorkerCount = 3;

        viewModel.UltraCompositionPreset = nameof(TeamCompositionPreset.Balanced);

        viewModel.UltraMutationTesters.ShouldBe(1);
        viewModel.UltraAdversaries.ShouldBe(1);
        viewModel.UltraConformity.ShouldBe(1);
        viewModel.UltraGapFinders.ShouldBe(1);
        viewModel.UltraFollowUpScouts.ShouldBe(0);
        viewModel.Composition.Total.ShouldBe(7);
        viewModel.UltraCompositionProblem.ShouldBeNull();
        viewModel.UltraCompositionSummary.ShouldContain("7 workers");
        viewModel.UltraCompositionSummary.ShouldContain("3 builders");
        viewModel.UltraCompositionSummary.ShouldContain("GB");
    }

    [Fact]
    public void TheCountsAreWhatIsSaved_NotThePresetTheyStartedFrom()
    {
        var settings = NewSettings();
        var viewModel = CreateViewModel(settings);
        viewModel.UltraWorkerCount = 2;
        viewModel.UltraCompositionPreset = nameof(TeamCompositionPreset.Hardening);
        viewModel.UltraAdversaries = 1;

        viewModel.SaveUltraSettingsCommand.Execute(null);

        var saved = CopilotChatPaneViewModel.ReadUltraComposition(settings);
        saved.Builders.ShouldBe(2);
        saved.CountOf(TeamRoleKind.MutationTester).ShouldBe(2, "Hardening starts with two; only the adversary count was edited");
        saved.CountOf(TeamRoleKind.Adversary).ShouldBe(1);
        CopilotChatPaneViewModel.ReadUltraCompositionPreset(settings).ShouldBe(TeamCompositionPreset.Hardening);
    }

    [Fact]
    public void ATeamPastTheWorkerLimit_IsRefusedAtSave_WithTheReason()
    {
        var settings = NewSettings();
        var viewModel = CreateViewModel(settings);
        viewModel.UltraWorkerCount = CopilotChatPaneViewModel.MaxUltraWorkerCount;
        viewModel.UltraAdversaries = 2;

        viewModel.SaveUltraSettingsCommand.Execute(null);

        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("at most 64");
        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("Nothing was saved");
        CopilotChatPaneViewModel.ReadUltraComposition(settings).CountOf(TeamRoleKind.Adversary)
            .ShouldBe(0, "a composition that cannot start is not written");
    }
}
