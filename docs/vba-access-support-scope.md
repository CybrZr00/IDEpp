# VBA/Access language support — scoping & roadmap

Editing VBA code that lives inside a company Microsoft Access application, using
IDEpp as the editor. Scoped 2026-09-07 via a multi-agent research pass across
this repo and the separate [AccessMCP](https://github.com/) repo
(`C:\Users\markp\source\repos\AccessMCP`), which already has COM automation
against Access's VBE object model.

## Progress (updated 2026-09-07)

Phases 0-3 are done and verified live against the real production database
(`C:\FAM\FAM_form.mdb`, a private working copy, 1116 VBA modules). What
actually shipped diverged from the original plan in one real way: **the
plugin references `AccessMCP.Core` in-process rather than going through
`McpClient`** — `IPluginHost` has no "open a file in the main editor"
capability, so instead of routing through `EditorWorkspace`, the tool window
embeds its own AvalonEdit editor directly. This also means Push gets the real
exception message on failure (including the self-verifying-write/rollback
outcome below), not the MCP tool surface's generic error wrapper.

- **Phase 0**: done. `AccessMCP.exe` wired into `mcp-servers.json`.
- **Phase 1**: done, and hardened further than planned. Real live testing
  found `access_import_module_code` could silently corrupt content or throw
  partway through a rewrite — a genuine Access COM automation reliability
  issue, reproduced independent of this codebase via plain PowerShell
  automation. Fixed with a self-verifying write: every write is read back
  immediately, and on any failure it attempts to restore the pre-write
  content and reports plainly whether that rollback verified. The write
  mechanism itself moved from `AddFromString` to line-by-line `InsertLines`,
  which held up better in testing, though the self-verifying wrapper is what
  actually guarantees safety. IDEpp side: `samples/AccessVbaPlugin` (a tool
  window with a module list, Pull/Push, and an
  `access_export_all_modules_to_folder` "Export All to Folder" button — the
  git-version-control deliverable originally slated for Phase 2, built here
  instead since it fit naturally alongside Pull/Push).
- **Phase 2**: done (2026-09-13). VBA syntax highlighting shipped (`Vba.xshd`,
  loaded directly into the tool window's embedded editor rather than via
  `RegisterHighlighting`, which only applies to file-based documents). Export
  now uses `VBComponent.Export` (hidden `Attribute VB_Name=`/class-header
  lines preserved, files re-saved as UTF-8) and writes a manifest of each
  module's code-body hash; every module is individually verified to be
  round-trippable (stripping the headers reproduces `CodeModule.Lines`
  exactly — all 1116 modules in the real database verified). A new
  `access_import_changed_modules_from_folder` tool, with an "Import Changed
  from Folder" button, pushes back only files whose body changed since the
  export, refuses modules that changed inside Access meanwhile (stale), and
  reuses the backed-up/self-verifying write per module in one Access session.
  Re-export preserves files with unpushed local edits unless told otherwise.
  This makes the export folder a genuine two-way, git-backed workflow —
  the file-based round trip the original plan called for, on top of the
  live Pull/Push.
  Along the way the write itself was fixed for real: the earlier
  "content did not verify" failures were traced to Access leaving one phantom
  empty trailing line when a module is emptied and re-inserted line by line.
  The write is now a minimal line-level edit (`ReplaceLine` where lines
  differ, insert/delete only at the tail, trailing-blank fixup) and never
  empties the module. The earlier garbage-bytes corruption traced back to
  `AddFromString`, which is no longer used anywhere. Note that a full export
  takes ~1.5-2 minutes for this project (`VBComponent.Export` is much slower
  than reading `Lines()`); import of a handful of changed modules is seconds.
- **Phase 3**: done. A Compile Check button wraps the existing
  `access_compile_vba` tool. Tested carefully given the real risk of a hidden
  compile-error dialog hanging automation (`Visible=false`) — completed in
  ~11s with no hang, and turned out lower-risk than expected: every
  successful write test had already exercised this same compile+save command
  as a side effect of `access_import_module_code`'s default `compile=true`.
- **Phase 3+**: done (2026-09-13). Proactive drift detection for the export
  folder: `access_check_export_drift` (read-only) classifies every module as
  unchanged / drifted in Access / edited locally / conflict (both) / local
  already matches Access / new in Access / removed from Access / file
  missing. `access_pull_drifted_modules` re-exports clean drifts (and new
  modules) and updates their manifest base; for conflicts it writes the live
  version alongside as `<file>.live` and leaves the local file *and* its base
  untouched, so import keeps refusing until the module is passed in
  `markResolved` after merging by hand — which adopts the live version as the
  base without touching the file. Tool-window buttons: Check Drift, Pull
  Drifted, Mark Selected Resolved; and a single-module Pull now notes whether
  the module differs from the export folder's copy. Verified against the real
  database end to end, including the full conflict path through to importing
  the merged result. A check or pull takes ~10s (it reads every module's code
  but only re-exports what changed).
- **Phase 4**: done, first cut (2026-09-13). Completion and hover in the tool
  window's own editor — auto on `.` or Ctrl+Space, tooltips on identifiers —
  so no core IDEpp change was needed after all (`IMemberCompletionProvider`
  only serves the main editor). Symbols come from a purpose-built,
  declaration-level VBA parser over the export folder (`Symbols/`): every
  module's procedures with parameters and locals, fields, constants,
  Declares, Events, Enums and Types. Member completion resolves locals,
  parameters and fields typed as class modules, enums or user types,
  standard modules, `Me` in form/report code-behind, `With` receivers, and a
  curated table of Access/DAO objects (`DoCmd`, `Recordset`, `Form`,
  `Control`, ...) whose members live in COM type libraries the plugin never
  reads. Global completion is prefix-filtered and ordered locals > module >
  project > language, with the language vocabulary read from `Vba.xshd`.
  **Decision:** this was chosen over embedding Rubberduck's full ANTLR
  grammar for the first cut — much smaller integration surface, verifiable
  now, and the parser sits behind an interface a real grammar could replace
  later for precision (e.g. expression typing beyond declared types). Tested
  by a new `samples/AccessVbaPlugin.Tests` project (parser, index, engine,
  and the real 1116-module corpus when present). Not exercised by hand in
  the running IDE yet — the popup and tooltip mechanics are AvalonEdit's
  standard `CompletionWindow`/`ToolTip`, but worth a real try.
  **Follow-up (2026-09-13):** inline FIM ghost text (DeepSeek, when configured)
  in the tool window's editor too — via the first and only core IDEpp change
  of the whole effort, an *additive* `IPluginHost.GetInlineCompletionAsync`
  that forwards to `EditorServices.CompletionProvider` (null when no model is
  configured). The plugin mirrors `TextEditorHost`'s driver: 350ms debounce,
  4000/1000-char prefix/suffix, stale-result guards, Tab accepts, Esc or a
  caret move clears, and it's suppressed while the completion popup is open
  or a module is being loaded. Ghost text is rendered by a plugin-local copy
  of `GhostTextGenerator` (IdePlusPlus.Editor isn't reachable from a plugin;
  AvalonEdit is). Existing plugin binaries are unaffected — plugins consume
  the interface, only the host implements it.
  Also learned: the "intermittent crash" of `AccessVbaPluginCompatibilityTests`
  was almost certainly the external `timeout` wrapper I was running it under
  killing a slow from-scratch Release build inside the test (vstest reports a
  killed host as "crashed"); no crash was ever recorded in the event log, and
  it has never failed unwrapped. Don't run that test under a tight timeout.

## Recommendation

Build the phased hybrid: file-based export/import through AccessMCP, edited as
ordinary files in IDEpp's existing plugin extension points — starting with the
zero-code MCP-wiring step (Phase 0).

The deciding facts:

- IDEpp already has a working stdio MCP client
  (`src/IdePlusPlus.Copilot.Mcp/McpClient.cs`,
  `src/IdePlusPlus.Copilot.Mcp/McpServerConfigStore.cs`). Phase 0 costs
  nothing to build — pointing it at `AccessMCP.exe` gets VBA browsing into the
  chat pane immediately.
- AccessMCP's write path (`access_import_module_code`, whole-module
  `DeleteLines`+`AddFromString` replace in `AccessMCP/AccessTools.cs`
  L1099-1151) exists but has never been exercised against a live Access
  instance — its only test coverage (`AccessMCP.Tests/AccessVbeToolsTests.cs`)
  asserts it throws when `EnableComAutomation=false`. Hardening that one write
  path is unavoidable regardless of which IDEpp-side architecture sits on top
  of it, so there's no payoff to a more expensive "live COM-backed virtual
  document" architecture.
