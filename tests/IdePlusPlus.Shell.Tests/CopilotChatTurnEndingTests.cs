using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Threading;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
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
/// How a turn ends when something other than "the model finished" ends it. Each of these used to stop
/// the assistant with nothing said (an empty reply, the step limit, a dead connection) or with the wrong
/// thing said (an HTTP timeout shown as "[cancelled]"), which from the chair looks like the copilot
/// getting stuck or giving up for no reason.
/// </summary>
public sealed class CopilotChatTurnEndingTests : IDisposable
{
    private const string Done = "data: [DONE]\n\n";
    private const string Stop = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" + Done;

    private static string Text(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\n";

    private static string ToolCall(int n) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"call_{n}\",\"type\":\"function\","
        + "\"function\":{\"name\":\"list_files\",\"arguments\":\"{}\"}}]}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" + Done;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusTurnEndingTests", Guid.NewGuid().ToString("N"));

    private readonly string _directory;
    private readonly string _solution;
    private readonly ScriptedChatHttp _http = new();
    private readonly List<IDisposable> _disposables = [];

    public CopilotChatTurnEndingTests()
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

    [Fact]
    public void AnEmptyReply_IsRetriedOnce_AndTheRetrysAnswerIsUsed() => RunOnUiThread(async () =>
    {
        _http.Script = n => n == 1 ? Stop : Text("Here is the answer") + Stop;
        var viewModel = CreateViewModel();

        await Ask(viewModel);

        _http.ChatRequests.ShouldBe(2);
        Transcript(viewModel).ShouldContain("Here is the answer");
        Transcript(viewModel).ShouldNotContain("empty reply");
    });

    [Fact]
    public void AReplyThatStaysEmpty_SaysSo_InsteadOfLeavingABlankBubble() => RunOnUiThread(async () =>
    {
        _http.Script = _ => Stop;
        var viewModel = CreateViewModel();

        await Ask(viewModel);

        _http.ChatRequests.ShouldBe(2, "retried once, not in a loop");
        Transcript(viewModel).ShouldContain("empty reply");
        viewModel.IsStreaming.ShouldBeFalse();
    });

    [Fact]
    public void RunningOutOfSteps_SaysSo_AndHowToCarryOn() => RunOnUiThread(async () =>
    {
        _http.Script = n => ToolCall(n);
        var viewModel = CreateViewModel();

        await Ask(viewModel);

        _http.ChatRequests.ShouldBe(40, "the manual-mode step limit");
        Transcript(viewModel).ShouldContain("paused after 40 steps");
        Transcript(viewModel).ShouldContain("continue");
        viewModel.IsStreaming.ShouldBeFalse();
    });

    [Fact]
    public void AReplyThatFinishesNormally_GetsNoWarning() => RunOnUiThread(async () =>
    {
        _http.Script = _ => Text("All done") + Stop;
        var viewModel = CreateViewModel();

        await Ask(viewModel);

        _http.ChatRequests.ShouldBe(1);
        Transcript(viewModel).ShouldNotContain("paused");
        Transcript(viewModel).ShouldNotContain("empty reply");
        Transcript(viewModel).ShouldNotContain("[error");
    });

    [Fact]
    public void ADroppedConnection_IsReported_NotTreatedAsAFinishedReply() => RunOnUiThread(async () =>
    {
        _http.Script = _ => Text("Starting to answer"); // no finish_reason, no [DONE]
        var viewModel = CreateViewModel();

        await Ask(viewModel);

        Transcript(viewModel).ShouldContain("Starting to answer");
        Transcript(viewModel).ShouldContain("connection closed before the model finished");
    });

    [Fact]
    public void AnHttpTimeout_IsReportedAsATimeout_NotAsCancelled() => RunOnUiThread(async () =>
    {
        _http.TimeOutRequests = true;
        var viewModel = CreateViewModel();

        await Ask(viewModel);

        Transcript(viewModel).ShouldNotContain("[cancelled]");
        Transcript(viewModel).ShouldContain("timed out");
    });

    private async Task Ask(CopilotChatPaneViewModel viewModel)
    {
        viewModel.SetSolutionContext(_solution, _directory, () => "Debug");
        viewModel.InputText = "Do the thing";
        await viewModel.SendCommand.ExecuteAsync(null);
    }

    private static string Transcript(CopilotChatPaneViewModel viewModel) =>
        string.Join("\n", viewModel.Messages.OfType<ChatMessageViewModel>().Select(m => m.Text));

    private CopilotChatPaneViewModel CreateViewModel()
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
        var index = new CodebaseIndexService(new LmStudioEmbeddingClient(httpClient, appSettings));

        return new CopilotChatPaneViewModel(
            new OpenRouterChatClient(httpClient, secrets),
            new LmStudioChatClient(httpClient, appSettings),
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
            new TemplateInstantiationService(buildService));
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

    /// <summary>Stands in for OpenRouter: the Nth chat request gets whatever <see cref="Script"/> returns
    /// for N (1-based) as its whole SSE body. Anything else never answers.</summary>
    private sealed class ScriptedChatHttp : HttpMessageHandler
    {
        private int _chatRequests;

        public Func<int, string> Script { get; set; } = _ => Stop;

        public bool TimeOutRequests { get; set; }

        public int ChatRequests => _chatRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref _chatRequests);
                if (TimeOutRequests)
                {
                    // What HttpClient.Timeout looks like from the caller's side.
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Script(n), Encoding.UTF8, "text/event-stream"),
                };
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
