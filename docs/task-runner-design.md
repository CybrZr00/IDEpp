# Task Runner Explorer — design

Visual Studio's Task Runner Explorer discovers tasks from `package.json`, `gulpfile.js` and
`Gruntfile.js`, lists them in a dockable pane, runs one on double-click with its output in a per-task
console, and lets a task be *bound* to a point in the IDE lifecycle (Before Build, After Build,
Clean, Project Open) so it runs automatically. IDE++ has none of this today.

This is the design for getting there in three shippable phases, ordered so the phase that needs no
Node.js on the machine lands first and the phase that executes repository-authored JavaScript lands
last.

## Where this sits today

- `BuildService` is `dotnet`-only. It shells out to the `dotnet` CLI with a single-build-at-a-time
  guard (`IsRunning`) that also covers publish, since `dotnet publish` writes the same `obj/bin`.
- `MainWindowViewModel.RunBuildVerbAsync` is the single funnel for the Build/Clean/Rebuild commands
  — but **not** the only build entry point: `ResolveOutputDllAsync` (the implicit build behind
  Run/Debug) calls `_buildService.BuildAsync` directly, and the copilot's `build` tool is a third
  caller. Anything that claims to run "before every build" has to account for all three.
- `ShellCommandRunner` already solves the hard part of running Node tooling on Windows — it kills
  the whole process tree on timeout/cancel precisely because "npm spawns node" — but it is an
  agent-facing, confirmed-per-use tool, not a task runner.
