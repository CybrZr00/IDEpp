# Bigger bets — detailed designs

Produced by a 5-way parallel survey (one agent per feature, each required to read the actual
files it names before proposing anything) plus a synthesis pass that cross-checked all five for
file conflicts, shared infrastructure, and hidden MVP dependencies. This is the detail behind the
one-line "Bigger bets" bullet in [roadmap.md](roadmap.md); implementation follows the order below,
one feature at a time, each fully built/tested/launch-verified and committed before the next starts.

## Implementation order and why

1. **NuGet package management** — highest tractability (near-verbatim mirror of the proven
   `GitService` process-spawn pattern), `dotnet list package --format json` gives a stable
   machine-readable format, self-contained new project, immediate everyday value.
2. **Multi-project debug/attach** — surprisingly tractable: `DapClient`'s per-instance seq
   dictionary and `DebugSession`'s step/continue methods already work correctly with N concurrent
   instances with zero changes; the real work is a thin `DebugSessionManager` wrapper, an `attach`
   DAP request sibling to the existing `launch`, and session-picker UI.
3. **Test Explorer** — reuses `SolutionLoader`'s already-evaluated `Project` objects for
   test-project detection at zero extra MSBuild cost. Ordered after multi-project-debug (not
   before) so that when "debug a test" is picked up as a fast-follow, `DebugSessionManager` +
   `attach` already exist to build on.
4. **Roslyn-based IntelliSense/diagnostics** — almost certainly the highest end-user value item,
   but the most genuinely novel territory (new heavy dependency graph, a second full MSBuild pass
   on every solution load, version-skew risk between pinned `Microsoft.CodeAnalysis.*` and
   whatever `MSBuildLocator` resolves at runtime). Placed fourth so the team has already banked
   experience wiring background-loaded services into `LoadSolutionAsync` twice before tackling the
   heaviest one, and so its "wall of CS0246 errors on an unrestored project" risk can lean on
   NuGet's restore-on-load already existing.
5. **Plugin API v2 + sandboxing** — correctly last: by far the largest effort (XL vs. L), the most
   speculative near-term value (benefits a not-yet-existing plugin ecosystem), and contains
   genuinely hard correctness work (collectible `AssemblyLoadContext` unload verification) with no
   existing pattern in the codebase to mirror.

**Cross-feature findings from the synthesis:**
- No two designs collide on a new file path or new project name.
- Several designs independently touch the same existing hotspots (`MainWindowViewModel.LoadSolutionAsync`,
  the Solution Explorer Project context menu in `MainWindow.xaml.cs`, `TextEditorHost.xaml.cs`,
  `Directory.Packages.props`) — not blocking, but each feature after the first at a hotspot should
  expect a manual merge, not a clean apply.
