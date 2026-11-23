using System.ComponentModel;
using System.Net.Http;
using IdePlusPlus.Copilot.Anthropic;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The pipeline's settings: which strategy an Ultra message runs, and the limits the pipeline works within. A settings file from before
/// the pipeline has none of them, so reading one - or one with nonsense in it - has to land on the Ultra team and the defaults, and a
/// run that never opens this page has to behave exactly as it did.
/// </summary>
public sealed class UltraPipelineSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-pipeline-settings-{Guid.NewGuid():N}");

    public UltraPipelineSettingsTests() => Directory.CreateDirectory(_root);

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

    // ---- Reading ----

    [Fact]
    public void NothingSet_IsTheUltraTeamAndTheDefaultLimits_SoAnExistingRunIsUnchanged()
    {
        var settings = NewSettings();

        CopilotChatPaneViewModel.ReadUltraStrategy(settings).ShouldBe(UltraStrategy.Team);
        CopilotChatPaneViewModel.ReadUltraPipelineMaxLoops(settings).ShouldBe(3);
        CopilotChatPaneViewModel.ReadUltraPipelineSeverityThreshold(settings).ShouldBe(TeamFindingSeverity.Medium);
        CopilotChatPaneViewModel.ReadUltraPipelineMaxUnderstand(settings).ShouldBe(3);
        CopilotChatPaneViewModel.ReadUltraPipelineMaxOwners(settings).ShouldBe(6);
        CopilotChatPaneViewModel.ReadUltraPipelineMaxCustomJudges(settings).ShouldBe(2);
    }

    [Theory]
    [InlineData("Pipeline", UltraStrategy.Pipeline)]
    [InlineData("pipeline", UltraStrategy.Pipeline)]
    [InlineData("PIPELINE", UltraStrategy.Pipeline)]
    [InlineData("Team", UltraStrategy.Team)]
    [InlineData("team", UltraStrategy.Team)]
    [InlineData("", UltraStrategy.Team)]
    [InlineData("Sideways", UltraStrategy.Team)]
    [InlineData("2", UltraStrategy.Team)]   // a number that is not a member is not a choice...
    [InlineData("7", UltraStrategy.Team)]
    [InlineData("1", UltraStrategy.Pipeline)] // ...and one that is, is
    public void TheStrategy_IsThePipelineOnlyWhenThePipelineWasChosen(string stored, UltraStrategy expected)
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraStrategySettingKey, stored);

        CopilotChatPaneViewModel.ReadUltraStrategy(settings).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-4", 1)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("8", 8)]
    [InlineData("99", 8)]
    [InlineData("many", 3)]
    [InlineData("", 3)]
    public void TheRepairRounds_AreOneToEight_AndNonsenseIsTheDefault(string stored, int expected)
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraPipelineMaxLoopsSettingKey, stored);

        CopilotChatPaneViewModel.ReadUltraPipelineMaxLoops(settings).ShouldBe(expected);
    }

    [Theory]
    [InlineData("-1", 0)]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("9", 5)]
    [InlineData("x", 3)]
    public void TheSurveyors_AreNoneToFive(string stored, int expected)
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraPipelineMaxUnderstandSettingKey, stored);

        CopilotChatPaneViewModel.ReadUltraPipelineMaxUnderstand(settings).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("1", 1)]
    [InlineData("16", 16)]
    [InlineData("99", 16)]
    [InlineData("x", 6)]
    public void TheOwners_AreOneToSixteen(string stored, int expected)
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraPipelineMaxOwnersSettingKey, stored);

        CopilotChatPaneViewModel.ReadUltraPipelineMaxOwners(settings).ShouldBe(expected);
    }

    [Theory]
    [InlineData("-3", 0)]
    [InlineData("0", 0)]
    [InlineData("3", 3)]
    [InlineData("9", 3)]
    [InlineData("x", 2)]
    public void TheExtraJudges_AreNoneToThree(string stored, int expected)
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraPipelineMaxCustomJudgesSettingKey, stored);

        CopilotChatPaneViewModel.ReadUltraPipelineMaxCustomJudges(settings).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Low", TeamFindingSeverity.Low)]
    [InlineData("medium", TeamFindingSeverity.Medium)]
    [InlineData("HIGH", TeamFindingSeverity.High)]
    [InlineData("Critical", TeamFindingSeverity.Medium)]
    [InlineData("9", TeamFindingSeverity.Medium)]
    [InlineData("", TeamFindingSeverity.Medium)]
    public void TheSeverityBar_IsMediumUnlessLowOrHighWasChosen(string stored, TeamFindingSeverity expected)
    {
        var settings = NewSettings();
        settings.Set(CopilotChatPaneViewModel.UltraPipelineSeverityThresholdSettingKey, stored);

        CopilotChatPaneViewModel.ReadUltraPipelineSeverityThreshold(settings).ShouldBe(expected);
    }

    // ---- The page ----

    [Fact]
    public void ThePage_ShowsTheTeamAndTheDefaults_UntilThePipelineIsChosen()
    {
        var viewModel = CreateViewModel(NewSettings());

        viewModel.UltraRunStrategy.ShouldBe("Team");
        viewModel.IsUltraPipeline.ShouldBeFalse();
        (viewModel.UltraPipelineMaxLoops, viewModel.UltraPipelineSeverityThreshold, viewModel.UltraPipelineMaxUnderstand,
            viewModel.UltraPipelineMaxOwners, viewModel.UltraPipelineMaxCustomJudges).ShouldBe((3, "Medium", 3, 6, 2));
        viewModel.UltraStrategyChoices.ShouldBe(["Team", "Pipeline"]);
        viewModel.SeverityThresholdChoices.ShouldBe(["Low", "Medium", "High"]);
    }

    [Fact]
    public void ChoosingThePipeline_SaysSo_ToWhateverEnablesTheRestOfTheCard()
    {
        var viewModel = CreateViewModel(NewSettings());
        var raised = new List<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.UltraRunStrategy = "Pipeline";

        raised.ShouldContain(nameof(SettingsViewModel.IsUltraPipeline));
        viewModel.IsUltraPipeline.ShouldBeTrue();
        viewModel.UltraRunStrategy = "pipeline";
        viewModel.IsUltraPipeline.ShouldBeTrue("the choice is read the way it is stored: without regard to case");
        viewModel.UltraRunStrategy = "Team";
        viewModel.IsUltraPipeline.ShouldBeFalse();
    }

    [Fact]
    public void SavedValues_AreWhatTheNextPageAndTheNextRunRead()
    {
        var settings = NewSettings();
        var viewModel = CreateViewModel(settings);
        viewModel.UltraRunStrategy = "Pipeline";
        viewModel.UltraPipelineMaxLoops = 5;
        viewModel.UltraPipelineSeverityThreshold = "High";
        viewModel.UltraPipelineMaxUnderstand = 1;
        viewModel.UltraPipelineMaxOwners = 4;
        viewModel.UltraPipelineMaxCustomJudges = 0;

        viewModel.SaveUltraSettingsCommand.Execute(null);

        CopilotChatPaneViewModel.ReadUltraStrategy(settings).ShouldBe(UltraStrategy.Pipeline);
        CopilotChatPaneViewModel.ReadUltraPipelineMaxLoops(settings).ShouldBe(5);
        CopilotChatPaneViewModel.ReadUltraPipelineSeverityThreshold(settings).ShouldBe(TeamFindingSeverity.High);
        CopilotChatPaneViewModel.ReadUltraPipelineMaxUnderstand(settings).ShouldBe(1);
        CopilotChatPaneViewModel.ReadUltraPipelineMaxOwners(settings).ShouldBe(4);
        CopilotChatPaneViewModel.ReadUltraPipelineMaxCustomJudges(settings).ShouldBe(0);
        viewModel.StatusMessage.ShouldStartWith(
            "Ultra settings saved - pipeline: up to 5 repair rounds, findings of high severity or above keep a round from passing, "
            + "up to 1 surveyor, 4 owners and 0 extra judges; ");

        var reopened = CreateViewModel(settings);
        (reopened.UltraRunStrategy, reopened.UltraPipelineMaxLoops, reopened.UltraPipelineSeverityThreshold, reopened.UltraPipelineMaxUnderstand,
            reopened.UltraPipelineMaxOwners, reopened.UltraPipelineMaxCustomJudges).ShouldBe(("Pipeline", 5, "High", 1, 4, 0));
    }

    [Fact]
    public void ValuesOutsideTheirRange_AreClampedWhenSaved_ShownAsSaved_AndANonChoiceIsTheTeam()
    {
        var settings = NewSettings();
        var viewModel = CreateViewModel(settings);
        viewModel.UltraRunStrategy = "Sideways";
        viewModel.UltraPipelineMaxLoops = 99;
        viewModel.UltraPipelineSeverityThreshold = "Critical";
        viewModel.UltraPipelineMaxUnderstand = -2;
        viewModel.UltraPipelineMaxOwners = 0;
        viewModel.UltraPipelineMaxCustomJudges = 7;

        viewModel.SaveUltraSettingsCommand.Execute(null);

        (viewModel.UltraPipelineMaxLoops, viewModel.UltraPipelineMaxUnderstand, viewModel.UltraPipelineMaxOwners, viewModel.UltraPipelineMaxCustomJudges)
            .ShouldBe((8, 0, 1, 3));
        CopilotChatPaneViewModel.ReadUltraStrategy(settings).ShouldBe(UltraStrategy.Team);
        CopilotChatPaneViewModel.ReadUltraPipelineSeverityThreshold(settings).ShouldBe(TeamFindingSeverity.Medium);
        settings.Get(CopilotChatPaneViewModel.UltraPipelineMaxLoopsSettingKey).ShouldBe("8");
        settings.Get(CopilotChatPaneViewModel.UltraStrategySettingKey).ShouldBe("Team");
        viewModel.StatusMessage.ShouldNotContain("pipeline:"); // the team is what is saved, so it is what is said
    }

    [Fact]
    public void ASaveThatIsRefused_SavesNoneOfIt_ThePipelineSettingsIncluded()
    {
        var settings = NewSettings();
        var viewModel = CreateViewModel(settings);
        viewModel.UltraRunStrategy = "Pipeline";
        viewModel.UltraPipelineMaxLoops = 7;
        viewModel.UltraWorkFolder = "not\\a\\full\\path";

        viewModel.SaveUltraSettingsCommand.Execute(null);

        viewModel.StatusMessage.ShouldContain("Nothing was saved.");
        settings.Get(CopilotChatPaneViewModel.UltraStrategySettingKey).ShouldBeNull();
        settings.Get(CopilotChatPaneViewModel.UltraPipelineMaxLoopsSettingKey).ShouldBeNull();
    }

    [Fact]
    public void TheTeamSaysWhatItAlwaysDid_AtThePageSave()
    {
        var viewModel = CreateViewModel(NewSettings());

        viewModel.SaveUltraSettingsCommand.Execute(null);

        viewModel.StatusMessage.ShouldStartWith("Ultra settings saved - 4 workers (4 builders)");
    }
}