- IDEpp has no virtual-document seam (`DocumentViewModel` is file-path-only —
  `src/IdePlusPlus.Editor/DocumentViewModel.cs:8-62`; `EditorWorkspace.Save`
  hardcodes `File.WriteAllText` — `src/IdePlusPlus.Editor/EditorWorkspace.cs:108-129`),
  so a live-editing architecture would mean a whole parallel save pipeline for
  no additional diagnostic or safety benefit — Access's own compiler
  (`acCmdCompileAllModules`) only returns pass/fail, not line numbers, in
  every architecture considered.

## Decisions (resolved 2026-09-07)

- **Access app usage:** multiple concurrent users have the front-end open
  live. This means drift detection (someone else's edit landing between pull
  and push) is a real near-term risk, not a speculative one — folded into
  Phase 1 below rather than deferred.
- **Working copy:** editing a private working copy of the front-end (standard
  front-end/back-end split) is acceptable — the whole file-based plan assumes
  this.
- **End goal:** the user wants live-editing parity eventually, but the
  cheaper, higher-value read of that is fast pull/push plus *real* semantic
  completion, not literally bypassing files to edit the running Access
  instance directly. The literal live-COM architecture remains a fallback if
  the file-based flow ever proves genuinely too slow in practice, but nothing
  in the research suggests it buys anything the phases below don't already
  cover.
