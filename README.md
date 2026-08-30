# IDE++

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A from-scratch Windows IDE for .NET, built with WPF, with an AI coding agent built directly into
the shell rather than bolted on as an autocomplete widget.

## A note on the name

This project started out inspired by, and nearly named after, Notepad++ — back when the plan was a
fast, lightweight text editor. Somewhere along the way it grew a project system, a debugger, Git
integration, NuGet package management, a Test Explorer, Roslyn-powered IntelliSense, a plugin
system, and an AI coding agent with its own tool-calling loop, and "notepad" stopped describing it
by any reasonable definition. By the time that was obvious, the `++` suffix had already stuck — so
what shipped is **IDE++**: a nod to where the idea started, and a bit of a misnomer, since this was
never really a notepad to begin with.

## What's here

### Core IDE
- Solution Explorer for `.sln`/`.slnx` solutions, multi-project aware throughout
- An AvalonEdit-based editor: syntax highlighting, code folding, single-line AI ghost-text
  completion, and Roslyn-powered member completion + live diagnostics (squiggles)
- Build / Run / Debug: Debug/Release configurations, multi-project startup configurations,
  attach-to-process, and breakpoints (including exception filters) via a hand-rolled Debug Adapter
  Protocol client driving `netcoredbg` (see **Setting up debugging** below — this one needs a
  manual step)
- Git integration: status, stage, commit, push/pull/fetch, branch switching, and an inline diff
  viewer shared with the AI Copilot's own edit-review flow
- NuGet package management: search, add, remove, restore
- Publish profiles: reads and writes standard `Properties/PublishProfiles/*.pubxml`, so profiles
  written by Visual Studio work here and profiles written here work from `dotnet publish`. Folder
  and container-image targets; profiles using a protocol IDE++ cannot run (MSDeploy, ClickOnce,
  Azure) are listed read-only rather than hidden or overwritten
- Test Explorer: discovers and runs `dotnet test` suites, jumps straight to a failing assertion
- A plugin API (context-menu commands, dockable tool windows, hover providers, keybindings, and
  chat-tool registration) with each plugin isolated in its own `AssemblyLoadContext`

### The AI Copilot agent
- Works against **OpenRouter** (any hosted model) or a local **LM Studio** server — your choice of
  provider, your call on cost and privacy
- A real tool-calling loop: reading/searching files and symbols, git diff/status, building,
  publishing with an existing profile, running tests, live diagnostics, and — opt-in, confirmed per
  use — running shell commands or fetching a public webpage
- A `propose_edit`/`propose_plan` diff-review workflow, with an Auto Mode toggle for when you'd
  rather it just get on with it
- **Automated skill creation** — the agent can save a reusable procedure to disk and reload it in a
  later session instead of re-explaining it every time
- **Adversarial review** — a one-click, independently-prompted second opinion on a diff, from a
  model with no memory of having written it
- **Parallel sub-agents** — fans out several independent, read-only investigations at once instead
  of researching them one at a time
- **Scheduled automations** — human-authored, read-only tasks that run on a timer while the IDE is
  open
- Model Context Protocol (MCP) support, configurable globally or per-solution, alongside the
  plugin-registered chat-tool mechanism

## Getting started

### Prerequisites
- Windows 10/11 (this is a WPF app — Windows only)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Git (used both to clone this repo and by the built-in Git pane)

### Build and run from source
```bash
git clone https://github.com/CybrZr00/IDEpp.git
cd IDEpp
dotnet build IdePlusPlus.slnx
dotnet run --project src/IdePlusPlus.App
```

### Prebuilt installer
There's a WiX-based MSI installer project too, if you'd rather not build from source every time —
see [installer/README.md](installer/README.md) for the one-command build.

### Setting up debugging (don't skip this one)

IDE++'s debugger drives **[netcoredbg](https://github.com/Samsung/netcoredbg)** — Samsung's
open-source .NET Core debugger — over the Debug Adapter Protocol. It isn't bundled or restored
automatically, and **Start Debugging / Attach to Process will fail** with a clear
"netcoredbg not found" error until it's in place:

1. Download the Windows x64 build from the
   [netcoredbg releases page](https://github.com/Samsung/netcoredbg/releases).
2. Extract it so the executable ends up at exactly:
   ```
   %LOCALAPPDATA%\IdePlusPlus\tools\netcoredbg\netcoredbg\netcoredbg.exe
   ```
   (`netcoredbg` appears twice on purpose — the release archive already contains its own
   `netcoredbg` folder, which needs to land one level inside `tools\netcoredbg\`.)

Everything else — building, running, Git, NuGet, Test Explorer, the AI Copilot — works without
this. Only stepping through code needs it.

### Setting up the AI Copilot

The Copilot Chat pane needs a model provider before it can do anything, configured under
**Tools → Settings**:
- **OpenRouter** (cloud, any hosted model) — get an API key from [openrouter.ai](https://openrouter.ai/).
- **LM Studio** (local, free, private) — run a model in [LM Studio](https://lmstudio.ai/) with its
  local server enabled, then point IDE++ at it. No API key needed.

The Web/Shell tool toggles, custom system prompt, and scheduled automations are all configured from
the Copilot Chat pane and its Scheduled Automations tab directly.

### Extending IDE++
- **Plugins** implement `IIdePlugin`/`IPluginHost` from `IdePlusPlus.PluginContracts` —
  `samples/SamplePlugin` is a small, complete example (a syntax highlighting definition plus a
  Tools-menu command).
- **MCP servers** can be added globally (`%LOCALAPPDATA%\IdePlusPlus\mcp-servers.json`) or scoped to
  one solution (`.ideplusplus/copilot/mcp-servers.json`); their tools show up in the Copilot's
  tool-calling loop automatically once connected.

## Project layout

```
src/         The application itself, split into ~20 single-purpose class libraries
tests/       Test projects mirroring that split, one per area
samples/     A sample console app, a sample plugin, and a multi-target sample project
installer/   The WiX MSI installer project (see installer/README.md)
docs/        Design notes and the feature roadmap
```

## Contributing

This started as, and largely remains, a personal project, but issues and pull requests are welcome.
[`docs/roadmap.md`](docs/roadmap.md) has a detailed sense of what's already been considered, what
shipped, and what's explicitly still out of scope.

## License

[MIT](LICENSE) © Mark Perry

## Acknowledgments

Built on top of some excellent open-source projects, including:
- **[netcoredbg](https://github.com/Samsung/netcoredbg)** (Samsung) — the .NET debugger IDE++
  drives via the Debug Adapter Protocol; see **Setting up debugging** above, since it's the one
  dependency you have to install yourself
- **[AvalonEdit](https://github.com/icsharpcode/AvalonEdit)** and
  **[AvalonDock](https://github.com/Dirkster99/AvalonDock)** for the editor and docking layout
- **[Roslyn](https://github.com/dotnet/roslyn)** for IntelliSense, live diagnostics, and symbol search
- **[DiffPlex](https://github.com/mmanela/diffplex)** for diffing
- **[WiX Toolset](https://wixtoolset.org/)** for the MSI installer
