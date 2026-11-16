using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Anthropic;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.LmStudio;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Publish;
using IdePlusPlus.Roslyn;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.SourceControl;
using IdePlusPlus.Templates;
using IdePlusPlus.TestRunner;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The conversation transcript through the real chat pane: what is recorded as the user and the model
/// talk, that it outlives New Chat and a restart (the reason it exists), and that the model can get
/// it back with search_transcripts once the conversation it came from is gone.
/// </summary>
public sealed class CopilotChatTranscriptTests : IDisposable
{
    private const string Done = "data: [DONE]\n\n";
    private const string Stop = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" + Done;

    private static string Reasoning(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"reasoning\":\"{text}\"}}}}]}}\n\n";

    private static string Text(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\n";

    private static string ToolCall(string name, string argumentsJson) =>
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\","
        + "\"function\":{\"name\":\"" + name + "\",\"arguments\":" + JsonSerializer.Serialize(argumentsJson) + "}}]}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" + Done;

    private static string SearchCall(string argumentsJson) => ToolCall("search_transcripts", argumentsJson);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusTranscriptTests", Guid.NewGuid().ToString("N"));

    private readonly string _directory;
    private readonly string _solution;
    private readonly ScriptedChatHttp _http = new();
    private readonly List<IDisposable> _disposables = [];

    public CopilotChatTranscriptTests()
    {
        _directory = Directory.CreateDirectory(Path.Combine(_root, "S")).FullName;
        _solution = Path.Combine(_directory, "S.slnx");
        File.WriteAllText(_solution, "<Solution />");
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a stubborn handle must not fail the test.
        }
    }

    private IReadOnlyList<TranscriptEntry> Entries() => new TranscriptStore().LoadAll(_directory);

    private string[] ConversationIds() => Entries().Select(e => e.ConversationId).Distinct().ToArray();

    [Fact]
    public void WhatTheUserSaid_AndTheRepliesThoughtAndText_AreAllRecorded() => RunOnUiThread(async () =>
    {
        _http.Respond = _ => Body(Reasoning("The cache key must include the tenant id") + Text("Because tenants share a process") + Stop);
        var viewModel = CreateViewModel();

        await Ask(viewModel, "Why is the cache keyed that way?");

        var entries = Entries();
        entries.Select(e => e.Kind).ShouldBe([TranscriptKinds.User, TranscriptKinds.Thought, TranscriptKinds.Assistant]);
        entries.Select(e => e.Text).ShouldBe(
        [
            "Why is the cache keyed that way?",
            "The cache key must include the tenant id",
            "Because tenants share a process",
        ]);
        ConversationIds().ShouldHaveSingleItem();
    });

    [Fact]
    public void TheThought_IsInTheTranscript_EvenThoughTheSavedChatHistoryNeverHadIt() => RunOnUiThread(async () =>
    {
        _http.Respond = _ => Body(Reasoning("A thought that chat-history.json drops") + Text("An answer") + Stop);
        var viewModel = CreateViewModel();

        await Ask(viewModel, "Question");

        File.ReadAllText(Path.Combine(ProjectMemoryStore.MemoryDirectory(_directory), "chat-history.json"))
            .ShouldNotContain("A thought that chat-history.json drops");
        Entries().ShouldContain(e => e.Kind == TranscriptKinds.Thought && e.Text == "A thought that chat-history.json drops");
    });

    [Fact]
    public void NewChat_ClearsTheConversation_ButKeepsItsTranscript_AndTheNextOneGetsItsOwn() => RunOnUiThread(async () =>
    {
        _http.Respond = n => Body(Reasoning($"Thought {n}") + Text($"Answer {n}") + Stop);
        var viewModel = CreateViewModel();

        await Ask(viewModel, "First question");
        await viewModel.NewConversationCommand.ExecuteAsync(null);
        viewModel.Messages.ShouldBeEmpty();
        await Ask(viewModel, "Second question");

        var conversations = Entries().GroupBy(e => e.ConversationId).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        conversations.Count.ShouldBe(2);
        conversations[0].Select(e => e.Text).ShouldBe(["First question", "Thought 1", "Answer 1"]);
        conversations[1].Select(e => e.Text).ShouldBe(["Second question", "Thought 2", "Answer 2"]);
    });

    [Fact]
    public void ARestartedPane_CarriesOnTheConversationThatWasOpen_RatherThanStartingAnother() => RunOnUiThread(async () =>
    {
        _http.Respond = n => Body(Text($"Answer {n}") + Stop);
        var before = CreateViewModel();
        await Ask(before, "Before the restart");

        var after = CreateViewModel();
        await Ask(after, "After the restart");

        after.Messages.OfType<ChatMessageViewModel>().Select(m => m.Text).ShouldContain("Before the restart");
        ConversationIds().ShouldHaveSingleItem();
        Entries().Select(e => e.Text).ShouldBe(["Before the restart", "Answer 1", "After the restart", "Answer 2"]);
    });

    [Fact]
    public void AReplyStoppedPartWay_KeepsWhatTheModelHadSaid_WithoutTheIdesOwnNote() => RunOnUiThread(async () =>
    {
        _http.Respond = _ => new StreamContent(new HeldOpenSseStream(Reasoning("Half a thought") + Text("Half an answer")));
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solution, _directory, () => "Debug");
        viewModel.InputText = "Do the thing";

        var send = viewModel.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.Messages.OfType<ChatMessageViewModel>().Any(m => m.Text.Contains("Half an answer")));
        viewModel.CancelStreamingCommand.Execute(null);
        await send;

        var entries = Entries();
        entries.ShouldContain(e => e.Kind == TranscriptKinds.Thought && e.Text == "Half a thought");
        entries.ShouldContain(e => e.Kind == TranscriptKinds.Assistant && e.Text == "Half an answer");
        entries.ShouldNotContain(e => e.Text.Contains("[cancelled]"));
        viewModel.Messages.OfType<ChatMessageViewModel>().Any(m => m.Text.Contains("[cancelled]")).ShouldBeTrue("the bubble still says so");
    });

    [Fact]
    public void TheModel_CanSearchAnEarlierConversationsThought_AfterNewChatHasClearedIt() => RunOnUiThread(async () =>
    {
        _http.Respond = n => n switch
        {
            1 => Body(Reasoning("The cache key must include the tenant id") + Text("Done") + Stop),
            2 => Body(SearchCall("{\"query\":\"tenant cache key\",\"kind\":\"thought\"}")),
            _ => Body(Text("I found it") + Stop),
        };
        var viewModel = CreateViewModel();
        await Ask(viewModel, "Why is the cache keyed that way?");
        await viewModel.NewConversationCommand.ExecuteAsync(null);

        await Ask(viewModel, "What did we decide about the cache key?");

        _http.ChatRequests.ShouldBe(3);
        // The third request carries the search tool's result back to the model.
        _http.LastRequestBody.ShouldContain("assistant thought");
        _http.LastRequestBody.ShouldContain("The cache key must include the tenant id");
        // The question asked for thoughts only, so the user's own messages are not in the answer.
        _http.LastRequestBody.ShouldNotContain("user message");
    });

    [Fact]
    public void WithNoEmbeddingModel_TheSearchSaysItWasByKeywordOnly() => RunOnUiThread(async () =>
    {
        _http.Respond = n => n switch
        {
            1 => Body(Text("The tenant id goes in the cache key") + Stop),
            2 => Body(SearchCall("{\"query\":\"tenant\"}")),
            _ => Body(Text("ok") + Stop),
        };
        var viewModel = CreateViewModel();
        await Ask(viewModel, "Where does the tenant id go?");
        await viewModel.NewConversationCommand.ExecuteAsync(null);

        await Ask(viewModel, "Remind me");

        _http.LastRequestBody.ShouldContain("keyword matches only");
    });

    [Fact]
    public void ASearchThatMatchesNothing_SaysSoPlainly_AndIsNotAnError() => RunOnUiThread(async () =>
    {
        _http.Respond = n => n == 1 ? Body(SearchCall("{\"query\":\"zzzunmatchable\"}")) : Body(Text("ok") + Stop);
        var viewModel = CreateViewModel();

        await Ask(viewModel, "Hello");

        var card = viewModel.Messages.OfType<McpToolCallViewModel>().ShouldHaveSingleItem();
        card.IsError.ShouldBeFalse();
        card.ResultText.ShouldNotBeNull().ShouldContain("No matches in the saved conversation transcripts");
    });

    [Fact]
    public void ASearchCanReachTheCurrentConversation_AndSaysWhichOneItCameFrom() => RunOnUiThread(async () =>
    {
        _http.Respond = n => n == 1 ? Body(SearchCall("{\"query\":\"Hello\"}")) : Body(Text("ok") + Stop);
        var viewModel = CreateViewModel();

        await Ask(viewModel, "Hello there");

        viewModel.Messages.OfType<McpToolCallViewModel>().ShouldHaveSingleItem()
            .ResultText.ShouldNotBeNull().ShouldContain("user message | this conversation");
    });

    [Fact]
    public void ASearchWhoseArgumentsAreTheWrongType_DoesNotEndTheTurn() => RunOnUiThread(async () =>
    {
        _http.Respond = n => n == 1
            ? Body(SearchCall("{\"query\":\"hello\",\"limit\":\"3\",\"kind\":7}"))
            : Body(Text("ok") + Stop);
        var viewModel = CreateViewModel();

        await Ask(viewModel, "Hello");

        _http.ChatRequests.ShouldBe(2);
        viewModel.Messages.OfType<ChatMessageViewModel>().Select(m => m.Text).ShouldContain("ok");
    });

    [Fact]
    public void ASubAgentsTaskThoughtsAndReply_AreKeptInTheConversationThatStartedIt() => RunOnUiThread(async () =>
    {
        _http.Respond = n => n switch
        {
            1 => Body(ToolCall("investigate_parallel", "{\"tasks\":[\"Look at the widget\"]}")),
            2 => Body(Reasoning("The widget lives in Widget.cs") + Text("It is a widget.") + Stop),
            _ => Body(Text("Summarised") + Stop),
        };
        var viewModel = CreateViewModel();

        await Ask(viewModel, "Investigate the widget");

        var entries = Entries();
        var subAgent = entries.Where(e => e.Source == "sub-agent 1").ToList();
        subAgent.Select(e => (e.Kind, e.Text)).ShouldBe(
        [
            (TranscriptKinds.User, "Look at the widget"),
            (TranscriptKinds.Thought, "The widget lives in Widget.cs"),
            (TranscriptKinds.Assistant, "It is a widget."),
        ]);
        ConversationIds().ShouldHaveSingleItem("the sub-agent belongs to the conversation that started it");
        entries.Where(e => e.Source is null).Select(e => e.Text).ShouldContain("Investigate the widget");
    });

    [Fact]
    public void WithoutATranscriptService_TheConversationWorksAsBefore_AndNothingIsWritten() => RunOnUiThread(async () =>
    {
        _http.Respond = _ => Body(Reasoning("t") + Text("Answer") + Stop);
        var viewModel = CreateViewModel(withTranscripts: false);

        await Ask(viewModel, "Question");

        viewModel.Messages.OfType<ChatMessageViewModel>().Select(m => m.Text).ShouldContain("Answer");
        Directory.Exists(TranscriptStore.TranscriptsDirectory(_directory)).ShouldBeFalse();
    });

    private static HttpContent Body(string sse) => new StringContent(sse, Encoding.UTF8, "text/event-stream");

    private async Task Ask(CopilotChatPaneViewModel viewModel, string text)
    {
        viewModel.SetSolutionContext(_solution, _directory, () => "Debug");
        viewModel.InputText = text;
        await viewModel.SendCommand.ExecuteAsync(null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition never became true");
            await Task.Delay(20);
        }
    }

    private CopilotChatPaneViewModel CreateViewModel(bool withTranscripts = true)
    {
        SolutionLoader.EnsureMSBuildRegistered();

        var appSettings = new AppSettingsStore(Path.Combine(_root, "settings.json"));
        var secrets = new SecureSettingsStore(Path.Combine(_root, "secrets.json"));
        secrets.SetSecret(OpenRouterChatClient.ApiKeySettingName, "test-key");

        var httpClient = new HttpClient(_http) { Timeout = Timeout.InfiniteTimeSpan };
        _disposables.Add(httpClient);

        var editService = new EditApplicationService(new EditorWorkspace(Path.Combine(_root, "recent-files.json")));
        var buildService = new BuildService();
        var roslyn = new RoslynWorkspaceService();
        _disposables.Add(roslyn);
        var fileReader = new WorkspaceFileReader();
        var index = new CodebaseIndexService(new LmStudioEmbeddingClient(httpClient, appSettings, secrets));

        return new CopilotChatPaneViewModel(
            new OpenRouterChatClient(httpClient, secrets),
            new LmStudioChatClient(httpClient, appSettings, secrets),
            new DeepSeekChatClient(httpClient, secrets),
            new AnthropicChatClient(httpClient, secrets),
            appSettings,
            editService,
            new ProjectMemoryStore(),
            new SkillStore(),
            new ChatHistoryStore(),
            index,
            new McpServerManager(
                new McpServerConfigStore(Path.Combine(_root, "mcp-servers.json")),
                new McpProjectTrustStore(Path.Combine(_root, "mcp-trust.json"))),
            new PluginChatToolRegistry(),
            new GitService(),
            buildService,
            new PublishService(buildService),
            new TestRunService(),
            roslyn,
            fileReader,
            new ReadOnlyToolDispatcher(fileReader, index, roslyn, editService),
            new HookService(),
            new TemplateCatalog(),
            new TemplateInstantiationService(buildService),
            withTranscripts
                ? new TranscriptMemoryService(new UnavailableEmbeddingClient(), new TranscriptStore(), TimeSpan.FromHours(1))
                : null);
    }

    private static void RunOnUiThread(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await test();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        thread.Join(TimeSpan.FromSeconds(60)).ShouldBeTrue("the test's UI thread did not finish within 60s - looks hung.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Stands in for OpenRouter: the Nth chat request is answered with whatever
    /// <see cref="Respond"/> returns for N (1-based). Anything else never answers.</summary>
    private sealed class ScriptedChatHttp : HttpMessageHandler
    {
        private int _chatRequests;

        public Func<int, HttpContent> Respond { get; set; } = _ => Body(Stop);

        public int ChatRequests => _chatRequests;

        /// <summary>The body of the most recent chat request - what the model was actually sent.</summary>
        public string LastRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref _chatRequests);
                LastRequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = Respond(n) };
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Sends its first part and then stays open until the reader gives up.</summary>
    private sealed class HeldOpenSseStream(string firstPart) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(firstPart);
        private int _reads;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ == 0)
            {
                _first.CopyTo(buffer);
                return _first.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