- **Licensing:** GPLv3 code is acceptable for this personal/internal tool, so
  Rubberduck's ANTLR VBA grammar can be referenced for real parsing later
  (Phase 4) — as long as the plugin stays personal/internal rather than
  publicly distributed, since GPL obligations bite on distribution.

## Current state

**AccessMCP** (`C:\Users\markp\source\repos\AccessMCP`, MIT-licensed):

- Read tools: `access_vbe_list_projects`, `access_vbe_list_modules`,
  `access_vbe_get_module_code`, `access_vbe_get_proc_info`
  (`AccessMCP/AccessTools.cs` L733-978), all via
  `VBE.VBProjects`/`VBComponents`/`CodeModule`.
- Write/compile tools already exist per the README:
  `access_export_module_code`, `access_import_module_code`,
  `access_compile_vba` — but `access_import_module_code` is untested against
  a live Access instance (see Recommendation above).
- COM lifecycle (`AccessMCP/AccessComAutomation.cs` L62-133) runs on a
  dedicated STA thread, opens/closes cleanly, always releases the COM object
  (L249-255).
- Compile-diagnostics primitive: `AcCmdCompileAllModules` /
  `AcCmdCompileAndSaveAllModules` constants (L22-23) — pass/fail only, no
  line/column info.
- Config gates relevant to safety: `ReadOnly`, `EnableComAutomation`,
  `AllowedDatabasePaths` (`AccessMCP/AccessMcpOptions.cs`). VBE tools also
  require Access Trust Center → "Trust access to the VBA project object
  model" enabled manually in Access itself.

**IDEpp** (this repo):

- No virtual-document concept — `DocumentViewModel`
  (`src/IdePlusPlus.Editor/DocumentViewModel.cs:8-62`) is file-path-only.
- Working MCP client already in place:
  `src/IdePlusPlus.Copilot.Mcp/McpClient.cs`,
  `McpServerConfigStore.cs` (config lives at
  `%LOCALAPPDATA%\IdePlusPlus\mcp-servers.json`, a flat array of
  `{ Name, Command, args, env }`).