- Process-spawning (`GitService`/`BuildService`'s `ProcessStartInfo`+`ArgumentList` pattern) is about
  to be mirrored a third and fourth time by NuGet and Test Explorer. A shared `ExternalProcessRunner`
  was flagged as worth extracting — **deliberately deferred**: none of the five MVPs require it, and
  extracting it mid-feature would touch already-shipped, already-tested `GitService`/`BuildService`
  code as a side effect of an unrelated feature. Left as a flagged future refactor, not done here.
- No feature's MVP hard-blocks on another feature's MVP landing first. Test Explorer's own design
  explicitly defers "debug a test" (which would need `DebugSessionManager`) to a later phase rather
  than baking the dependency into its MVP.

---

## 1. NuGet package management

**Summary**: A `NuGetService` that shells to `dotnet.exe` (list/add/remove package, restore)
mirroring `GitService`'s `ArgumentList` process-spawn pattern exactly, plus a NuGet tool pane
(per-project installed-package list with Remove buttons) and an Add Package dialog that queries
the nuget.org v3 search API through the app's existing shared `HttpClient`.

**Grounded in**: `GitService.cs`'s `RunRawAsync`/`RunAsync` split (never string-concatenated args) is
the pattern to mirror. `BuildService.cs`'s streaming `OutputDataReceived` callback is better suited
to slow NuGet operations than buffering. **Load-bearing fact**: this repo uses Central Package
Management (`ManagePackageVersionsCentrally=true`) — `dotnet add package` mutates the *shared*
`Directory.Packages.props`, not just the target `.csproj`, so this is a solution-wide write, not a
project-scoped one. `SolutionTreeNode` has no existing References/Packages node. The Project node's
context menu is built in code-behind (`MainWindow.xaml.cs`) because a XAML `ContextMenu`'s popup
isn't part of its `PlacementTarget`'s visual tree, so `RelativeSource` bindings can't reach the
view model — any new menu item must follow that same pattern. `SourceControlPaneViewModel` +
`SourceControlPanel.xaml` is the concrete, most-recent tool-pane shape to copy.

**New project**: `IdePlusPlus.NuGet` (`NuGetService.cs`, `InstalledPackage.cs`,
`NuGetSearchClient.cs`, `NuGetSearchResult.cs`).

**New Shell files**: `ToolPanes/NuGetPaneViewModel.cs`, `Views/NuGetPanel.xaml(.cs)`,
`Views/AddPackageDialog.xaml(.cs)` + `AddPackageDialogViewModel.cs`.

**MVP scope**: list/add/remove/restore via `dotnet.exe` (`ArgumentList` only — ids/versions from
search results or user typing never get concatenated into a command line); a NuGet tool pane opened
per-project via a new Solution Explorer context-menu item, listing direct (non-transitive) package
references with per-row Remove, Refresh, and Restore; an Add Package dialog searching nuget.org's
public v3 search endpoint with an explicit ~8s timeout and a visible offline/error state; a default-on,
user-toggleable "restore on solution load" setting. After every mutation the pane re-runs
`dotnet list package` rather than trusting an optimistic in-memory update, given the CPM shared-file
risk below.

**Deferred**: version-conflict resolution UI, private feed/NuGet.config authentication UI,
transitive dependency tree view, outdated-package UI, multi-project bulk add/remove.

**Real risks**: `dotnet add/remove` mutates `Directory.Packages.props` (solution-wide) and the
target `.csproj` directly on disk with no file-watcher and no unsaved-state check — if that file is
open as a dirty editor tab, the on-disk write silently diverges from the tab with no warning; the
service should check for an open, dirty document at that path before mutating and warn. The nuget.org
search call is the one piece of outbound HTTP beyond localhost/LLM providers in this feature — needs
a real timeout and a non-blocking error state. `IsBusy`-gating must prevent two concurrent `dotnet.exe`
invocations racing to write the same files. `dotnet list package --format json` requires a recent SDK
(.NET 6+) — treat a parse failure as a soft error, not a crash.

---

## 2. Multi-project debug/attach

**Summary**: Replace the single always-on `DebugSession` singleton with a `DebugSessionManager`
that owns zero-or-more concurrent `DebugSession` instances (each with its own `DapClient`/netcoredbg
process), add a session-picker UI so Locals/Call Stack show the selected session's state, add an
Attach-to-Process command that sends DAP `attach` instead of `launch`, and let the user configure a
settable list of startup projects that all launch together on F5.

**Grounded in**: all of `src/IdePlusPlus.Debug` (only two files: `DapClient.cs`, `DebugSession.cs`) —
`DapClient` matches requests to responses by `seq` in a per-instance dictionary, so **multiple
`DapClient` instances are already safe to run concurrently as-is, with zero protocol changes**.
`DebugSession.StartAsync` hard-codes `request = "launch"`; there is no `attach` request type or PID
plumbing anywhere. `BreakpointManager` is already session-agnostic (keyed by file path only), which
is good news — breakpoints don't need per-session storage, just re-sending to every active client.
`StartupProjectStore.cs` persists exactly one path string (not a list) via plain `File.WriteAllText`.
`DebugSession`/`CallStackPaneViewModel`/`LocalsPaneViewModel` are all DI singletons with zero
session concept today.

**New files**: `src/IdePlusPlus.Debug/DebugSessionManager.cs`, `ProcessAttachTarget.cs`,
`src/IdePlusPlus.Shell/ToolPanes/AttachToProcessDialogViewModel.cs`,
`src/IdePlusPlus.ProjectSystem/StartupProjectsStore.cs` (replaces the single-path store, migrating
the old plain-text format on read).

**MVP scope**: `DebugSessionManager` running N concurrent `DebugSession`s; a simple session picker
(ComboBox or small tab strip) above the existing Locals/Call Stack panes, which stay "dumb
single-collection" views — the picker just changes what gets pushed into them; an Attach-to-Process
command enumerating `System.Diagnostics.Process.GetProcesses()`, heuristically flagging likely-.NET
processes (coreclr.dll/hostfxr.dll loaded, or `dotnet.exe`/apphost name), in a simple pick-and-confirm
dialog; `DebugSession.AttachAsync(processId)` sending DAP `attach` with the same
init/breakpoints/configurationDone sequence otherwise; startup projects as a persisted list with
checkboxes replacing "Set as Startup Project" in the context menu; F5 launches one `DebugSession` per
checked project. Global F5/F10/F11 shortcuts act on one "active" session (whichever is selected in
the picker), not fanned out to all — this is the honest, low-risk behavior, matching how real IDEs
handle it too.

**Deferred**: cross-process breakpoint-hit visual sync polish, per-session exception-settings UI,
remote/non-local attach, a true per-session docked Watch pane (doesn't exist even for one session
today), auto-attach-on-child-process-launch, making non-debug Run (F5 without debugger) multi-project.

**Real risks**: netcoredbg is a real per-process CLR-hosting debugger — N concurrent sessions means N
full debugger-host processes with no throttling; the MVP should at least warn above some threshold.
F5/F10/F11 targeting "whichever session is active" needs to be visibly unambiguous in the UI (e.g. a
highlighted active-session name in the status bar) or stepping will confuse users once more than one
session exists. `DebugSession.StartAsync` pushes *every* breakpoint in the solution to whichever
client starts; with N sessions every netcoredbg gets every breakpoint regardless of whether that file
is even loaded — harmless (unmatched breakpoints just never hit) but wasteful, and not fixable without
breakpoint-to-project scoping (explicitly out of scope). The startup-project file-format migration
(string → JSON list) needs an explicit test for the old-format read path. `DapClient.SendRequestAsync`
has no built-in timeout — an attach that netcoredbg can't complete (e.g. no debug privileges on the
target process) will hang indefinitely unless a `CancellationToken` is passed and a clear error surfaced.

