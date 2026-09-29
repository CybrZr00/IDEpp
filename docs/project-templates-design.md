# New Project / New Item templates — design

Visual Studio's File > New > Project shows a searchable, tagged list of project templates, creates
one into the open solution, and reloads. File > New > File and Solution Explorer's Add > New Item do
the same for single files — Class, Interface, API Controller — with the namespace already correct
for the folder the file lands in. IDE++ has neither: `File > _New` creates an untitled in-memory
buffer (`MainWindowViewModel.NewDocument` → `EditorWorkspace.NewDocument`), and the only way to get
a project into a solution is to create it outside the IDE.

Both features want the same two things — *what templates exist* and *run one* — so this is one new
subsystem with two front-ends, in three shippable phases: the catalog plus New Project first (the
larger ask, and the one that forces every hard question about solution mutation), New Item second
(reusing the catalog wholesale), template-parameter UI and installable template packages last.

## Where this sits today

- `BuildService` is the single funnel for every `dotnet` invocation, with a one-at-a-time guard
  (`IsRunning`) that already covers publish for exactly the reason it must cover this: `dotnet new`
  runs an implicit restore as a post-action and `dotnet solution add` rewrites the solution file, so
  neither can race a build. `RunDotnetAsync` takes a verbatim argument list precisely so a caller
  can assemble its own command without `BuildService` knowing anything about it — `PublishService`
  is the precedent.
- `SolutionLoader.Load` parses `.sln`/`.slnx` via MSBuild and evaluates each project, building the
  `SolutionTreeNode` tree from `Compile`/`None`/`Content`/`Page`/`EmbeddedResource` items. Because
  those are evaluated globs, a new `.cs` file dropped in an SDK-style project directory is already
  a project item — nothing needs to edit the `.csproj`.
- `MainWindowViewModel.LoadSolutionAsync` is the only path that refreshes the tree, and it is a
  *full* reload: clears the Output pane, re-parses via MSBuild, touches the recent-solutions list,
  re-points Copilot/SourceControl/MCP at the solution, restarts test discovery and the Roslyn
  workspace load. Fine after adding a project; far too much after adding one file.
- The Solution Explorer context menu is built in code (`MainWindow.xaml.cs`
  `SolutionExplorerTree_OnPreviewMouseRightButtonDown`) and offers four Project-node commands and
  plugin contributions. There is no Add submenu, and File/Folder nodes get nothing.
- `PublishDialog` + `PublishDialogViewModel`, opened via `ShowDialog()` from a `MainWindowViewModel`
  method, is the established shape for a modal that runs `dotnet` and streams to the Output pane.
- Per-solution state goes in `.ideplusplus/*.json`, read with a degrade-to-empty posture.
- The roadmap has carried "no New Project/New Item wizards" as a known gap since it was written.

---

## Phase 1 — the template catalog, and New Project *(shipped)*