- `IPluginHost` has no build-lifecycle member, so a plugin cannot hook build events at all today.
- Per-solution state goes in `.ideplusplus/*.json` (`publish.json`, `startup-configurations.json`,
  the copilot's `schedules.json`), read with a degrade-to-empty posture on malformed input.
- The Solution Explorer tree is built from evaluated MSBuild items (`None`/`Content`/`Compile`/…),
  so a `package.json` inside an SDK-style project directory is *already* a `SolutionTreeNode` — but
  a repo-root `package.json` sitting outside every project directory is not.

---

## Phase 1 — npm-script Task Explorer (no Node.js required) *(shipped)*

**Summary**: A new `IdePlusPlus.Tasks` project (sibling to `IdePlusPlus.Build`) that finds
`package.json` files in and around the open solution, parses their `scripts` object, and exposes
them as `TaskDefinition`s. A `TaskExplorerPaneViewModel` renders them as a manifest → task tree in a
new dockable pane with Run/Stop/Refresh, streaming output into the existing Output pane. Discovery is
a pure `System.Text.Json` parse — nothing is executed — which is exactly why this phase can ship
without any consent prompt or Node.js dependency.

**Grounded in**: `LaunchSettingsLoader`'s "read a well-known JSON file next to a project, degrade to
nothing on malformed input" shape, reused almost verbatim for `package.json`. `BuildService`'s
`ProcessStartInfo` + `ArgumentList` + `OutputDataReceived` streaming and its
`KillBestEffort(entireProcessTree: true)` cancellation, mirrored for the task process.
`TestExplorerPaneViewModel`/`TestTreeNode` for the two-level tree pane with per-node run commands.
`PublishSelectionStore` for the `.ideplusplus/` persistence convention.

**New project**: `IdePlusPlus.Tasks` —

- `TaskManifestKind.cs` (`NpmScripts`, `Gulp`, `Grunt`), `TaskDefinition.cs`, `TaskManifest.cs`
- `PackageJsonReader.cs` — `scripts` and the corepack `packageManager` field; malformed → no tasks
- `PackageManagerDetector.cs` — `pnpm-lock.yaml` / `yarn.lock` / `package-lock.json`, with the
  `packageManager` field winning when present
- `NodeToolLocator.cs` — resolves `npm`/`pnpm`/`yarn` to a real executable (see risks)
- `TaskManifestDiscovery.cs` — candidate manifests from the solution directory and the tree,
  `node_modules` excluded
- `TaskRunService.cs` — runs one task, streams output, kills the tree on cancel, one run at a time
  (globally, not per manifest — concurrent runs are deferred, below)

**New files elsewhere**: `src/IdePlusPlus.Shell/ToolPanes/TaskExplorerPaneViewModel.cs` and
`TaskTreeNode.cs`; an inline `DataTemplate` plus a `LayoutAnchorable` in `MainWindow.xaml` alongside
the Test Explorer (the tree needs no more markup than the Test Explorer's, which is also inline
rather than a `Views/` UserControl), its entry in `MainWindow.xaml.cs`'s ContentId map so layout
restore works — `AttachMissingCoreToolWindows` already adds the pane back for a user whose saved
layout predates it; DI registration in `App.xaml.cs`; `tests/IdePlusPlus.Tasks.Tests`.

**MVP scope**: discover `package.json` manifests (solution directory, one shallow scan; plus
`_solutionRoot.Flatten()` file nodes named `package.json`, minus anything under `node_modules`);
list each manifest's scripts; Run/Stop per task via the detected package manager
(`npm run <script>`); stream stdout+stderr into the Output pane prefixed with the task name;
double-click a manifest to open it; a Refresh command and a re-scan on solution load.

**Deferred**: per-task output consoles (the Output pane is one buffer today — prefixing is the MVP);
running several tasks concurrently; parsing task output into the Error List; `npm install` /
dependency management UI.

**Real risks**:

- **`npm` is `npm.cmd`.** With `UseShellExecute = false`, `ProcessStartInfo("npm")` resolves through
  `CreateProcess`, which appends `.exe` only — it will not find `npm.cmd`, and the failure looks like
  "npm is not installed" on a machine where it plainly is. `NodeToolLocator` must walk `PATH`
  applying `PATHEXT` itself (preferring `.cmd`), and check `node_modules/.bin` first for a locally
  installed tool. Going through `cmd.exe /c` instead is the wrong fix: .NET's `ArgumentList` escaping
  follows the C runtime's rules, not `cmd.exe`'s, so it is not injection-safe there. Note the same
  problem arrives by the back door anyway — `CreateProcess` runs a resolved `.cmd` *through*
  `cmd.exe`, which is the BatBadBut bug class — so task names (repository-authored, therefore
  untrusted) are validated against a conservative charset and refused rather than escaped.
- **`node_modules` is already in the tree.** SDK-style default globs exclude `bin`/`obj` but not
  `node_modules`, so a project with dependencies installed contributes thousands of file nodes —
  including hundreds of nested `package.json`s. Discovery must filter on a `node_modules` path
  segment, not just on filename. (That the tree itself is bloated in that situation is a separate,
  pre-existing issue and out of scope here.)
- Yarn/pnpm workspaces mean several manifests legitimately share one lockfile; detection is
  per-manifest with a walk up to the nearest lockfile, not per-directory.
- **`Process.Kill(entireProcessTree: true)` does not stop an npm task.** Found by testing against a
  real `npm run watch`, which produces `IDE++ → cmd.exe → node (npm) → cmd.exe → node (script)`:
  after the tree kill the innermost node was still alive, orphaned, and still holding the redirected
  stdout pipe, so the reader never saw EOF either. A watcher that prints output happens to die soon
  after on a broken pipe, which is what made the first pass look fine; a silent one runs forever.
  Stopping therefore goes through `ProcessJob` (a Windows job object with `KILL_ON_JOB_CLOSE`), with
  the tree kill kept as a first step. `ShellCommandRunner`, `BuildService` and `TestRunService` made
  the same bare tree-kill assumption and now use `ProcessJob` too; each has a regression test that
  spawns a deliberately orphaned grandchild, verified failing without the job assignment. For
  `BuildService` this in principle means MSBuild worker nodes die with the build rather than
  surviving for reuse — measured on the .NET 10 SDK it costs nothing, because `dotnet build` reuses
  nodes started outside the job, which are not members. Still tree-kill-only, and not looked at:
  `RunService`, `DapClient`, `McpClient`.

---

## Phase 2 — Build bindings, and a build-event member on `IPluginHost` *(shipped)*

**Summary**: Bindings let a discovered task run automatically at a lifecycle point. Two consumers
need the same hook: the Task Explorer's own bindings, and plugins. Add `BuildEventKind` /
`BuildEventContext` to `IdePlusPlus.PluginContracts` and one new `IPluginHost` member — **appended,
never modifying an existing signature**, per that interface's own maintenance contract — then route
both consumers through a single dispatch point in the shell.

**Grounded in**: `IPluginHost`'s additive-only rule and `PluginHost`'s existing wrap-and-log safety
posture (`SafeHoverProvider`; plugin exceptions logged to Output rather than crashing the IDE).
`MainWindowViewModel.RunBuildVerbAsync` as the funnel. `PublishSelectionStore` for
`.ideplusplus/tasks.json`.

**New contract members** (all new files bar the one appended interface method):

```csharp
public enum BuildEventKind { BeforeBuild, AfterBuild, BeforeClean, AfterClean }

public sealed record BuildEventContext(string SolutionPath, string Configuration, int? ExitCode);

// appended to IPluginHost:
void RegisterBuildEventHandler(
    BuildEventKind kind, Func<BuildEventContext, CancellationToken, Task> handler);
```

Adding a member to `IPluginHost` is safe for already-compiled plugin DLLs — they hold MethodRefs only
to the members they call, and a new member adds none they need to resolve. Only the IDE implements
this interface.

**Storage**: `.ideplusplus/tasks.json`, keyed by solution-relative manifest path + task name, same
degrade-to-empty posture as `PublishSelectionStore`. Bindings created in IDE++ are not visible to
Visual Studio, which is a real interop gap worth stating in the UI.

Reading Visual Studio's own `/// <binding BeforeBuild='build' />` comment — planned for this phase —
**moved to Phase 3**. That comment is a JavaScript-file convention: it lives at the top of a
`gulpfile.js`/`Gruntfile.js`, which is exactly what Phase 3 adds and Phase 2 has none of. Putting it
in a `package.json` would make the file invalid JSON, which `PackageJsonReader` parses strictly
because npm does. So there was nothing for the importer to read yet, and building it now would have
meant guessing at where VS keeps npm-script bindings instead.

**Sequencing and the build guard**: `BuildService` gains
`Task<T> RunExclusiveAsync<T>(Func<Task<T>> work)` — the existing private guard exposed as a critical
section — so a Before Build task and the `dotnet build` that follows it are one indivisible run, and
`IsRunning` is true for the whole thing. Without that, the copilot's `build` tool can start
`dotnet build` while a Before Build task is still writing `wwwroot`. Dispatch itself lives in a new
`RunBoundTasksAsync(BuildEventKind)` in `MainWindowViewModel`, called from `RunBuildVerbAsync` **and**
from `ResolveOutputDllAsync`, so F5's implicit build fires bindings the way VS does.

**Failure policy**: a bound task exiting non-zero is reported in the Output pane and does not fail the
build — matching VS, and avoiding a broken lint config making the IDE unusable. A per-binding "fail
build on error" checkbox is deferred. A plugin handler that throws is caught and logged, exactly as
plugin failures are handled everywhere else.

**Real risks**:

- **Watch tasks never exit.** `npm run watch` / `gulp watch` bound to Before Build would hang every
  build forever. Mitigations: bindings get a timeout (default 2 minutes, then kill and log),
  nothing is ever auto-bound, and the binding UI warns on a task whose name or script matches a
  watch-ish pattern. VS has this same footgun; IDE++ should at least be recoverable — which is only
  true because Phase 1's stop path goes through a job object rather than a tree kill (see its risks
  above); a timeout that cannot actually kill the watcher would be theatre.
