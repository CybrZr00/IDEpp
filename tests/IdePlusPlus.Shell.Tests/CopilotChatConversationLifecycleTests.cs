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
/// Drives the real CopilotChatPaneViewModel through a solution switch and New Chat - the two ways a
/// conversation ends. Before these existed there was no way to start a new conversation at all, and
/// switching solution left the copilot working from the previous one: a turn still running carried
/// on against the new solution and saved itself into the new solution's history, and a solution
/// with no index of its own kept the previous one's.
///
/// Everything runs on an STA thread with a pumping Dispatcher, as in the app: the turn loop
/// marshals through the Dispatcher, and without one pumping a turn cannot make progress. The
/// OpenRouter endpoint is stubbed (see <see cref="StubCopilotHttp"/>), so a turn can be held open
/// mid-response while the solution changes under it.
/// </summary>
public sealed class CopilotChatConversationLifecycleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusConversationTests", Guid.NewGuid().ToString("N"));

    private readonly string _directoryA;
    private readonly string _directoryB;
    private readonly string _solutionA;
    private readonly string _solutionB;
    private readonly StubCopilotHttp _http = new();
    private readonly List<IDisposable> _disposables = [];
    private CodebaseIndexService _index = null!;

    public CopilotChatConversationLifecycleTests()
    {
        _directoryA = Directory.CreateDirectory(Path.Combine(_root, "A")).FullName;
        _directoryB = Directory.CreateDirectory(Path.Combine(_root, "B")).FullName;
        _solutionA = Path.Combine(_directoryA, "A.slnx");
        _solutionB = Path.Combine(_directoryB, "B.slnx");
        File.WriteAllText(_solutionA, "<Solution />");
        File.WriteAllText(_solutionB, "<Solution />");
    }

    public void Dispose()
    {
        _http.Release.TrySetResult();
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a stubborn handle must not fail the test.
        }
    }

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
        _index = new CodebaseIndexService(new LmStudioEmbeddingClient(httpClient, appSettings));

        return new CopilotChatPaneViewModel(
            new OpenRouterChatClient(httpClient, secrets),
            new LmStudioChatClient(httpClient, appSettings),
            appSettings,
            editService,
            new ProjectMemoryStore(),
            new SkillStore(),
            new ChatHistoryStore(),
            _index,
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
            new ReadOnlyToolDispatcher(fileReader, _index, roslyn, editService),
            new HookService(),
            new TemplateCatalog(),
            new TemplateInstantiationService(buildService));
    }

    private static string HistoryText(string solutionDirectory)
    {
        var path = Path.Combine(ProjectMemoryStore.MemoryDirectory(solutionDirectory), "chat-history.json");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static string[] SessionLogs(string solutionDirectory) =>
        Directory.Exists(ProjectMemoryStore.SessionsDirectory(solutionDirectory))
            ? Directory.GetFiles(ProjectMemoryStore.SessionsDirectory(solutionDirectory))
            : [];

    private static bool HasAssistantText(CopilotChatPaneViewModel viewModel, string text) =>
        viewModel.Messages.OfType<ChatMessageViewModel>().Any(m => m.Text.Contains(text));

    /// <summary>Starts a turn in solution A and returns once the stub has streamed the first part
    /// of its reply - the turn is then waiting on the rest.</summary>
    private async Task<Task> StartTurnInSolutionAAsync(CopilotChatPaneViewModel viewModel)
    {
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        viewModel.InputText = "Rename Foo in solution A";
        var send = viewModel.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => HasAssistantText(viewModel, StubCopilotHttp.FirstReplyText));
        viewModel.IsStreaming.ShouldBeTrue();
        return send;
    }

    [Fact]
    public void ATurnRunningWhenTheSolutionChanges_StopsAndSavesItselfToItsOwnSolution() => RunOnUiThread(async () =>
    {
        var viewModel = CreateViewModel();
        var send = await StartTurnInSolutionAAsync(viewModel);

        await viewModel.PrepareForSolutionChangeAsync();
        viewModel.SetSolutionContext(_solutionB, _directoryB, () => "Debug");
        await send;

        viewModel.IsStreaming.ShouldBeFalse();
        viewModel.Messages.ShouldBeEmpty();
        HistoryText(_directoryA).ShouldContain("Rename Foo in solution A");
        HistoryText(_directoryA).ShouldContain("[cancelled]");
        HistoryText(_directoryB).ShouldNotContain("Rename Foo");
    });

    /// <summary>A turn that doesn't stop when asked (a tool ignoring cancellation) is cut loose at the
    /// switch, and whatever it does afterwards reaches neither the new solution's conversation nor its
    /// saved history.</summary>
    [Fact]
    public void ATurnThatIgnoresStop_IsCutLooseAtTheSwitch_AndNothingOfItReachesTheNewSolution() => RunOnUiThread(async () =>
    {
        _http.IgnoreCancellation = true;
        var viewModel = CreateViewModel();
        var send = await StartTurnInSolutionAAsync(viewModel);

        viewModel.SetSolutionContext(_solutionB, _directoryB, () => "Debug");

        viewModel.IsStreaming.ShouldBeFalse();
        viewModel.Messages.ShouldBeEmpty();

        // Usable straight away - not only once the stuck turn finally returns, which may be never.
        viewModel.InputText = "A question for solution B";
        viewModel.SendCommand.CanExecute(null).ShouldBeTrue();
        viewModel.InputText = "";

        _http.Release.SetResult();
        await send;

        viewModel.Messages.ShouldBeEmpty();
        viewModel.IsStreaming.ShouldBeFalse();
        HistoryText(_directoryB).ShouldNotContain("Rename Foo");
        HistoryText(_directoryB).ShouldNotContain(StubCopilotHttp.LateReplyText);
        HistoryText(_directoryB).ShouldNotContain("list_files");
        HistoryText(_directoryA).ShouldContain("Rename Foo in solution A");
        HistoryText(_directoryA).ShouldNotContain(StubCopilotHttp.LateReplyText);
    });

    [Fact]
    public void SwitchingSolution_SavesTheConversationToItsOwnSolutionAndShowsTheNextOnes() => RunOnUiThread(() =>
    {
        new ChatHistoryStore().Save(_directoryB, [new StoredChatMessage("User", "Something asked in B")]);
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        viewModel.Messages.Add(new ChatMessageViewModel(ChatRole.User) { Text = "Something asked in A" });

        viewModel.SetSolutionContext(_solutionB, _directoryB, () => "Debug");

        viewModel.Messages.OfType<ChatMessageViewModel>().ShouldHaveSingleItem().Text.ShouldBe("Something asked in B");
        HistoryText(_directoryA).ShouldContain("Something asked in A");
        HistoryText(_directoryB).ShouldNotContain("Something asked in A");
        return Task.CompletedTask;
    });

    /// <summary>The shell has already moved to the new solution by the time the chat hears of it, so
    /// failing to save the previous conversation must not stop the chat moving too - it used to be
    /// left half on each.</summary>
    [Fact]
    public void SwitchingSolution_WhenThePreviousConversationCantBeSaved_StillSwitches() => RunOnUiThread(() =>
    {
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        viewModel.Messages.Add(new ChatMessageViewModel(ChatRole.User) { Text = "Something asked in A" });
        viewModel.PersistChatHistory();
        var historyA = Path.Combine(ProjectMemoryStore.MemoryDirectory(_directoryA), "chat-history.json");
        File.SetAttributes(historyA, FileAttributes.ReadOnly);
        try
        {
            viewModel.Messages.Add(new ChatMessageViewModel(ChatRole.User) { Text = "Not saved" });

            viewModel.SetSolutionContext(_solutionB, _directoryB, () => "Debug");

            viewModel.Messages.ShouldBeEmpty();
        }
        finally
        {
            File.SetAttributes(historyA, FileAttributes.Normal);
        }

        HistoryText(_directoryA).ShouldContain("Something asked in A");
        return Task.CompletedTask;
    });

    /// <summary>A saved conversation that can't be read (another process has it locked) isn't
    /// overwritten by the next save - that would lose it for good.</summary>
    [Fact]
    public void SwitchingToASolutionWhoseHistoryCantBeRead_SavesNothingOverIt() => RunOnUiThread(() =>
    {
        new ChatHistoryStore().Save(_directoryB, [new StoredChatMessage("User", "Something asked in B")]);
        var historyB = Path.Combine(ProjectMemoryStore.MemoryDirectory(_directoryB), "chat-history.json");
        var viewModel = CreateViewModel();

        using (new FileStream(historyB, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            viewModel.SetSolutionContext(_solutionB, _directoryB, () => "Debug");
        }

        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("Couldn't read");
        viewModel.Messages.Add(new ChatMessageViewModel(ChatRole.User) { Text = "Asked while it was unreadable" });
        viewModel.PersistChatHistory();

        HistoryText(_directoryB).ShouldContain("Something asked in B");
        HistoryText(_directoryB).ShouldNotContain("Asked while it was unreadable");
        return Task.CompletedTask;
    });

    /// <summary>New Project adds to the solution already open and reloads it; that must not throw
    /// away the conversation (it used to clear the pane and reload it from disk, dropping every
    /// proposal and tool card).</summary>
    [Fact]
    public void ReopeningTheSameSolution_KeepsTheConversation() => RunOnUiThread(() =>
    {
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        var message = new ChatMessageViewModel(ChatRole.User) { Text = "Still here?" };
        viewModel.Messages.Add(message);

        viewModel.SetSolutionContext(_solutionA.ToUpperInvariant(), _directoryA, () => "Release");

        viewModel.Messages.ShouldHaveSingleItem().ShouldBeSameAs(message);
        return Task.CompletedTask;
    });

    [Fact]
    public void SwitchingToASolutionWithNoIndex_LeavesNoIndexRatherThanThePreviousOne() => RunOnUiThread(() =>
    {
        // The model id the LM Studio client reports with no embedding model set up: an index saved by
        // any other model is deliberately not loaded.
        var indexA = new VectorIndex { ModelId = "lmstudio:" };
        indexA.Add(new CodeChunk(Path.Combine(_directoryA, "Foo.cs"), 1, 1, "class Foo { }") { Embedding = [1f] });
        indexA.SaveToDisk(ProjectMemoryStore.IndexFilePath(_directoryA));

        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        _index.IsIndexed.ShouldBeTrue();

        viewModel.SetSolutionContext(_solutionB, _directoryB, () => "Debug");

        _index.IsIndexed.ShouldBeFalse();
        viewModel.IndexStatus.ShouldBeNull();
        return Task.CompletedTask;
    });

    [Fact]
    public void NewConversation_SavesTheTranscriptAndStartsEmpty() => RunOnUiThread(async () =>
    {
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        viewModel.Messages.Add(new ChatMessageViewModel(ChatRole.User) { Text = "Old question" });
        viewModel.Messages.Add(new ChatMessageViewModel(ChatRole.Assistant) { Text = "Old answer" });
        viewModel.PersistChatHistory();

        await viewModel.NewConversationCommand.ExecuteAsync(null);

        viewModel.Messages.ShouldBeEmpty();
        new ChatHistoryStore().Load(_directoryA).ShouldBeEmpty();
        var log = File.ReadAllText(SessionLogs(_directoryA).ShouldHaveSingleItem());
        log.ShouldContain("**User:** Old question");
        log.ShouldContain("**Assistant:** Old answer");
        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("sessions");
    });

    [Theory]
    [InlineData("/clear")]
    [InlineData("/new")]
    [InlineData("  /CLEAR  ")]
    public void TypingAClearCommand_StartsANewConversationInsteadOfSendingIt(string typed) => RunOnUiThread(async () =>
    {
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        viewModel.Messages.Add(new ChatMessageViewModel(ChatRole.User) { Text = "Old question" });

        viewModel.InputText = typed;
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.ShouldBeEmpty();
        viewModel.InputText.ShouldBe("");
        _http.ChatRequests.ShouldBe(0);
        SessionLogs(_directoryA).ShouldHaveSingleItem();
    });

    [Theory]
    [InlineData("/clear", true)]
    [InlineData("/New", true)]
    [InlineData("/clear the cache", false)]
    [InlineData("please /clear", false)]
    [InlineData("/clearly", false)]
    [InlineData("", false)]
    public void IsNewConversationCommand_MatchesOnlyTheWholeInput(string text, bool expected) =>
        CopilotChatPaneViewModel.IsNewConversationCommand(text).ShouldBe(expected);

    [Fact]
    public void NewConversation_WhileAResponseIsRunning_StopsItFirstAndKeepsWhatItSaid() => RunOnUiThread(async () =>
    {
        var viewModel = CreateViewModel();
        var send = await StartTurnInSolutionAAsync(viewModel);
        string? askedAbout = null;
        viewModel.NewConversationConfirmation = warning =>
        {
            askedAbout = warning;
            return true;
        };

        await viewModel.NewConversationCommand.ExecuteAsync(null);
        await send;

        askedAbout.ShouldNotBeNull().ShouldContain("still running");
        viewModel.IsStreaming.ShouldBeFalse();
        viewModel.Messages.ShouldBeEmpty();
        new ChatHistoryStore().Load(_directoryA).ShouldBeEmpty();
        var log = File.ReadAllText(SessionLogs(_directoryA).ShouldHaveSingleItem());
        log.ShouldContain("Rename Foo in solution A");
        log.ShouldContain(StubCopilotHttp.FirstReplyText);
    });

    [Fact]
    public void NewConversation_Declined_KeepsEverything() => RunOnUiThread(async () =>
    {
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        var proposal = UndecidedProposal(viewModel);
        viewModel.NewConversationConfirmation = warning =>
        {
            warning.ShouldContain("1 proposed edit");
            return false;
        };

        await viewModel.NewConversationCommand.ExecuteAsync(null);

        viewModel.Messages.ShouldContain(proposal);
        proposal.IsDecided.ShouldBeFalse();
        SessionLogs(_directoryA).ShouldBeEmpty();
    });

    /// <summary>The confirmation is modal, and a solution switch already under way can finish while it
    /// is open. By then the conversation the user meant to clear has been saved and replaced; New Chat
    /// must not go on to clear the next solution's instead.</summary>
    [Fact]
    public void NewConversation_WhenASwitchFinishesDuringTheConfirmation_LeavesTheNextSolutionsConversationAlone() => RunOnUiThread(async () =>
    {
        new ChatHistoryStore().Save(_directoryB, [new StoredChatMessage("User", "Something asked in B")]);
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        UndecidedProposal(viewModel);
        viewModel.NewConversationConfirmation = _ =>
        {
            viewModel.SetSolutionContext(_solutionB, _directoryB, () => "Debug");
            return true;
        };

        await viewModel.NewConversationCommand.ExecuteAsync(null);

        viewModel.Messages.OfType<ChatMessageViewModel>().ShouldHaveSingleItem().Text.ShouldBe("Something asked in B");
        HistoryText(_directoryB).ShouldContain("Something asked in B");
        SessionLogs(_directoryB).ShouldBeEmpty();
    });

    [Fact]
    public void NewConversation_WithdrawsEditsStillWaitingForADecision() => RunOnUiThread(async () =>
    {
        var viewModel = CreateViewModel();
        viewModel.SetSolutionContext(_solutionA, _directoryA, () => "Debug");
        var proposal = UndecidedProposal(viewModel);
        viewModel.NewConversationConfirmation = _ => true;

        await viewModel.NewConversationCommand.ExecuteAsync(null);

        proposal.IsDecided.ShouldBeTrue();
        proposal.WasApplied.ShouldBeFalse();
        proposal.DecisionText.ShouldNotBeNull().ShouldContain("new conversation");
        File.ReadAllText(Path.Combine(_directoryA, "Foo.cs")).ShouldBe("a\nb\nc");
    });

    private EditProposalViewModel UndecidedProposal(CopilotChatPaneViewModel viewModel)
    {
        File.WriteAllText(Path.Combine(_directoryA, "Foo.cs"), "a\nb\nc");
        var proposal = new EditProposalViewModel(
            "call-1", new EditApplicationService(new EditorWorkspace(Path.Combine(_root, "proposal-recent.json"))));
        proposal.Complete(
            new EditProposalEvent
            {
                ToolCallId = "call-1",
                Status = EditProposalStatus.Complete,
                Operation = new EditOperation("Foo.cs", "replace_range", 2, 2, "B"),
                ArgumentsJson = "{}",
            },
            _directoryA);
        viewModel.Messages.Add(proposal);
        return proposal;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met within 10s.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Runs <paramref name="test"/> on an STA thread whose Dispatcher is pumping, the way
    /// the app's UI thread is, and rethrows whatever it failed with.</summary>
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

    /// <summary>
    /// Stands in for OpenRouter. A chat request streams <see cref="FirstReplyText"/> and then waits:
    /// until cancelled, or - with <see cref="IgnoreCancellation"/> - until <see cref="Release"/>,
    /// after which it streams <see cref="LateReplyText"/> and finishes. Anything else (the model
    /// list, embeddings) never answers: nothing here depends on it, and an answer arriving mid-test
    /// would change the pane's status line under the assertions.
    /// </summary>
    private sealed class StubCopilotHttp : HttpMessageHandler
    {
        public const string FirstReplyText = "Working on it";
        public const string LateReplyText = "Finished long after it was asked to stop";

        private int _chatRequests;

        public int ChatRequests => _chatRequests;

        public bool IgnoreCancellation { get; set; }

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _chatRequests);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingSseStream(this)) };
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        private sealed class StallingSseStream(StubCopilotHttp owner) : Stream
        {
            private static readonly byte[] FirstPart = Encoding.UTF8.GetBytes(
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{FirstReplyText}\"}}}}]}}\n\n");

            // Ends with a tool call, so a turn that carried on past a switch would go on to run a tool
            // against whichever solution is open by then and post its card into that conversation.
            private static readonly byte[] LatePart = Encoding.UTF8.GetBytes(
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\" {LateReplyText}\"}}}}]}}\n\n"
                + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_late\",\"type\":\"function\","
                + "\"function\":{\"name\":\"list_files\",\"arguments\":\"{}\"}}]}}]}\n\n"
                + "data: [DONE]\n\n");

            private int _reads;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                switch (_reads++)
                {
                    case 0:
                        FirstPart.CopyTo(buffer);
                        return FirstPart.Length;

                    case 1:
                        if (owner.IgnoreCancellation)
                        {
                            await owner.Release.Task;
                        }
                        else
                        {
                            await Task.Delay(Timeout.Infinite, cancellationToken);
                        }

                        LatePart.CopyTo(buffer);
                        return LatePart.Length;

                    default:
                        return 0;
                }
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
}