**Summary**: A new `IdePlusPlus.Templates` project that reads the .NET SDK's own template cache to
enumerate installed templates, and instantiates one by shelling `dotnet new <template>` through
`BuildService`. On top of it, a `NewProjectDialog` (File > New > Project…, and Add > New Project… on
the Solution Explorer's solution node) that creates a project into the open solution — or creates a
solution first when none is open — and reloads.

**Grounded in**: `PublishService`'s "assemble arguments here, run them through `BuildService`'s
guard" split. `LaunchSettingsLoader`/`PackageJsonReader`'s "read a well-known JSON file, degrade to
nothing on malformed input" shape, applied to `templatecache.json`. `PublishDialog`'s modal +
view-model + Output-pane streaming. `TaskManifestDiscovery`'s in-memory caching with a re-scan
trigger.

**New project**: `IdePlusPlus.Templates` —

- `TemplateKind.cs` (`Project`, `Item`, `Solution`), `TemplateParameter.cs`, `TemplateInfo.cs`,
  `TemplateGroup.cs`
- `TemplateCacheLocator.cs` — resolves `%DOTNET_CLI_HOME%` (falling back to `%USERPROFILE%`) +
  `.templateengine/dotnetcli/<sdk-version>/templatecache.json`, with the SDK version from
  `dotnet --version` **run in the solution directory** so a `global.json` pin is honored
- `TemplateCacheReader.cs` — pure `string json → IReadOnlyList<TemplateInfo>`; no SDK needed to test
- `TemplateGrouping.cs` — pure dedupe/grouping (see risks; this is the load-bearing piece)
- `TemplateCatalog.cs` — locate → read → prime-if-missing → group, memoized against the cache
  file's last-write time
- `TemplateInstantiationService.cs` — builds the `dotnet new` argument list, runs it via
  `BuildService.RunDotnetAsync`, and reports which files appeared
- `SolutionProjectAdder.cs` — `dotnet solution <path> add <project> [--solution-folder …]`
- `TemplateNameValidator.cs` — pure name validation (see risks)

**New files elsewhere**: `src/IdePlusPlus.Shell/Views/NewProjectDialog.xaml` +
`NewProjectDialogViewModel.cs`; a `_New` submenu in `MainWindow.xaml`'s File menu; an `Add` submenu
in `SolutionExplorerTree_OnPreviewMouseRightButtonDown`; DI registration in `App.xaml.cs`;
`tests/IdePlusPlus.Templates.Tests`.

**MVP scope**: list project templates grouped one row per template with a search box and a language
picker; a target-framework dropdown; name + location fields with live validation; create into the
open solution's directory by default; `dotnet solution add` into a chosen solution folder; reload the
solution and select the new project. With no solution open, create a solution (`dotnet new sln`,
which now defaults to `.slnx`) alongside the project and open it. Output streams to the Output pane
exactly as a build does.

**Deferred**: template-specific parameters beyond name/language/framework (Phase 3); installing
template packages from inside the dialog (Phase 3); "recently used" template ordering; project
templates that are really solution templates get a narrow treatment (see risks).

**Real risks**:

- **There is no machine-readable `dotnet new list`.** `dotnet new list` prints a fixed-width table
  that *truncates* the column it's most needed for — real output on this machine includes
  `ASP.NET Core We...` three times over — and its headers are localized. Parsing it is not viable.
  The catalog instead reads `templatecache.json` (1.2 MB here, 228 templates) which carries
  `Name`, `ShortNameList`, `Description`, `Author`, `Classifications`, `TagsCollection`
  (`language`, `type`), `Identity`, `GroupIdentity`, `Precedence`, `DefaultName` and the full
  `Parameters` list. The catch: that format is internal to the template engine, not a documented
  contract — it carries its own `"Version": "1.0.0.7"`. So `TemplateCacheReader` checks the version,
  and *every* failure (absent, malformed, unexpected version, zero templates) degrades to an empty
  catalog and a one-line Output message, never an exception and never a hard-coded template list
  presented as if it were real.
- **The cache may not exist yet**, on a machine where `dotnet new` has never run. Verified
  reproducible with an isolated `DOTNET_CLI_HOME`: running `dotnet new list` in a fresh home creates
  `.templateengine/dotnetcli/<sdk>/templatecache.json` as a side effect. So the catalog primes it by
  running `dotnet new list` once and re-reading, showing a "Finding templates…" state while it does.
  Priming costs ~0.7–2.9 s (both observed across runs); the JSON read afterwards is milliseconds,
  which is the whole reason for reading it directly rather than spawning a process per listing.
- **The same template is in the cache many times over, and naive listing is unusable.** 48 of 56
  C#-project template groups have more than one entry. `classlib` alone appears three times —
  `Microsoft.Common.Library.CSharp.8.0/9.0/10.0`, precedence 10000/11000/12000, one per SDK band
  shipped side by side under `C:\Program Files\dotnet\templates\`. Listing the cache as-is shows
  "Class Library" three times. `TemplateGrouping` must resolve the way the engine does: group by
  `GroupIdentity`, then within a group pick the highest `Precedence` entry per language.
- **Target framework is not a free parameter — it selects the template entry.** Each of those three
  `classlib` entries offers a *different* `Framework` choice list: the 10.0 entry offers
  `net10.0|netstandard2.1|netstandard2.0`, the 8.0 entry `net8.0|netstandard2.1|netstandard2.0`.
  There is no entry that offers all of them. So the dialog's framework dropdown is the *union* of
  the group's `Framework` choices, and picking one re-selects which entry gets instantiated
  (highest precedence among those offering it) rather than passing a flag to a fixed one. Getting
  this wrong looks like "net8.0 isn't in the list" on a machine that has the 8.0 templates installed.
  Implementation note: the CLI turns out to union the choices the same way for its own `--help`, and
  to resolve the band itself when handed `--framework net8.0` — verified producing a net8.0 project
  from a 10.0-default `classlib`. So the grouping decides what the dropdown *offers* and the CLI
  makes the selection; the resolution logic stays, because it is also what tells the dialog which
  entry's parameters apply.
- **Some "project" templates are solution templates.** `aspire-starter`, `maui-multiproject` and
  `maui-blazor-web` are tagged `type: solution` and emit several projects plus their own solution
  file. Adding one to an already-open solution is not meaningful. MVP treatment: show them, tagged,
  and route a solution-kind template to the create-a-new-solution-and-open-it path regardless of
  what is currently open — with the dialog saying so before it runs, not after.
- **`dotnet new create` silently ignores `--output`.** Found while implementing this, and the single
  easiest way to ship a feature that appears to work while putting every generated file in the wrong
  folder. With the explicit `create` verb, `--output` is swallowed as a template argument — in *both*
  positions, before and after the template name — so the file is written to the working directory
  instead, while the CLI still prints "created successfully" and exits 0. Measured on 10.0.301:
  `dotnet new create class -n X -o Sub` writes `./X.cs`; `dotnet new class -n X -o Sub` writes
  `./Sub/X.cs`. So the implicit form is the one to use, and it has a named regression test. Its one
  cost: a template whose short name collides with a `dotnet new` sub-command (`list`, `install`,
  `search`, …) becomes unreachable — none of the 228 stock templates does, but a third-party package
  is free to, so that is refused loudly rather than silently running the sub-command.
- **Name collisions do not fail loudly.** `dotnet new` refuses to overwrite with a prose message
  ("Creating this template will make changes to existing files") and **exit code 73**, not a
  non-zero-means-crashed error. The service must treat any non-zero exit as failure and surface the
  output; the dialog must pre-check the target directory and refuse before spawning. `--force` is
  never passed implicitly.
- **`dotnet solution add` and `.slnx`.** Verified working against a `.slnx` on the .NET 10 SDK
  (writes `<Project Path="Lib1/Lib1.csproj" />`). It defaults to deriving a solution folder from the
  on-disk path — this repo's own solution keeps projects under `/src/` and `/tests/` folders — so
  the destination folder is a visible field in the dialog, defaulting to the folder of whichever
  node the command was invoked from, with `--in-root` when that's the solution root.
- **No SDK on PATH.** `BuildService.RunProcessAsync` already catches `Win32Exception` and returns
  -1 with a readable message; the catalog needs the same posture for its own `dotnet --version` and
  priming calls, so a machine without the SDK gets an empty template list and an explanation rather
  than a startup exception.
- **Reload cost.** Adding a project genuinely changes the solution, so `LoadSolutionAsync` is the
  right call — but it clears the Output pane, which throws away the creation log the user may want
  to read. Worth fixing narrowly (don't clear on a reload triggered from inside the IDE) rather
  than accepting.

**What shipping it changed**: two bugs the design could not have predicted, both found by running the
dialog rather than reading it. Searching left nothing selected, because
`ObservableCollection.Clear()` drives the ListBox's `SelectedItem` to null through the two-way
binding, so the "is the selection still in the filtered list" guard never fired. And the project name
kept the previously selected template's default. Verified working against the real SDK: 40
deduplicated rows from 228 cache entries, Class Library offering all five frameworks, `net8.0`
producing `<TargetFramework>net8.0</TargetFramework>`, `obj/` staying out of the created-file list,
and a re-create returning 73 rather than overwriting.

---

## Phase 2 — New Item *(shipped)*

**Summary**: The same catalog, filtered to `type: item`, behind two entry points: a curated
shortlist directly on `File > New` (Class, Interface, Enum, Record, API Controller — the ones asked
for, all of which the SDK genuinely ships), and a searchable `NewItemDialog` on
`File > New > Item…` and Solution Explorer's `Add > New Item…`. The generated file is added to the
tree and opened in the editor.

**Grounded in**: Phase 1's catalog and instantiation service, unchanged. `SolutionTreeNode`
`GetOrCreateFolder` for placing the new node without a reload. `MainWindowViewModel.OpenPath` for
opening the result. The existing `_New` menu item keeps its behavior as `File > New > Text File`.

**New files**: `src/IdePlusPlus.Shell/Views/NewItemDialog.xaml` + `NewItemDialogViewModel.cs`;
`NamespaceResolver.cs` in `IdePlusPlus.Templates`; menu and context-menu additions.

**MVP scope**: resolve a target project + directory from the invocation context; show the item
templates that apply; take a name; run the template; insert a `File` node under the right folder in
the tree; open the file. The shortlist entries skip the dialog entirely — name prompt only.

**Target resolution** (VS uses Solution Explorer selection; this mirrors it explicitly because
`File > New` has no selection of its own): the selected Solution Explorer node's directory, if any;
else the active document's directory when it sits inside a project of the open solution; else the
startup project's directory; else — no solution open — the shortlist entries are disabled and
`File > New > Text File` is what remains. Whichever wins is shown in the dialog as an editable
path, so the fallback chain is never silently wrong.

**Deferred**: Add > Existing Item; rename/delete/move in Solution Explorer (adjacent, separate);
non-SDK-style projects that need an explicit `<Compile Include>` (see risks); item templates for
languages other than C#/VB beyond whatever the catalog reports.

**Real risks**:

- **The namespace will be wrong unless IDE++ computes it.** Two distinct failures, both verified:
  `dotnet new class -n Widget -o Models` inside a project whose `RootNamespace` is `Demo` emits
  `namespace Demo;`, **not** `Demo.Models` — the template binds `DefaultNamespace` to
  `msbuild:RootNamespace`, which has no idea about the subfolder, and passing `--project` does not
  change it. Meanwhile `dotnet new apicontroller` with no `--namespace` emits the *literal*
  `namespace MyApp.Namespace`, that parameter's default value. Visual Studio gets this right because
  Add > Class uses VS's own `.vstemplate` items, not `dotnet new`. So `NamespaceResolver` computes
  `RootNamespace` + the folder path relative to the project (each segment sanitized to a C#
  identifier), and the instantiation service passes `--namespace` when the template declares that
  parameter — `apicontroller`, `mvccontroller`, `razorcomponent`, `page`, `view` all do. For
  templates that do not declare one (`class`, `interface`, `enum`, `record`, `struct` — they only
  have the MSBuild binding), the emitted namespace token is rewritten in place afterwards: a
  targeted replacement of the exact known `RootNamespace` value on the namespace line, not a
  general-purpose rewrite.
- **The item list is not filtered by project type, and is far too long for a menu.** Verified:
  `dotnet new list --type item --project <a class library>` still lists API Controller and all seven
  MAUI item templates — the `project-capability` constraint on these templates only asks for
  `CSharp`. So the catalog cannot be dumped into `File > New`. The shortlist is a fixed set of short
  names (`class`, `interface`, `enum`, `record`, `apicontroller`), each shown only if the catalog
  actually has it; everything else lives behind the searchable dialog. Filtering the dialog by what
  the target project references is a Phase 3 refinement, not an MVP promise.
- **Which file was created?** The cache does not carry `primaryOutputs`, and `--dry-run`'s
  "File actions would have been taken: Create: Models\DryClass.cs" is localized prose. The
  instantiation service instead snapshots the target directory before and after and opens what
  appeared — which also copes with templates that emit more than one file (`maui-page-xaml` emits
  a `.xaml` and a `.xaml.cs`).
- **Generated files carry a UTF-8 BOM.** Observed on `dotnet new class` output. Worth confirming
  `EditorWorkspace.Open` and the Roslyn workspace both handle it rather than showing a stray
  character; if they don't, that is a pre-existing bug this feature would make easy to hit.
- **Non-SDK-style projects.** A new file only appears automatically because SDK-style projects glob
  their sources. A legacy `.csproj` needs an explicit item. Out of scope for the MVP — but the tree
  insert must not silently imply the file is in the build. Detect the absence of an `Sdk` attribute
  and say so in the Output pane rather than pretending.
- **No reload for a new item.** Deliberately: the file is already a project item by glob, so the
  tree gets a targeted `GetOrCreateFolder` + `File` node insert. The Roslyn workspace, however, does
  not know about it — `RoslynWorkspaceService` has no add-document path, only
  `LoadSolutionAsync`. Until it does, a new file gets no diagnostics or completion until the
  solution is reopened. That is the one real cost of skipping the reload, and it should be a known,
  stated limitation rather than a surprise; a narrow `NotifyDocumentAdded` is the follow-up.

**What shipping it changed**: the namespace work landed as designed - `NamespaceResolver` plus a
targeted rewrite for the templates that declare no `namespace` parameter - with one addition the
design missed. Folder names that are C# keywords become verbatim identifiers (`@event`) rather than
the bare keyword Visual Studio emits, which does not compile.

Three failures showed up only on running it, all in the ordinary case of adding the first file to a
folder that does not exist yet. `Process.Start` fails outright on a working directory that does not
exist, so creation died with "Could not run dotnet - is the SDK installed and on PATH?"; the working
directory now walks up to the nearest ancestor that does. Underneath that sat the real refusal: the
item templates' `project-capability` constraint resolves the project **from the `--output` path**
rather than by walking up from it, so a folder with no project in it reports "No project was found at
the path" and refuses — verified that `--project` fixes exactly this, so every item plan now names its
project. And the solution root needed expanding along with the project, since WPF only realizes a
`TreeViewItem` once its parents are expanded.

---

## Phase 3 — parameters, installed template packages, and the copilot *(shipped)*

**Summary**: Surface the template parameters the cache already describes, let template packages be
installed from inside the IDE, and give the copilot a tool for both features.

**MVP scope**: render each template's `Parameters` generically from the cache — `bool` as a
checkbox, `choice` as a combo box, `string`/`text` as a text box — using `DisplayName`/`Description`
where present (the API Controller template, for example, carries `actions` /
"Add Read/Write Actions", exactly the checkbox VS shows). Required parameters block the OK button.
`dotnet new install` / `uninstall` for template packages, listed from
`%DOTNET_CLI_HOME%/.templateengine/packages.json`. A `create_project` / `add_item` pair of copilot
tools, routed through the same service and subject to the same guard.

**Deferred**: importing Visual Studio `.vstemplate` item/project templates; user-authored templates
in a solution-local folder; template parameter *conditions* (`IsEnabledCondition` /
`IsRequiredCondition` in the cache are expressions the engine evaluates — honoring them properly
means an expression evaluator, so the MVP shows such parameters unconditionally).

**What shipping it changed**: the parameter rendering landed as designed. Two decisions the design
did not anticipate. Installing a template package deliberately has **no copilot tool** — it is
arbitrary code fetched by name from a public feed that later runs as a template on the user's
machine, which is the same shape as the `run_command` and MCP-trust prompts elsewhere here, so it
stays user-initiated. And the copilot's `create_project` refuses solution templates for the reason
Phase 1 already found: they bring their own solution file and would replace the one that is open.

`add_item` is the tool that earns its place — writing a class with `propose_edit` guesses at the
project's language version, implicit usings and namespace, while the template knows all three, so
the description says to prefer it. `list_templates` exists because short names vary by machine and
SDK version, and guessing one is the failure the other two would otherwise hit first.

Verified end to end against a live model. Asked to add a record, it called `list_templates`, then
`add_item`, then `get_diagnostics` on the file it had just created - which came back clean, and only
could because `add_item` registers the new file with the Roslyn workspace. Asked for a class library
it created one and added it to the solution; asked for `aspire-starter` it was refused with the
solution-template message, nothing was written, and it relayed the reason and offered
`aspire-apphost`/`aspire-servicedefaults` instead, which genuinely are project templates.

The package round-trip is verified too: installing `NUnit3.DotNetNew.Template` added six cache
entries which the grouping collapsed to one row, and uninstalling it restored the machine exactly -
228 templates before and after.

---

## Testing

`tests/IdePlusPlus.Templates.Tests`, following `IdePlusPlus.Tasks.Tests`' posture — parsing and
resolution pinned against captured samples, with actually running `dotnet new` treated as an
integration concern:

- `TemplateCacheReader` against a trimmed captured `templatecache.json`, plus malformed / empty /
  wrong-version inputs all degrading to an empty list.
- `TemplateGrouping` against the real duplicate cases (three `classlib` entries; `class` in C# and
  VB; `nunit-test` from two SDK bands) — asserting one row out, highest precedence per language,
  and the union-of-`Framework`-choices mapping back to the right entry.
- `NamespaceResolver` for nested folders, folder names that aren't valid identifiers, a project
  with no explicit `RootNamespace`, and a file at the project root.
- `TemplateNameValidator` for path characters, reserved Windows device names (`CON`, `PRN`, `NUL`,
  `COM1`…), trailing dots and spaces, and non-identifier names for C# item templates.
- Argument-list construction for `dotnet new` and `dotnet solution add` — pure, and the
  place where a `--namespace`-only-when-declared bug would otherwise hide.
- The created-files directory diff, over a `TempDirectory`.