---

## 3. Test Explorer

**Summary**: A `TestRunner` service (new project, sibling to `IdePlusPlus.Build`) that shells out to
`dotnet test --list-tests` for discovery and `dotnet test --filter ... --logger "trx"` for
execution, parsing the resulting TRX XML into a flat list of (project, class, method, outcome,
duration, error/stack). A `TestExplorerPaneViewModel` mirrors `ErrorListPaneViewModel`'s shape but as
a project→class→method tree with status icons and Run All/Selected/Failed/Cancel commands; double-click
a failure navigates to its assertion location the same way `OpenDiagnostic` does for build errors.

**Grounded in**: `BuildService.cs`'s `ProcessStartInfo`+`ArgumentList`+`OutputDataReceived` streaming
pattern (mirrored exactly); `ErrorListPaneViewModel`'s tool-pane shape and `MainWindow.xaml.cs`'s
`ErrorListBox_OnMouseDoubleClick` → `MainWindowViewModel.OpenDiagnostic` navigation, reused unchanged
in kind. `SolutionLoader.cs`'s already-evaluated `Microsoft.Build.Evaluation.Project` object is the
natural place to add a test-project heuristic (`IsTestProject` MSBuild property, set automatically by
`Microsoft.NET.Test.Sdk`, with a `PackageReference`-name fallback for older SDKs) — this adds zero
extra MSBuild evaluations. Confirmed against this repo's own 15 `tests/**/*.csproj`: all rely on the
SDK's automatic `IsTestProject` default, none set it explicitly. `src/IdePlusPlus.Debug`'s `DapClient`
only ever does `launch`, never `attach` — so "debug a single test" cannot reuse it as-is and is
correctly deferred (see multi-project-debug above, item 2, which lands first).

**New project**: `IdePlusPlus.TestRunner` (`TestCase.cs`, `TestOutcome.cs`, `TestResult.cs`,
`TestDiscoveryService.cs`, `TestRunService.cs`, `TrxParser.cs`, isolated in its own file since TRX's
schema is VSTest-internal and has shifted across SDK releases before).

