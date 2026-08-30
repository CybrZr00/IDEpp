# IDE++ Feature Roadmap

Produced by reading the codebase directly (chat/agent loop, edit-proposal flow, plugin contracts,
shell/menus, settings, DI composition root) plus a five-way parallel survey of the remaining
subsystems (Editor, Build/Run/Debug/ProjectSystem, the Copilot ecosystem, Infrastructure/Plugins/Tests,
Shell UX). Every item below is grounded in an actual file, not generic IDE advice.

**Execution status** (this plan is being executed autonomously in-repo; updated as milestones land):
- [x] Reliability & safety net (Part 2) — global exception handler, Serilog wiring, plugin crash guards,
      missing-file/save-cancel/ghost-text bug fixes, secrets pruning, `.bak` backup, new
      `IdePlusPlus.Infrastructure.Tests` project.
- [x] Effort control (Part 1.3) — `ChatEffort` threaded through `IChatClient`/OpenRouter/LM Studio as an
      OpenRouter `reasoning.effort` field, persisted UI picker.
- [x] Auto Mode (Part 1.4) — edit proposals auto-accept when on; tool-loop cap raised to 30 iterations.
- [x] Plan Mode (Part 1.2) — `propose_plan` tool gates `propose_edit` behind an approved checklist.
- [x] Git integration MVP (Part 1.1) — new `IdePlusPlus.SourceControl` project (shells to `git.exe`),
      status/stage/commit/push/pull/fetch/branch-switch, a Git tool pane reusing the existing
      `DiffComputer`, a status-bar branch indicator, and a `_Git` menu.
- [x] Build/Debug maturity — Debug/Release configuration switcher (status bar + threaded through
      Build/Run/the debug-DLL search path), Clean/Rebuild commands, DAP `setExceptionBreakpoints`,
      and a new Error List pane (parses MSBuild diagnostics, double-click navigates to the file) with
      its own `BuildDiagnosticParser` + `IdePlusPlus.Build.Tests` project (previously zero coverage).
- [x] Shell polish batch — Recent Solutions list + reopen-last-solution-on-startup, window title bound
      to solution/document, window position/size/maximized-state persistence, an Edit menu (Find/Replace
      via AvalonEdit's existing SearchPanel, plus a new Go to Line dialog with a `GoToLineRequest`
      pub/sub bridge mirroring the existing `EditorServices` pattern), Save All/Close All (routed through
      the existing dirty-document confirmation, not bypassing it), solution-aware drag-and-drop, a
      minimal About dialog, and filename-with-full-path-tooltip Recent Files/Solutions menus.
- [x] Copilot-ecosystem depth — chat history now persists per-solution and reloads on reopen
      (`ChatHistoryStore`), a user-editable system prompt lives in Settings, a conversation-length
      warning nudges toward Save Memory before hitting a provider's context limit, MCP servers now
      support project-local discovery (`.ideplusplus/copilot/mcp-servers.json`, overriding/adding to
      the global list), token usage is tracked and shown per session (`stream_options.include_usage`),
      and the diff viewer now highlights the specific word-level change within a modified line
      (via `SideBySideDiffBuilder` + a word chunker, reused by both the AI edit-proposal diff and the
      new Git diff view). Not done: multi-file `propose_edit` (atomicity across files is a real design
      question, not just plumbing) and MCP resources/prompts support (tools-only remains as-is).
- [x] Editor quick wins — breakpoints now persist across restarts (`BreakpointManager` gained the same
      constructor-injectable-path pattern as `AppSettingsStore`/`SecureSettingsStore`), DeepSeek FIM
      failures are logged via Serilog instead of failing silently, a "Test Connection" button in
      Settings makes a real (tiny) completion request, and word-wrap/zoom (`View` menu, Ctrl+=/Ctrl+-
      /Ctrl+0, Ctrl+MouseWheel) mirror the existing `EditorTheme` static-broadcast pattern via a new
      `EditorSettings`. Added `IdePlusPlus.Editor.Tests` (previously zero coverage) for
      `DocumentViewModel`/`EditorWorkspace`/`BreakpointManager`/`BraceFoldingStrategy` (the last given a
      small refactor to separate its pure fold-region computation from the AvalonEdit `FoldingManager`
      call, for testability) - writing those tests caught and fixed a real bug: `ForNewFile`'s `Title`
      always showed the literal string "Untitled" regardless of the actual untitled name passed in
      (e.g. "Untitled2"), even though `ContentId` used it correctly, so multiple new files were
      indistinguishable in their tab titles.
