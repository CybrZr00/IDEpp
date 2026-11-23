using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The Ultra team's thoughts and replies in the transcript: the lead's, labelled with what it was
/// doing, and each worker's, labelled with its name.
/// </summary>
public sealed class UltraTranscriptTests : IDisposable
{
    private readonly TeamRepositoryFixture _repo = new();
    private readonly ScriptedTeamChatClient _client = new();
    private readonly TranscriptStore _store = new();
    private readonly TranscriptMemoryService _service;
    private readonly string _solution;

    public UltraTranscriptTests()
    {
        _solution = Path.Combine(_repo.Root, "solution");
        Directory.CreateDirectory(_solution);
        _service = new TranscriptMemoryService(new UnavailableEmbeddingClient(), _store, TimeSpan.FromHours(1));
        _service.SetSolution(_solution);
    }

    public void Dispose() => _repo.Dispose();

    private TranscriptRecorder Recorder() => new(_service, _solution, _service.StartConversation()!, "ultra team");

    private static ChatStreamEvent[] Thinking(string thought, string reply) => [new ReasoningDelta(thought), new TextDelta(reply)];

    [Fact]
    public async Task TheLeadsReview_IsKeptUnderTheWorkerItWasReviewing()
    {
        _client.Script("lead", Thinking("The diff only touches Class1.", "Looks fine to me."));
        var lead = new UltraOrchestrator(
            _client, "lead-model", ChatEffort.High, _repo.ReadOnlyTools, _repo.RepositoryRoot,
            new TeamBoard(TimeProvider.System, TimeSpan.FromMinutes(10)),
            new TeamMessageBus(TimeProvider.System, ["w1", LeadTools.LeadName]),
            [new TeamWorkerState("w1")], TimeProvider.System,
            transcript: Recorder());

        await lead.ReviewMergeAsync("goal", "w1", "diff", CancellationToken.None);

        _store.LoadAll(_solution).Select(e => (e.Source, e.Kind, e.Text)).ShouldBe(
        [
            ("ultra lead: reviewing w1's merge", TranscriptKinds.Thought, "The diff only touches Class1."),
            ("ultra lead: reviewing w1's merge", TranscriptKinds.Assistant, "Looks fine to me."),
        ]);
    }

    [Fact]
    public async Task AWholeRun_KeepsTheLeadsPlanningAndEachWorkersThoughts_UnderTheirOwnLabels()
    {
        ChatStreamEvent[] workerReply =
            [new ReasoningDelta("Nothing here needs changing."), .. _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" })];
        _client
            .Script("lead",
                [new ReasoningDelta("Two small steps."), .. _client.Call(LeadTools.SubmitPlanToolName, new { tasks = new[] { "Do it" } })],
                Thinking("", "Planned."))
            .Script("w1", workerReply);
        var run = new UltraTeamRun(
            _client,
            "chat-model",
            new UltraTeamOptions(1, TimeSpan.FromMinutes(10), ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
            {
                StaggerDelay = TimeSpan.Zero,
                HardStopGrace = TimeSpan.FromSeconds(5),
                PreflightBuild = false,
                Orchestrated = true,
                SuperviseInterval = TimeSpan.FromHours(1),
                Transcript = Recorder(),
            },
            _repo.ReadOnlyTools,
            new GitService(),
            _repo.TeamGit,
            TimeProvider.System,
            Path.Combine(_repo.Root, "ultra"),
            runCommand: null);

        await run.RunAsync(
            "Make Class1.Value return 2.",
            _repo.RepositoryRoot,
            Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
            [],
            CancellationToken.None);

        var entries = _store.LoadAll(_solution);
        entries.ShouldContain(e => e.Source == "ultra lead: planning" && e.Kind == TranscriptKinds.Thought && e.Text == "Two small steps.");
        entries.ShouldContain(e => e.Source == "ultra lead: planning" && e.Kind == TranscriptKinds.Assistant && e.Text == "Planned.");
        entries.ShouldContain(e => e.Source == "ultra worker w1" && e.Kind == TranscriptKinds.Thought && e.Text == "Nothing here needs changing.");
        entries.Select(e => e.ConversationId).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task WithNoRecorder_ARunWorksAsBefore()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));
        var run = new UltraTeamRun(
            _client, "chat-model",
            new UltraTeamOptions(1, TimeSpan.FromMinutes(10), ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
            {
                StaggerDelay = TimeSpan.Zero,
                PreflightBuild = false,
            },
            _repo.ReadOnlyTools, new GitService(), _repo.TeamGit, TimeProvider.System, Path.Combine(_repo.Root, "ultra"), runCommand: null);

        await run.RunAsync(
            "Make Class1.Value return 2.",
            _repo.RepositoryRoot,
            Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
            [],
            CancellationToken.None);

        _store.LoadAll(_solution).ShouldBeEmpty();
    }
}