**New files elsewhere**: `src/IdePlusPlus.ProjectSystem/TestProjectDetector.cs`,
`src/IdePlusPlus.Shell/ToolPanes/TestExplorerPaneViewModel.cs`, `TestTreeNode.cs`.

**MVP scope**: discover test projects already loaded in the solution (via `IsTestProject`/package
fallback, reusing `SolutionLoader`'s already-evaluated objects); list tests via `--list-tests` into a
project→class→method tree in a new docked pane; Run All/Selected/Failed via `--filter`+`--logger trx`,
one process per project sequentially (parallel-across-projects is out of scope); parse the TRX for
per-test outcome/duration/failure, updating tree icons live as results come in; a pane/status-bar
summary ("42 passed, 1 failed, 3 not run"); best-effort double-click navigation to a failure's
file:line; a Cancel button killing the process tree, mirroring `RunService.Stop()` (note:
`BuildService`'s own `CancellationToken` parameter is dead code today — nothing wires a Build-cancel
button to it, so this is genuinely new capability, not parity work).

**Deferred**: "debug a test" (explicitly cross-referenced to the multi-project-debug design — needs
either a new `attach` code path plus `VSTEST_HOST_DEBUG=1`-style wait-for-attach plumbing, or bypassing
`dotnet test` entirely to launch the test host DLL directly under netcoredbg), live test-impact
analysis, code coverage, parallel test-project execution, non-xUnit `--list-tests` output quirks.

**Real risks**: both TRX's schema *and* `--list-tests`'s console-text output are VSTest-internal and
undocumented/unstable across SDK versions — isolate both behind their own files/interfaces (as
scoped) so a future SDK bump's breakage is a one-file fix. Discovery requires the project to already
be built — trigger discovery after a successful Build, not solution load alone, or a fresh clone shows
zero/stale tests. `IsTestProject` heuristic is best-effort, not a guarantee. Failure-to-file:line
mapping needs a portable PDB and only works when the assertion's own frame is at the top of the stack
— unlike `BuildDiagnostic`'s always-exact navigation, this should fail gracefully, not assume success.

---

## 4. Roslyn-based IntelliSense/diagnostics

**Summary**: Stand up a background-loaded `Microsoft.CodeAnalysis.MSBuild` workspace that reports
live compiler diagnostics as squiggly underlines in the editor and as rows in the existing Error List
pane (tagged Build vs. Live), and drives a member-list completion popup via
`AvalonEdit.CodeCompletion.CompletionWindow` on `.`/Ctrl+Space — running alongside, not instead of,
the existing single-line DeepSeek FIM ghost text. Signature help, go-to-definition, find-all-references,
and rename are explicitly deferred.

**Grounded in**: `TextEditorHost.xaml.cs` hosts one AvalonEdit `TextEditor` per document with a
`BreakpointMargin`, a `CurrentLineBackgroundRenderer`, and a `GhostTextGenerator` — the established
pattern (pure "compute regions from text" + a thin AvalonEdit-facing renderer) that a diagnostics
squiggle renderer should copy. `EditorServices.cs` is the "ambient static locator" pattern
`TextEditorHost` already uses for singletons (it's built by an implicit WPF `DataTemplate`, not DI) —
new Roslyn hooks should follow this, not invent DI-into-`UserControl`. **Load-bearing fact**:
`SolutionLoader.cs` does NOT keep its MSBuild `Project` evaluations alive (immediately
`UnloadProject`s them after reading file lists) — a Roslyn `MSBuildWorkspace` cannot reuse anything
from it and must independently load the same `.sln`/`.csproj` files, meaning solution load now does
two full, non-shareable MSBuild passes back-to-back. `Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults()`
is already called exactly once, at the correct time (literally the first line of `App.OnStartup`), so
a Roslyn workspace constructed after DI setup is safe on that front. `Directory.Packages.props`
confirms no `Microsoft.CodeAnalysis.*` package is referenced yet. AvalonEdit 6.3.1.120 already
includes `AvalonEdit.CodeCompletion`, so the completion popup UI itself needs no extra package.

**New project**: `IdePlusPlus.Roslyn` (`RoslynWorkspaceService.cs`, `RoslynCompletionService.cs`).

**New Editor-side abstractions** (no Roslyn types leak into `IdePlusPlus.Editor`):
`ILiveDiagnosticsProvider.cs`, `IMemberCompletionProvider.cs`, `DiagnosticSquiggleRenderer.cs`,
`RoslynCompletionData.cs`.

**MVP scope**: background `MSBuildWorkspace` load per opened solution, independent of and in parallel
with `SolutionLoader`'s tree load; incremental push of in-editor text changes into the workspace so
diagnostics track unsaved edits; live diagnostics as squiggles (colored by severity) and as Error List
rows with a visible Build/Live distinction (`BuildDiagnostic` gains a `Source` enum so both share one
collection); a member-list completion popup on `.`/Ctrl+Space, coexisting with DeepSeek FIM ghost text
without the two UIs overlapping at the same caret position. **Sequencing note from the synthesis**:
gate the workspace load to start after NuGet's restore-on-load completes (or skip if disabled), since
an unrestored project produces a wall of near-meaningless CS0246 errors from `MSBuildWorkspace`'s
design-time build.

**Deferred**: signature help, go-to-definition, find-all-references, rename (all incremental once
`RoslynWorkspaceService`'s `FilePath`↔`DocumentId` map exists, but each needs its own UI surface that
doesn't exist yet, e.g. a Peek/Find-Results pane); live workspace reload on project/solution file
changes (add/remove/rename a source file, edit a `.csproj`) — MVP only reflects text edits to files
that existed at solution-load time.

**Real risks**: doubling first-load I/O/CPU cost (a second full MSBuild pass) — must be genuinely
cancellable and never block Build/Run/UI. Version skew between the pinned
`Microsoft.CodeAnalysis.Workspaces.MSBuild` package and whatever MSBuild `MSBuildLocator` resolves at
runtime can surface as `MissingMethodException`/`FileLoadException` on some machines/SDK installs but
not others — needs explicit smoke-testing against the actual target SDK version(s), not just "it
compiled". Multi-targeted (`TargetFrameworks`, plural) projects need explicit handling — Roslyn's
`OpenSolutionAsync` doesn't automatically share `SolutionLoader`'s existing TFM-picking logic. A live
`Compilation` held for the whole solution's lifetime is the single largest new memory cost this
feature adds (potentially hundreds of MB to low GB) — must be disposed on solution close. Roslyn's
`Workspace` is not safely mutated from multiple threads — serialize all updates onto the UI thread as
the existing ghost-text debounce timer already does.

---

## 5. Plugin API v2 + sandboxing

**Summary**: Extends the plugin contract from 3 methods to a real extension surface — editor and
Solution Explorer context menus, one dockable AvalonDock tool window per plugin, an editor hover
provider, keybindings, and chat-tool registration mirroring the MCP `ToolDefinition` shape — while
replacing plain `Assembly.LoadFrom` with a per-plugin collectible `AssemblyLoadContext` and narrowing
every new host callback to capability-scoped contexts. **Both isolation tiers are explicitly framed
as hardening against accidents, not a security boundary against a hostile plugin**; real isolation is
called out as a future out-of-process (VS Code Extension Host style) v3.

**Grounded in**: `IIdePlugin`/`IPluginHost` today are exactly `{Name, Initialize}` and
`{RegisterHighlighting, RegisterToolsMenuCommand, LogInfo}` — no context menus, tool windows, editor
extension points, keybindings, or chat-tool registration, matching roadmap.md's own gap list verbatim.
`PluginLoader.cs` loads every DLL via plain `Assembly.LoadFrom` into the Default `AssemblyLoadContext`
— no isolation, no unload capability. `PluginHost.cs` already exemplifies the capability-scoped
pattern this design continues (privately holds `MainWindowViewModel`/`OutputPaneViewModel`, exposes
only 3 narrow methods). `samples/SamplePlugin/HelloWorldPlugin.cs` is a real, working first-party
plugin exercising both existing methods — the one concrete backward-compat obligation: **the original
3 members must be treated as permanently frozen, additions only**, since even a same-named-method
signature change breaks an already-compiled plugin binary's metadata tokens at load time.
`CopilotChatPaneViewModel.cs`'s tools-list aggregation (~line 513) and tool-call routing switch
(~line 538) are the exact splice point for plugin chat tools. AvalonDock docking is 100% static XAML
today (named `LayoutAnchorable` elements, imperative `Content` assignment, a fixed `_contentById`
dictionary built once at construction) — dynamic pane registration is real new plumbing, not an
extension. `SecureSettingsStore`'s DPAPI secrets are decryptable by any code running as the same
Windows user — confirming in-process plugin code is a real secret-exfiltration risk today, not
theoretical.

**New files**: `src/IdePlusPlus.PluginContracts/PluginContextMenuContext.cs`, `IPluginHoverProvider.cs`,
`PluginToolDefinition.cs`; `src/IdePlusPlus.Shell/PluginToolWindowRegistry.cs`,
`PluginKeyBindingRegistry.cs`, `PluginChatToolRegistry.cs`, `PluginLoadContext.cs`, `LoadedPlugin.cs`.

**MVP scope**: additive-only `IPluginHost` v2 (6 new methods, existing 3 byte-for-byte unchanged);
editor + Solution Explorer context-menu contribution points; one dockable tool window per
registration, tolerant of the owning plugin being uninstalled at layout-restore time; one editor
extension point — hover, chosen over completion/code-lens for MVP; keybinding registration with
collision rejection against the 9 existing reserved gestures (never silently shadows F5/etc.);
chat-tool registration qualified `"plugin__{PluginName}__{ToolName}"` (mirroring MCP's
`"{ServerName}__{Name}"` convention), spliced into the existing tools aggregation/dispatch point,
structurally excluded from the `ProposeEditTool`/`ProposePlanTool` apply-to-disk routes. **Tier 1**:
real per-plugin collectible `AssemblyLoadContext`, forwarding shared/host assemblies (PluginContracts,
WPF assemblies, AvalonEdit, AvalonDock, System.Text.Json) to the Default ALC to preserve type identity,
private dependencies resolved via `AssemblyDependencyResolver`. **Tier 2**: every new registration
takes narrow, single-purpose contexts/delegates, never `IServiceProvider` or `MainWindowViewModel`
itself. A WeakReference-based load/unload GC test is a **hard gate**, not optional polish, before
Tier 1 is considered done.

**Deferred**: completion/code-lens extension points, multiple tool windows per plugin, a wired-up
"reload plugin" command, plugin manifest/versioning/signing, per-plugin permission-consent UI, and
**Tier 3** — a genuine out-of-process, RPC-isolated plugin host (the actual security boundary),
likely reusing `McpClient.cs`'s already-proven stdio JSON-RPC plumbing rather than inventing new IPC.

**Real risks**: `AssemblyLoadContext.Unload()` doesn't throw or report failure when references
remain — it just silently stays alive forever; every path that hands a plugin's object graph to
long-lived host state (the `IIdePlugin` instance, any tool-window `UserControl` referenced by
AvalonDock's visual tree and `layout.xml`, event subscriptions via `+=`, static fields inside the
plugin's own type) must be released on unload or the context leaks invisibly. Dynamic AvalonDock pane
registration against a 100%-static-XAML docking system is likely the single largest risk item in this
feature. **Tier 2 is advisory only**: `AssemblyLoadContext` doesn't hide other loaded assemblies from
reflection — `AppDomain.CurrentDomain.GetAssemblies()` still enumerates the whole process, so a
plugin can `Activator.CreateInstance(typeof(SecureSettingsStore))` directly regardless of how narrow
`IPluginHost` is; this must be stated plainly wherever "sandboxing" is described, never sold as
protection against a hostile plugin. Adding `<UseWPF>true</UseWPF>` to `PluginContracts.csproj` (today
a literal 2-line, deliberately dependency-free assembly) for the tool-window factory signature is a
one-way door worth flagging explicitly. Chat-tool registration touches the highest-stakes existing
code path (real file-editing via `ProposeEditTool`) and must be try/catch-isolated so a throwing
plugin tool degrades one chat turn, not the app.
