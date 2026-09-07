# VBA/Access language support — scoping & roadmap

Editing VBA code that lives inside a company Microsoft Access application, using
IDEpp as the editor. Scoped 2026-09-07 via a multi-agent research pass across
this repo and the separate [AccessMCP](https://github.com/) repo
(`C:\Users\markp\source\repos\AccessMCP`), which already has COM automation
against Access's VBE object model.

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