- [x] Read-only workspace tools (`read_file`, `list_files`, `search_codebase`) — added in response to a
      real Plan Mode failure found in live use: the Plan Mode prompt instructed the model to
      "read files, search, use read-only tools", but IDE++ exposed **no file-access tool at all** (file
      context only ever arrived as pre-injected system messages). A capable model obediently reached for
      the only exploration-shaped tools it had — the user's Playwright MCP server — and browsed in
      circles until the tool-iteration cap. Fixed by actually providing the tools, correcting the prompt
      to name them and steer off browser/docs tools for first-party code, and ending the turn when a
      proposal is produced (previously a `propose_plan` emitted in the same turn as a tool call was
      dropped from history entirely, so the model couldn't see it had already proposed). `read_file`
      enforces solution-root containment, since the *model* chooses the path.
- [x] Eager chat-history flush — history previously persisted only when a whole assistant turn
      finished, so a crash, kill, or app close during a long agentic turn lost the entire conversation,
      defeating the feature's purpose. Now checkpoints on each user message, after each tool-loop
      iteration, and on window close (per-message/per-iteration, never per-token).
- [x] Auto Mode "stuck after one edit" bug, found in live use immediately after the Plan Mode fix
      above: raising `AutoModeMaxToolLoopIterations` to 30 had been inert for the common case,
      because nothing ever made the loop *continue* past a proposal Auto Mode had already
      auto-accepted - the "a proposal ends the turn" rule from the Plan Mode fix applied
      unconditionally, so even a successfully-applied auto-accepted edit still stopped the
      conversation cold, with no summary and no attempt at the rest of the task. Fixed by continuing
      the loop (with a synthetic note, since nothing else records that the edit landed) exactly when
      Auto Mode resolved every edit proposed in that turn - a plan proposal still always stops the
      turn regardless of Auto Mode, and manual mode is unchanged. Given this exact control flow has
      now caused two live bugs in one session, the decision itself was pulled out into a pure
      `AssistantTurnPolicy.Decide` and given its own `IdePlusPlus.Shell.Tests` project (Shell had zero
      coverage until now) covering every combination of tool-calls/edit-proposals/plan-proposal/
      Auto-Mode state.
- [x] Reasoning trace, activity indicator, and immediate error surfacing — requested after a live
      session where the assistant stopped mid-task with no visible sign of why. Three changes:
      (1) the OpenRouter-normalized `reasoning`/DeepSeek's `reasoning_content` SSE field is now parsed
      into a `ReasoningDelta` and shown in a collapsed-by-default "💭 Reasoning" `Expander` on each
      assistant bubble - the app already requests this via `ChatEffort` but never surfaced it;
      (2) an indeterminate spinner plus a live `CurrentActivity` string ("Thinking...", "Calling
      read_file...") is visible for the whole duration of a turn, including a multi-iteration tool
      loop; (3) `RunAssistantTurnsAsync` now catches every exception, not just cancellation, logging
      immediately via Serilog and appending a visible error to the chat rather than relying on
      `TaskScheduler.UnobservedTaskException`, which only fires once a fire-and-forget
      `AsyncRelayCommand`'s faulted Task happens to be garbage-collected - arbitrarily delayed
      relative to when the conversation actually stopped, and exactly why the prior investigation
      found a truncated final message but zero log output. Whether (3) is what actually happened
      in the reported session is unconfirmed (no log file existed to check), but it closes a real
      gap either way.
- [x] "Bigger bets" (Roslyn IntelliSense/diagnostics, NuGet management, Test Explorer, multi-project
      debug/attach, plugin API v2 + sandboxing) — each a multi-week initiative in its own right, all
      five now shipped. Grounded, file-level designs for all five (produced by a 5-way parallel
      codebase survey plus a cross-check synthesis for conflicts/shared-infra/hidden dependencies)
      live in [bigger-bets-designs.md](bigger-bets-designs.md). Implementation order, per the
      synthesis (tractability and value-per-risk first, riskiest/most novel last; no feature's MVP
      hard-blocked on another's): **1. NuGet package management → 2. Multi-project debug/attach →
      3. Test Explorer → 4. Roslyn IntelliSense/diagnostics → 5. Plugin API v2 + sandboxing.**
      Each landed one at a time with full build/test/launch verification (and, for the two
      highest-risk items, real end-to-end verification beyond "it compiled" — see items 4 and 5
      below) before the next started.
  - [x] **1. NuGet package management** — new `IdePlusPlus.NuGet` project (`NuGetService` shells to
        `dotnet.exe` for list/add/remove/restore, mirroring `GitService`'s `ArgumentList`-only
        process-spawn pattern with `BuildService`-style streamed output for the slow operations;
        `NuGetSearchClient` queries nuget.org's public v3 search API through the app's existing
        shared `HttpClient` with an 8s timeout), a new docked NuGet tool pane (opened per-project via
        a new "Manage NuGet Packages..." Solution Explorer context-menu item, alongside — not
        replacing — "Set as Startup Project"), an Add Package dialog, and a default-on
        "Restore NuGet packages on solution load" setting. Every mutation re-runs `dotnet list
        package` from disk rather than trusting an optimistic in-memory update, since this repo's
        Central Package Management setup means `dotnet add/remove` mutates the solution-wide
        `Directory.Packages.props`, not just the target project's `.csproj`. New
        `IdePlusPlus.NuGet.Tests` project (14 tests, real integration tests against `dotnet.exe` and
        a scratch classlib project — not mocks, matching this repo's existing test philosophy) plus
        unit tests for the search client's error/timeout handling via a fake `HttpMessageHandler`.
        Verified: full-solution build (0 warnings/errors), full test suite (179 tests across 15
        projects, all passing), and a real launch smoke test. No existing file's behavior changed
        beyond the named integration points (`GitService`/`BuildService` untouched).
  - [x] **2. Multi-project debug/attach** — `DebugSession`'s singleton DI registration replaced with
        a `DebugSessionManager` owning zero-or-more concurrent `DebugSession` instances (each keeps
        its own `DapClient`/netcoredbg process — confirmed via `DapClient`'s per-instance `seq`
        dictionary that this needed zero protocol changes to run concurrently). Added `DebugSession
        .AttachAsync` sending DAP `attach` instead of `launch`, a `ProcessAttachTarget` heuristic
        (`coreclr.dll`/`hostfxr.dll` sibling, or apphost + matching `.deps.json`) behind an
        "Attach to Process..." Debug-menu item and dialog, a session picker (`ComboBox`) shown above
        both the Call Stack and Locals panes (which stay simple single-collection views per the
        design's MVP scope — `MainWindowViewModel` keeps its own `DebugSessions` mirror, synced only
        on the UI thread via `_dispatcher.Invoke`, since `DebugSessionManager`'s own events fire on
        the DAP reader's background thread and a WPF-bound `ObservableCollection` cannot be mutated
        off-thread), and a new `StartupProjectsStore` persisting a project-path list (migrating
        transparently from the old single-path `startup-project.txt`) surfaced as an additive
        "Include in Startup Projects" checkbox alongside the existing single-target "Set as Startup
        Project". A real ordering bug was caught during implementation and fixed before landing: a
        newly started session's own `StateChanged` event reaches `MainWindowViewModel` *before*
        `DebugSessionManager` finishes adding it to `Sessions`/marking it active, which would have
        left `IsDebugging`/`IsStoppedAtBreakpoint` stuck false and Stop/Continue/Step permanently
        disabled after every debug launch — fixed by also recomputing debug state on
        `SessionsChanged`, not just `StateChanged`. The single-startup-project case (today's only
        case) is an explicit behavioral no-op: with no saved multi-project state, the new
        `_startupProjectPaths` list resolves to exactly the one existing runnable project, so
        `StartDebuggingAsync`'s loop executes once with identical Output text and command-enablement
        transitions as before. New `IdePlusPlus.Debug.Tests` project (6 tests for the attach
        heuristic against a real temp-directory filesystem, not mocks) plus 4 new
        `StartupProjectsStore` migration tests. `DebugSessionManager`'s own orchestration logic
        (active-session bookkeeping) is left deliberately uncovered — `DebugSession`'s constructor
        transitively requires a live `System.Windows.Application.Current`, which does not exist in a
        plain xunit host, so exercising it needs a real (unavailable in this environment) netcoredbg
        process rather than a construction-only test double. Verified: full-solution build (0
        warnings/errors), full test suite (193 tests across 16 projects, all passing), and a real
        launch smoke test with the new `DebugSessionManager` DI wiring. `DapClient.cs`'s wire
        protocol was not touched.
  - [x] **3. Test Explorer** — new `IdePlusPlus.TestRunner` project (`TestDiscoveryService` shells to
        `dotnet test --list-tests`, `TestRunService` shells to `dotnet test --logger trx` with real
        `Process.Kill(entireProcessTree: true)` cancellation mirroring `RunService.Stop()` — the only
        genuine cancellation precedent in the codebase, since `BuildService`'s own `CancellationToken`
        parameter is accepted but never actually wired to a Cancel button — and `TrxParser` parses the
        resulting VSTest `.trx` XML defensively, tolerating missing/unexpected elements). Both the
        `--list-tests` output shape and the `.trx` schema were verified against this machine's real
        installed SDK (10.0.301) rather than assumed from memory, per the design's own risk that both
        formats are undocumented and SDK-version-fragile. `SolutionTreeNode` gained an `IsTestProject`
        flag (via a new `TestProjectDetector` checking MSBuild's automatic `IsTestProject` property
        with a `PackageReference`-name fallback) set at zero extra MSBuild-evaluation cost, right
        alongside the existing `TargetFramework` read in `SolutionLoader`. A new docked Test Explorer
        pane shows a Project→Class→Method tree with pass/fail/skipped/not-run status glyphs, Run
        All/Selected/Failed/Cancel commands, and a live summary string; double-clicking a failure
        navigates to its assertion's file:line on a best-effort basis (a regex over the TRX stack
        trace's `"in <path>:line <n>"` suffix — silently a no-op, never a throw, when no such pattern
        is found). Discovery runs after every successful Build and best-effort after solution load.
        "Debug a single test" remains explicitly out of scope, as does streaming each `dotnet test`
        run's raw output into the Output pane (results still land correctly; only the live
        in-progress console text is not yet surfaced — a small, known follow-up, not a
        functional gap). New `IdePlusPlus.TestRunner.Tests` (8 tests, including a real,
        wall-clock-bounded cancellation test and a namespace-with-dots FQN-splitting case) and 2 new
        `TestProjectDetector` tests, all against real scratch projects and real `dotnet test`/`dotnet
        build` runs, never mocks. End-to-end sanity-checked against this repo's own real
        `IdePlusPlus.SourceControl.Tests` project (discovered and ran all 29 real tests correctly)
        beyond the unit suite. Verified: full-solution build (0 warnings/errors), full test suite
        (211 tests across 17 projects, all passing), and a real launch smoke test.
  - [x] **4. Roslyn-based IntelliSense/diagnostics** — new `IdePlusPlus.Roslyn` project hosting an
        `MSBuildWorkspace` per solution, loaded independently of and after (chained, not parallel
        with) `SolutionLoader`'s own tree load and any NuGet restore-on-load attempt, to reduce the
        risk of a wall of false `CS0246`s from an unrestored project. Drives live squiggly-underline
        diagnostics in the editor plus a Build/Live split in the Error List (`BuildDiagnostic` gained
        a `Source` enum; all four existing `ErrorList.Clear()` call sites in Build/Clean/Rebuild/
        Start-Debugging were redirected to a new `ClearBuildDiagnostics()` so live rows survive a
        build run), and a member-list completion popup (`AvalonEdit.CodeCompletion.CompletionWindow`)
        on `.`/Ctrl+Space, mutually exclusive with the existing single-line DeepSeek FIM ghost text in
        both directions. Package versions (`Microsoft.CodeAnalysis.CSharp.Workspaces`/`.Workspaces
        .MSBuild`/`.CSharp.Features`, all `5.0.0`) were chosen empirically, not guessed — their
        `Microsoft.Build.Framework` dependency (18.0.2) was checked against this repo's pinned
        `Microsoft.Build` 18.9.6 line before committing, and `5.0.0` is also the first version whose
        dependency chain can parse `.slnx` at all (via `Microsoft.VisualStudio.SolutionPersistence`),
        which this repo needs. **A real on-disk data-loss bug was caught and fixed before this ever
        reached the working tree**: the first implementation pushed unsaved editor keystrokes into
        the workspace via `Workspace.TryApplyChanges`, which is built for "format and save" CLI
        tools and silently writes changed document text straight back to the file on disk — running
        the mandated real-solution verification step actually overwrote `GitService.cs` with
        synthetic probe text before this was caught, restored via `git checkout --`, and fixed by
        tracking unsaved text purely in an in-memory `Solution` snapshot that's never fed back into
        the `Workspace` itself. `RoslynWorkspaceService`'s type doc now states this invariant
        prominently, and the final code was grepped to confirm `TryApplyChanges` is never called
        anywhere, only mentioned in the comments explaining why not. Verification for this feature
        went well beyond "it compiled" at every stage, per its own design doc's explicit risk
        callout: the real `IdePlusPlus.slnx` was loaded via a real `MSBuildWorkspace` and produced
        real diagnostics (5 genuine `CS####` errors from injected bad syntax) and real completions
        (55 real BCL members after `File.`) before any Shell wiring was written; the final stage
        launched the real built app and used UI Automation to drive it, visually confirming real
        squiggles and a real completion popup against this repo's own source files, then confirmed
        via `git status` that none of those in-app edits ever touched disk. Signature help,
        go-to-definition, find-all-references, and rename remain explicitly out of scope. New
        `IdePlusPlus.Roslyn.Tests` (5 tests: 3 pure diagnostic-mapping unit tests plus 2 real
        integration tests against a real scratch solution built via the actual `dotnet` CLI — a
        genuine `CS0029` semantic error and a real `System.String.` completion request, no mocks).
        Verified: full-solution build (0 warnings/errors), full test suite (216 tests across 18
        projects, all passing), and a real launch smoke test.
  - [x] **5. Plugin API v2 + sandboxing** (final bigger bet) — replaced `Assembly.LoadFrom` with a
        per-plugin collectible `AssemblyLoadContext` (`PluginLoadContext`), forwarding a fixed set of
        shared/host assembly names (`IdePlusPlus.PluginContracts`, WPF's own assemblies, AvalonEdit,
        AvalonDock, `System.Text.Json`) to the Default ALC so type identity across the host/plugin
        boundary is preserved — without this, a plugin merely shipping its own copy of a shared DLL
        would get a second, distinct runtime type for `IIdePlugin`/`IPluginHost` and every interface
        dispatch across the boundary would fail. A real GC/unload test was treated as a hard gate, not
        optional polish, per the design doc: a genuine scratch plugin assembly (compiled via a real
        `dotnet build`, not an in-memory stub) was loaded, `Initialize`'d, unloaded, and its load
        context's `WeakReference` confirmed collected — `IsAlive` was `false` on the very first GC
        check, no retries needed. `IPluginHost` gained 6 new additive members (editor/Solution
        Explorer context-menu commands, a dockable tool window, a hover provider, keybindings with
        reserved-gesture rejection, and chat-tool registration mirroring MCP's
        `"{ServerName}__{Name}"` qualified-name convention as `"plugin__{PluginName}__{ToolName}"`)
        — the original 3 members are byte-for-byte unchanged, and `samples/SamplePlugin`'s existing
        v1 behavior was verified still working through the new ALC-based loader via a real,
        independently-compiled build of that exact sample. Every plugin-supplied delegate (menu
        `execute`/`canExecute`, hover, tool-window factory, chat-tool `execute`) is wrapped in
        try/catch-and-log, matching the existing `RunPluginCommand` convention, so a throwing plugin
        callback degrades one feature rather than the app. Dynamic AvalonDock pane registration —
        flagged by the design as likely the single largest risk in this feature — required a real
        ordering fix: `XmlLayoutSerializer.Deserialize` replaces the whole layout tree when a saved
        `layout.xml` exists, which would silently discard a plugin pane added before that ran, so
        registrations are buffered and attached only after `RestoreLayout()` completes, with a
        ContentId-collision check to avoid duplicating a pane a saved layout already reconstructed.
        Plugin chat-tool calls are checked and routed before the existing MCP fallback, structurally
        unable to reach `ProposeEditTool`/`ProposePlanTool`'s real file-editing routes. Framing
        throughout, including in code comments: neither Tier 1 (ALC isolation) nor Tier 2 (narrow
        capability surface) is a security boundary against a deliberately malicious plugin — a
        collectible `AssemblyLoadContext` does not hide other loaded assemblies from reflection, so
        `Activator.CreateInstance(typeof(SecureSettingsStore))`, arbitrary file access, P/Invoke, and
        `Environment.Exit` remain fully reachable from plugin code regardless; a genuine boundary
        would need an out-of-process host (Tier 3, explicitly deferred). Real end-to-end
        verification: the actual built app was launched with the real, rebuilt `SamplePlugin.dll`
        dropped into the real plugins directory and stayed alive and responsive with zero Serilog
        errors; full interactive click-through of the new UI surfaces (context-menu item, tool
        window, hover tooltip, live chat-tool call) was not completed because the interactive desktop
        session was locked at verification time — per safety policy, no attempt was made to unlock it
        or enter credentials, and this gap is stated here rather than implied away. 18 new tests
        across `IdePlusPlus.Shell.Tests` (the unload/GC hard gate, the SamplePlugin compatibility
        check, reserved-gesture rejection, and chat-tool registry routing/error-isolation), all
        against real compiled plugin assemblies, no mocks of the loader itself. Verified: full-solution
        build (0 warnings/errors), full test suite (217 tests across 17 projects, all passing), and a
        real launch smoke test (with no plugin installed, confirming the no-plugin path is unaffected).

- [x] Publish profiles — new `IdePlusPlus.Publish` project reading and writing standard
      `Properties/PublishProfiles/*.pubxml`, so a profile authored in Visual Studio is consumed
      as-is and one authored here runs from `dotnet publish -p:PublishProfile=x`. Folder and
      container-image (`-t:PublishContainer`) targets; both argument forms were verified against a
      real SDK before being written down. A Publish dialog (Build menu, Solution Explorer project
      context menu, status-bar profile picker persisted per project in `.ideplusplus/publish.json`)
      and a `publish_project` copilot tool. Two design points carried most of the weight: unmodelled
      properties round-trip verbatim and existing files are edited in place, because a writer that
      emitted only the properties IDE++ understands would delete everything Visual Studio wrote (and
      the comment block in this repo's own `win-x64.pubxml`) on the first save; and `dotnet publish`
      runs through a new `BuildService.RunDotnetAsync` rather than a lock of its own, since a publish
      writes the same `obj/bin` a concurrent build does. Profiles using a protocol IDE++ cannot run
      (MSDeploy, ClickOnce, Azure, FTP) are listed read-only rather than hidden — hiding them would
      make a shared solution look like it has fewer profiles than it does — and the writer refuses
      them outright. The copilot tool runs folder profiles only and never authors one: a container
      profile with a registry configured pushes an image outward with no undo, and what gets shipped
      where is the user's decision. 74 new tests across `IdePlusPlus.Publish.Tests` and
      `IdePlusPlus.Shell.Tests`; full suite 594 tests across 18 projects, all passing. Not yet
      verified interactively: the dialog's own click-through, and a container publish against a
      running daemon.

**All five "bigger bet" initiatives are now shipped.** Signature help, go-to-definition,
find-all-references, rename, debugging a single test, remote/non-local attach, NuGet
version-conflict UI, and a real out-of-process plugin sandbox (Tier 3) all remain explicitly
deferred, as scoped in each feature's own design section above — future work, not gaps introduced
silently.

**Verification performed**: every milestone above was verified with `dotnet build` (0 warnings/errors)
and `dotnet test` (105 tests passing across 12 test projects, up from 7 projects/~60 tests at the
start of this pass) after landing, plus a real launch of `IdePlusPlus.exe` after every milestone to
confirm no DI/XAML startup crash. Testing this way caught two real bugs before they shipped: `git
restore --staged` failing on a repo with no commits yet (surfaced by a real end-to-end git test, not
a mock), and an unguarded empty-`choices`-array index that would have crashed every single chat
stream once usage-reporting was turned on (surfaced by testing the exact chunk shape real APIs send,
not just the happy path). This sandbox has no interactive desktop session, so the new UI (Git pane,
Error List, Go to Line/About dialogs, menu additions, word-level diff highlighting) could not be
visually screenshot-verified here - worth a look on a real machine before considering it fully done.
Committed to git in two commits (one per major batch); nothing pushed.

**Baseline**: IDE++ is a from-scratch Windows WPF IDE (AvalonDock + AvalonEdit + CommunityToolkit.Mvvm +
Microsoft.Extensions.DependencyInjection + the MSBuild API + a hand-rolled DAP client over netcoredbg),
21 commits in, with its own Copilot-style chat pane (OpenRouter/LM Studio, MCP tools, a `propose_edit`
diff/accept flow, project memory, and a local embeddings index). It is impressively complete for its
age, but confirmed to have **zero** git integration, **zero** CI, and no plan-mode / effort / auto-mode
concepts anywhere in the chat loop.

---

## Part 1 — The four requested features

### 1. Git / version-control integration

Nothing exists today: no `LibGit2Sharp` package, no `Git`/`SourceControl` types anywhere, nothing in
[`Directory.Packages.props`](../Directory.Packages.props).

**Recommended approach: shell out to `git.exe`**, not LibGit2Sharp. Reasons:
- [`BuildService`](../src/IdePlusPlus.Build/BuildService.cs) and [`RunService`](../src/IdePlusPlus.Run/RunService.cs) already establish exactly this pattern (spawn a CLI process, stream stdout/stderr, parse a callback) — a `GitService` following the same shape is idiomatic for this codebase, not a new paradigm.
- VS Code's own built-in Git extension shells out to `git` rather than embedding libgit2, for the same reason: it avoids bundling a native per-platform binary and inherits the user's actual git config/credential helpers/hooks for free.
- `git status --porcelain=v2` and `git diff` are stable, scriptable, well-documented formats.
- LibGit2Sharp remains a fallback if process-spawn latency ever becomes a problem for very large repos (its maintenance has also been inconsistent — another reason to prefer the CLI dependency, which every .NET developer already has installed).

**New project**: `IdePlusPlus.SourceControl` (+ `IdePlusPlus.SourceControl.Tests`, matching the existing per-area project split).

**Components**:
| Piece | What it does |
|---|---|
| `GitService` | Shells to `git`, exposes `GetStatusAsync` (porcelain v2 → parsed file list with staged/unstaged/untracked state), `StageAsync`/`UnstageAsync`, `CommitAsync`, `GetBranchesAsync`/`CheckoutAsync`/`CreateBranchAsync`, `PushAsync`/`PullAsync`/`FetchAsync`, `GetDiffAsync(path)` (returns old/new text so the **existing** `DiffComputer`/`DiffLine` infra from `IdePlusPlus.Copilot.Diff` can render it — same diff viewer, two callers), `GetFileAtHeadAsync(path)` for gutter diffing. |
| `SourceControlPaneViewModel` (new `ToolPaneViewModel`, docked like `SolutionExplorerPaneViewModel`) | Staged/unstaged file lists, commit message box, Commit/Push/Pull/Fetch buttons, current-branch display with a branch-switch dropdown. |
| Editor gutter diff markers | A new `BackgroundRenderer`/margin in [`IdePlusPlus.Editor`](../src/IdePlusPlus.Editor) (same pattern as `CurrentLineBackgroundRenderer.cs`/`BreakpointMargin.cs`) comparing the live buffer against `GetFileAtHeadAsync`, coloring added/modified/removed line strips. |
| Status bar branch indicator | Add a `TextBlock` to the existing `StatusBar` in [`MainWindow.xaml`](../src/IdePlusPlus.Shell/MainWindow.xaml) (next to the existing cursor-position block) showing `branch ↑2 ↓1`. |
| Solution Explorer glyphs | Color/annotate `SolutionTreeNode` file entries by git status (modified/added/untracked) — reuses the existing `HierarchicalDataTemplate` in `MainWindow.xaml`. |
| Menu | New `_Git` top-level menu: Commit, Push, Pull, Fetch, Branches, (later) History. |

**Phasing**: MVP = status pane + stage/commit/push/pull + branch switch + diff view (reusing `DiffComputer`). v2 = commit history/log graph, blame, stash, merge-conflict UI.

**Effort**: L (new project, but every sub-piece reuses an existing pattern — diffing, tool-pane docking, process-spawn services — so it's assembly, not invention).

### 2. Plan Mode

Today [`CopilotChatPaneViewModel.RunAssistantTurnsAsync`](../src/IdePlusPlus.Shell/ToolPanes/CopilotChatPaneViewModel.cs) always sends the real `propose_edit` tool and auto-executes MCP tool calls in a loop (bounded by `MaxToolLoopIterations = 8`) — there's no read-only "investigate and present a plan" gate before anything can touch a file.

**Design**:
- Add an `IsPlanMode` toggle to the chat pane toolbar (same pattern as the existing `IncludeCurrentFileContext`/`UseCodebaseSearch` checkboxes), persisted via `AppSettingsStore`.
- New `propose_plan` tool (sibling to [`ProposeEditTool.cs`](../src/IdePlusPlus.Copilot.Diff/ProposeEditTool.cs)) with a schema like `{ steps: string[], summary: string }`.
- In `StreamOneTurnAsync`, when `IsPlanMode` is true: send `propose_plan` instead of `propose_edit` in the `tools` list (MCP read-oriented tools can stay enabled; there's no side-effect metadata on MCP tools today to distinguish read vs. write ones, so the honest MVP scope is "no file edits happen in Plan Mode," not "no tool calls happen at all").
- Render the plan as a checklist message (new `PlanProposalViewModel`, same family as `EditProposalViewModel`) with a single **"Approve & Implement"** action that flips `IsPlanMode` off for the rest of that conversation and re-sends the approved plan as an instruction with the real tool set enabled.
- System-prompt note added only while `IsPlanMode` is on, telling the model to investigate and propose a plan rather than edit.

**Files**: `CopilotChatPaneViewModel.cs`, new `ProposePlanTool.cs`/`PlanProposalViewModel.cs`, `MainWindow.xaml` (checklist DataTemplate + toolbar toggle).

**Effort**: M

### 3. Effort (reasoning-effort control)

Confirmed: no `temperature`/`top_p`/`reasoning_effort` parameter is ever sent — [`OpenAiCompatibleChat.cs`](../src/IdePlusPlus.Copilot.Core/OpenAiCompatibleChat.cs) builds request bodies with only `role`/`content`/`tool_calls`. OpenRouter's OpenAI-compatible endpoint accepts a `reasoning: { effort: "low" | "medium" | "high" }` field (silently ignored by models that don't support extended reasoning), which is exactly the shape Claude Code itself exposes as "effort."

**Design**:
- Add an `EffortLevel` enum (`Low`/`Medium`/`High`) with a `ComboBox` next to the existing model/provider pickers in the chat pane, persisted via `AppSettingsStore` under a new `ChatEffort` key — same pattern as `ProviderSettingKey`/`OpenRouterModelSettingKey` in `CopilotChatPaneViewModel.cs`.
- Extend [`IChatClient.StreamChatAsync`](../src/IdePlusPlus.Copilot.Core/IChatClient.cs) with an optional `effort` parameter; `OpenAiCompatibleChat.ToRequestMessage`/request-building adds `reasoning: { effort }` when set.
- Wire through [`OpenRouterChatClient.cs`](../src/IdePlusPlus.Copilot.OpenRouter/OpenRouterChatClient.cs) and [`LmStudioChatClient.cs`](../src/IdePlusPlus.Copilot.LmStudio/LmStudioChatClient.cs) (LM Studio can just ignore the field or omit it if the loaded local model doesn't support it — no harm either way).

**Effort**: S–M (small, mechanical, mirrors the existing model/provider-selection plumbing exactly).

### 4. Auto Mode

Confirmed asymmetry: MCP tool calls **already** auto-execute in a loop; `propose_edit` proposals **always** require a manual Accept/Reject click in [`EditProposalViewModel.cs`](../src/IdePlusPlus.Shell/ToolPanes/EditProposalViewModel.cs). "Auto Mode" closes that gap for edits.

**Design**:
- Add an `IsAutoMode` toggle to the chat toolbar, persisted setting.
- When `EditProposalViewModel.Complete()` runs and `IsAutoMode` is true, immediately invoke `AcceptCommand` instead of waiting for the user — the diff still renders in the transcript first for auditability, it's just not gated.
- Raise `MaxToolLoopIterations` (or make it configurable) when Auto Mode is on — the manual accept step was an implicit checkpoint every few steps; without it a higher ceiling (with the existing `CancelStreaming`/Stop button always available) is appropriate.
- Add a visible indicator (banner or colored chat-pane border) whenever Auto Mode is active, so "the AI is writing to disk without asking" is never ambiguous.
- Pair with the quick win below ("write a `.bak` before disk writes") — Auto Mode is exactly the scenario where a cheap safety net matters most.
- Note the natural interaction with Plan Mode: the two are independent toggles, but Auto Mode without an approved plan first is the highest-risk combination worth calling out in the UI (e.g. a subtle warning), not blocking.

**Files**: `CopilotChatPaneViewModel.cs`, `EditProposalViewModel.cs`.

**Effort**: S–M

*(Plan Mode + Effort + Auto Mode as three independent toggles/dials in one chat toolbar is a coherent single milestone — it mirrors Claude Code's own UX and reuses the same settings-persistence pattern for all three.)*

---

## Part 2 — Everything else found in the survey

### Reliability & safety net (do these first — cheap, de-risks everything below)

| Item | Effort | Files |
|---|---|---|
| Global unhandled-exception handler (`DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) — today one bad exception (including from a plugin's menu callback) crashes the whole process with zero trail | S | [`App.xaml.cs`](../src/IdePlusPlus.App/App.xaml.cs) |
| Wrap `PluginMenuCommand`'s execute delegate in try/catch — `PluginLoader` guards `Initialize()` but not command execution | S | [`PluginMenuCommand.cs`](../src/IdePlusPlus.Shell/PluginMenuCommand.cs) |
| Actually wire up Serilog — it's referenced in `Directory.Packages.props` but **zero** project references it or calls it; the only "log" today is the in-memory Output pane, lost on exit/crash | S | `App.xaml.cs`, [`Directory.Packages.props`](../Directory.Packages.props) |
| Guard `EditorWorkspace.Open`/recent-file-open against a missing file — currently an unhandled `IOException` from clicking a stale Recent Files entry takes down the app (no global handler exists yet either) | S | [`EditorWorkspace.cs`](../src/IdePlusPlus.Editor/EditorWorkspace.cs), [`DocumentViewModel.cs`](../src/IdePlusPlus.Editor/DocumentViewModel.cs) |
| Fix silent data loss: canceling the Save-As dialog during a dirty-document close-confirmation still closes the document and discards changes | S | [`MainWindow.xaml.cs`](../src/IdePlusPlus.Shell/MainWindow.xaml.cs) `DockManager_OnDocumentClosing` |
| Prune undecryptable entries from `secrets.json` instead of leaving them forever (machine-migration leaves permanently dead rows) | S | [`SecureSettingsStore.cs`](../src/IdePlusPlus.Infrastructure/SecureSettingsStore.cs) |
| Log `ex.ToString()` (+ `LoaderExceptions` for `ReflectionTypeLoadException`) instead of just `ex.Message` on plugin load failure | S | [`PluginLoader.cs`](../src/IdePlusPlus.Shell/PluginLoader.cs) |
| Write a `.bak` (or a staging undo dir) before `EditApplicationService.Apply()` overwrites a not-currently-open file on disk — today there's zero recovery path for that case | S | [`EditApplicationService.cs`](../src/IdePlusPlus.Copilot.Diff/EditApplicationService.cs) |
| Fix two real ghost-text bugs: (1) accepting a stale suggestion inserts it at the old offset after the caret has moved away; (2) switching documents mid-request can render document A's AI suggestion into document B | S each | [`TextEditorHost.xaml.cs`](../src/IdePlusPlus.Editor/TextEditorHost.xaml.cs) |

### Editor

- **Add a discoverable Edit menu** (Find/Replace/Go-to-Line) — AvalonEdit's `SearchPanel` already provides working Ctrl+F/Ctrl+H, it's just never surfaced in any menu (M).
- Add word-wrap toggle and zoom/font-size control — both are single pre-existing AvalonEdit properties with no UI wired to them (M).
- Add a test project for the editor's pure-logic classes (`DocumentViewModel`, `EditorWorkspace`, `BreakpointManager`, `BraceFoldingStrategy`) — all directly unit-testable today, zero WPF dependency, zero coverage (M).
- Persist breakpoints across restarts — reuse the exact JSON-persistence pattern `EditorWorkspace` already has for `recent-files.json` (S).
- Surface DeepSeek FIM failures (auth errors, network errors) instead of failing silently — currently indistinguishable from "no suggestion available" (S).
- Live bracket-matching highlight distinct from folding; go-to-definition/symbol navigation; minimap; multi-cursor; split-view; line-ending/encoding indicator — larger, lower-priority polish items (L, mostly build-from-scratch since AvalonEdit doesn't provide them).
- **Bigger bet**: no IntelliSense/LSP/Roslyn at all — completion is single-line AI ghost text only, no diagnostics/squiggles, no member lists, no signature help. This is the single largest capability gap in the editor and would be its own multi-milestone initiative (Roslyn workspace hosting + a completion/diagnostics provider wired into AvalonEdit).

### Build / Run / Debug / Project System

- **Configuration (Debug/Release) switcher** — the whole IDE is hardcoded to Debug today: `BuildService`/`RunService` never pass `--configuration`, and `ResolveOutputDllAsync` hardcodes `bin/Debug` when locating the debuggee DLL. This is arguably the highest-value single gap in this category and the plumbing pattern (thread an optional string through, mirroring how `--framework` already works) already exists (M).
- Add Clean/Rebuild commands — `BuildService` has only `BuildAsync` today (S).
- Parse MSBuild's `file(line,col): error CSxxxx: message` output into a clickable **Error List** pane instead of one undifferentiated text blob — reuses the existing tool-pane pattern and the existing file-open-at-line navigation already used by Solution Explorer double-click (M).
- Send DAP `setExceptionBreakpoints` during debug startup — right now whether an unhandled exception even stops the debugger depends entirely on netcoredbg's undeclared default (S).
- Add condition/hit-count support to breakpoints, and pass them through the DAP `setBreakpoints` call netcoredbg already supports (M).
- Add a minimal Watch pane using the DAP `evaluate` request, which is never sent today — `VariableInfo` and the `LocalsPaneViewModel` pattern already exist to reuse (M).
- No NuGet package management (add/remove/restore) anywhere, no Test Explorer/`dotnet test` integration (the repo's own xUnit suite can't be run from inside IDE++ itself), no New Project/New Item wizards, no multi-project debug or attach-to-process — larger initiatives, roughly in that priority order.
- No publish of any kind — no `dotnet publish`, no `.pubxml` reading, no deployment UI. The only publish in the repo is IDE++ publishing itself, via a hand-written profile nothing in the app can read (M). *(Shipped — see the execution status above.)*

### Copilot / AI ecosystem

- **Chat history is lost on close** — `Messages` is purely in-memory; the only persistence is the manual "Save memory" LLM-summarization flow, not a raw-transcript save. Fix: JSON-serialize `Messages` into the same per-solution directory `ProjectMemoryStore` already uses (`.ideplusplus/copilot/`), auto-saved after each turn (M).
- Add a user-editable system-prompt/custom-instructions setting — one more `ChatRole.System` insertion sourced from `AppSettingsStore`, same insertion point as the existing memory/file-context/RAG blocks (S).
- No context-window/token-budget management at all — full history + full open file + memory + RAG chunks are sent unbounded every turn. A cheap first pass (word/char-count threshold → "conversation is getting long, consider Save memory" warning) is a good S-effort mitigation short of real token counting (S).
- No token usage/cost display — OpenAI-compatible streaming APIs typically emit a `usage` block on the final chunk with `stream_options: {include_usage: true}`; parsing it is additive to the existing stream-event model (M).
- `propose_edit` is strictly single-file/single-range per call — a multi-file change becomes N independent proposals with no atomicity (accept 2 of 3, third rejected → partially-applied state). Worth a design pass before Auto Mode ships broadly, since Auto Mode makes this failure mode easier to hit unnoticed.
- Diff view is line-level only — DiffPlex's `InlineDiffBuilder` already exposes word-level `SubPieces` that aren't being used (M).
- No way to rewind/checkpoint a conversation or batch-revert applied edits.
- MCP support is tools-only (no `resources/list`/`resources/read`, no `prompts/*`) — the initialize handshake already reads server capabilities but ignores them (M).
- MCP server list is global-only — no project-local `.ideplusplus/copilot/mcp-servers.json` auto-discovery, even though the per-solution directory convention already exists for memory and embeddings (S).

### Infrastructure, security, plugins, tests

- **10 of 17 `src/` projects have zero test coverage**: `App`, `Build`, `Copilot.DeepSeek`, `Copilot.LmStudio`, `Copilot.Memory`, `Debug`, `Editor`, `Infrastructure`, `PluginContracts`, `Shell`. `Shell` is the largest project in the solution and `Infrastructure` is small, pure, and already designed for testability (`SecureSettingsStore` takes a constructor-injected path override specifically for tests) — an `IdePlusPlus.Infrastructure.Tests` project is the single best effort-to-value ratio in the whole test-coverage gap (S).
- No auto-update mechanism at all.
- Plugins run fully trusted, in-process, unsigned, unversioned, with no isolation — the narrow `IPluginHost` contract is cosmetic since a plugin can reach anything via reflection once loaded, including `secrets.json`'s DPAPI-protected values (which are trivially decryptable from code already running as the same Windows user). Worth flagging even though a real fix (separate `AssemblyLoadContext`, capability sandboxing) is a large undertaking.
- Plugin API is currently 3 methods (`RegisterHighlighting`, `RegisterToolsMenuCommand`, `LogInfo`) — no context-menu contributions, no new dockable tool windows, no editor extension points (completion/code-lens/hover), no keybindings, and no way to register an additional chat tool the way an MCP server can.
- `SecureSettingsStore`'s DPAPI `CurrentUser` scope silently breaks all stored API keys on machine migration/profile re-image, with no export/import story and no diagnostic beyond "No key set."

### Shell / UX polish

- No **Recent Solutions** list and no reopen-last-solution-on-startup (Recent *Files* already exists and is a good model to copy) (S–M).
- No Quick-Open/fuzzy file switcher and no Find-in-Files across the solution.
- No Edit menu, no Window menu, no Help/About dialog.
- Solution Explorer's context menu only has "Set as Startup Project" for Project nodes — right-clicking a File/Folder node does nothing (no Open/Rename/Delete/Copy Path).
- No busy/progress indicator for solution load — a large solution can look inert.
- Several small, high-value S-effort fixes: bind the window title to the active solution/document (currently hardcoded `"IDE++"`), persist window bounds/maximize state (pane *layout* is already persisted via `XmlLayoutSerializer` — the outer window isn't), make drag-and-drop `.sln`/`.slnx`-aware (currently opens a solution file as a raw text document), add Save All/Close All, show filename-with-path-tooltip in the Recent Files menu instead of the full path as the label, add a minimal About dialog.

---

## Suggested sequencing

1. **Reliability pass** (all S-effort, no dependencies on each other) — exception handling, Serilog wiring, the missing-file/save-cancel/plugin-crash bugs, `.bak` safety net. Do this before Auto Mode ships, since Auto Mode is exactly where an unhandled exception or an un-backed-up overwrite hurts most.
2. **The four requested features** — Git MVP, Effort, Auto Mode, Plan Mode, as one coherent chat-toolbar milestone (Git can land in parallel since it's a separate subsystem).
3. **Build/Debug maturity** — Configuration switcher, Clean/Rebuild, Error List, exception breakpoints.
4. **Copilot ecosystem depth** — chat history persistence, system prompt, token usage, multi-file edits (revisit once Auto Mode is live and the atomicity gap is felt directly), MCP resources/project-local config.
5. **Shell polish batch** — Recent Solutions, window title/bounds, Edit menu, Save All/Close All, About dialog (all S/M, easy to batch into one PR).
6. **Bigger bets** (each its own initiative) — Roslyn-based IntelliSense/diagnostics, NuGet management, Test Explorer, multi-project debug/attach, plugin API v2 (tool windows, context menus, chat-tool registration), plugin sandboxing.
7. **Test debt** — add the 10 missing test projects incrementally alongside whichever area is being touched by the milestone above (start with `Infrastructure` and `Editor`, both already test-friendly with zero WPF ceremony).