- Plugin extension points usable with zero core changes:
  `RegisterHighlighting`, `RegisterHoverProvider`, `RegisterToolWindow`,
  `RegisterChatTool` (`src/IdePlusPlus.PluginContracts/IPluginHost.cs`).
- Real semantic completion (`IMemberCompletionProvider`) is currently a single
  Roslyn/C#-only global slot (`EditorServices.MemberCompletionProvider`), not
  dispatched per file extension — relevant to Phase 4 below.

## What's genuinely hard here

- **Live-file corruption** is a documented Access failure mode (stale p-code
  after a module rewrite) — no tooling here has a backup/rollback step yet.
- **No version control exists today.** This plan introduces git for the VBA
  for the first time, via export, not modifying an existing setup.
- **Multi-user safety isn't enforced by tooling** — the private-copy
  discipline is a process convention, not something AccessMCP or IDEpp
  detects or requires today.
- **VBA's grammar is non-standard** and the one real parser (Rubberduck) is
  GPLv3 at the parser level — acceptable per the licensing decision above, but
  still real integration work, not a drop-in library.
- **No line-level diagnostics feed exists** — `acCmdCompileAllModules` +
  `IsCompiled` is pass/fail only.

## Phased roadmap

**Phase 0 — MCP wiring only (days).** Add `AccessMCP.exe` as a configured
server in `mcp-servers.json`; use `McpClient.cs` as-is. Zero new code in
either repo. Deliverable: read-only project/module/code browsing for the VBA
in chat.

**Phase 1 — File materialization + tool window, with a staleness guard
(1-2 weeks).** In AccessMCP: harden and actually test
`access_import_module_code` against a live Access instance for the first
time; add a pre-write backup/snapshot. Because the app is multi-user, bake a
staleness check into push: re-pull the live module immediately before
writing, compare against what was last pulled, and refuse (or show a diff)
on drift — not a full three-way merge, just a guard against silently
clobbering someone else's edit. In IDEpp: new plugin
(`IdePlusPlus.Plugins.AccessVba`) using `RegisterToolWindow` (Pull/Push
buttons) and `RegisterChatTool`, calling AccessMCP through the existing
`McpClient`. Exported files open through unmodified
`EditorWorkspace.Open`/`Save` — no `DocumentViewModel` changes.

**Phase 2 — Grammar + round-trip fidelity (1 week).** Port a
permissively-licensed VBA TextMate grammar to an AvalonEdit `.xshd`, wired via
`RegisterHighlighting(".bas"/".cls"/".frm", ...)`. Switch export to real
`VBComponent.Export` (preserves `Attribute VB_Name=` headers) instead of
`CodeModule.Lines()`, plus a module↔file↔project manifest so only dirtied
files push.

**Phase 3 — Compile-and-check button (days).** Wrap
`RunCommand(acCmdCompileAndSaveAllModulesCommand)` + `IsCompiled` as an
AccessMCP tool and an IDEpp tool-window button. Pass/fail status only,
clearly labeled as such.

**Phase 4 — Semantic completion (2-3 weeks).** Reference Rubberduck's ANTLR
VBA grammar as a library to get a real parse tree → a basic per-module symbol
table (Subs/Functions/Properties/variables) → wire into completion and hover.
Note: `IMemberCompletionProvider` is currently a single Roslyn-only global
slot, so this phase needs either a small core IDEpp change (dispatch
completion per file extension — generically useful beyond VBA) or a
plugin-owned completion popup via `RegisterKeyBinding` that bypasses the core
completion UI. Decide once here.

**Deferred, not currently planned:** literal live in-process COM editing (no
diagnostic/safety upside found over the file-based flow); forms/reports
editing (needs `SaveAsText`/`LoadFromText`, a different API surface than
`VBComponents`); full three-way merge/conflict resolution beyond the Phase 1
staleness guard.

## Effort estimate

Phase 0 through Phase 4: roughly 5-7 weeks of solo-developer effort. Most of
the early time is in AccessMCP (hardening and first-time live testing of the
write path); IDEpp stays a thin tool-window/highlighting/completion plugin
throughout.