- **Double-running.** A web project's `dotnet build` may already invoke npm through an MSBuild target.
  A Before Build binding on top of that runs it twice — worth a note in the docs rather than
  detection heuristics.

---

## Phase 3 — Gulp and Grunt discovery (needs Node.js, and consent)

**Summary**: Enumerate gulp tasks with `gulp --tasks-simple` (one task name per line) and grunt tasks
by parsing `grunt --no-color --help`, preferring the locally installed `node_modules/.bin` binary and
falling back to `npx`. Cache the result per manifest keyed on file path + mtime so a solution load
does not spawn Node.

**Why this is last**: `gulp --tasks-simple` *executes the gulpfile* to build its task graph. Listing
tasks is therefore arbitrary code execution from the repository — which is exactly what IDE++ gates
behind explicit opt-in everywhere else (the shell tool is opt-in and confirmed per use). Gate
first-time discovery per solution behind a one-time prompt naming the file, persisted as a flag in
`.ideplusplus/tasks.json`. `package.json` scripts, being a pure JSON parse, need no such gate — the
reason Phase 1 goes first.

**New files**: `GulpTaskDiscovery.cs`, `GruntTaskDiscovery.cs`, `TaskDiscoveryCache.cs` in
`IdePlusPlus.Tasks`; a consent dialog in the shell; and `VsBindingCommentReader.cs`, carried over
from Phase 2 — the `/// <binding BeforeBuild='sass' />` comment it reads only exists in the
JavaScript manifests this phase introduces. Read-only: IDE++ never rewrites the user's gulpfile.

**Real risks**: both output formats are tool-version-dependent, so each parser stays in its own file
(same containment argument as `TrxParser`). Discovery must be timeout-bounded and tree-killed — a
gulpfile that starts a watcher at module scope will otherwise never return. A gulpfile that throws
should surface its stderr, not an empty task list.

---

## Cross-cutting

**Copilot tool**: a `run_task` tool (new `TaskTools.cs`, following `PublishTools`/`BuildTools`) is a
natural addition once Phase 1 exists. It runs repository-authored code, so it takes the shell tool's
posture — opt-in, confirmed per use — and it must **not** be added to `SubAgentTools.AllowedTools` or
`ReadOnlyToolDispatcher`, both of which are read-only by design for sub-agents and scheduled
automations.

**Tests**: `tests/IdePlusPlus.Tasks.Tests` covering `package.json` script/`packageManager` parsing
(including malformed → empty), lockfile-based manager detection, `PATHEXT` resolution against a fake
PATH, `node_modules` exclusion, VS `<binding>` comment parsing, and the gulp/grunt output parsers
against captured fixtures. `IdePlusPlus.Shell.Tests` covering binding round-trip and the sequencing
rule (Before Build completes before `dotnet build` starts; a failing bound task logs and does not
abort).

**Suggested order**: Phase 1 (M) → Phase 2 (M, and the only phase touching the plugin contract) →
Phase 3 (S once Phase 1's process plumbing exists, but the consent flow is the fiddly part).

Phases 1 and 2 are shipped. Both were verified end to end against a throwaway solution driven
through the real `SolutionLoader` → discovery → pane → `TaskRunService` path, not only by unit
tests — which is how the process-tree defect above was found, and how the bound-task timeout was
confirmed to actually release the build rather than merely being configured.
