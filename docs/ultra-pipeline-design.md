# Ultra pipeline: understand, design, build, then judge and repair

The Ultra team (see the roadmap entry and `UltraTeamRun`) starts N workers on a shared backlog and lets them sort it
out on a board. It has no division of labour by *phase*, no structured hand-off between stages and no loop that
judges the result: review only happens if a worker happens to do it.

The **Ultra pipeline** is the other strategy. It runs in phases, each producing a typed artifact, and ends in a
bounded loop in which independent judges check the result and the agents that wrote the code repair what they find:

```
Setup -> Roster -> Understand -> Design -> Build -> loop { Gate -> Judge -> Triage -> Repair } -> Finalize
```

It is **selectable**: Settings, Ultra team, Strategy. Team - the Ultra team, unchanged - is the default, and a
settings file from before the pipeline reads as Team. Nothing else about a run changes until Pipeline is chosen.

## What a run does

| Stage | Who | What comes out |
|---|---|---|
| Setup | the runner | a snapshot of the working tree, the team branch, read-only and gate checkouts, a build of the solution |
| Roster | the lead (the orchestrator) | how many surveyors, and which extra judges, this goal needs (`roster.json`) |
| Understand | surveyors `u1..uN` | what exists, what has to exist, what could be trimmed - each with evidence (`survey/uN.json`) |
| Design | the designer `d1` | the design: files, one owner each, acceptance criteria, misuse cases; and a *contract commit* of compilable interface stubs (`design.json`, `ownership.json`) |
| Build | owners `b1..bK` | each owner builds its own files in a worktree of its own and merges them to the team branch (`build/bN.json`) |
| Gate | the runner | a deterministic check of the team branch: build, tests, conformity (`loop-N/gate.json`) |
| Judge | judges `j1..j4` and the lead's own | one verdict each (`loop-N/verdicts/jK.json`) |
| Triage | rules, then the lead for what rules cannot place | who repairs which finding (`loop-N/triage.json`) |
| Repair | the owners that were given findings | repairs merged to the team branch (`loop-N/repairs.json`) |
| Finalize | the runner | `summary.json`, `report.md`, the result |

The runner is a deterministic stage machine. The lead is called only where judgement is needed: the roster, findings
no rule can place, and appeals. Between those calls it costs nothing, and it is never the reason a run stops: a
decision it cannot give comes back as a safe default (no extra agents, nothing triaged, the owner's refusal standing)
and a note in the record.

## The agents

| Agent | Name | Count | May edit | Sees |
|---|---|---|---|---|
| Surveyor | `u1..uN` | 0 to 5 (the lead decides; with 0 the designer surveys) | nothing | the shared read-only checkout |
| Designer | `d1` | 1 | stubs, in a worktree of its own; the runner checks the contract commit builds and touches only declared files | its own worktree |
| Owner | `b1..bK` | 1 to 16 (the design decides) | exactly the files it owns | its own worktree |
| Judge | `j1` gaps and flaws, `j2` maintainability, `j3` security and what-ifs, `j4` runtime bugs | 4 | nothing; `j4` may write tests | the shared read-only checkout, moved to the team head each round |
| Custom judge | `j5..` | 0 to 3 | nothing, or tests only | as a judge |

Every agent's system prompt begins `You are <name>,` and ends in a *submit tool* (`survey_submit`, `design_submit`,
`build_submit`, `verdict_submit`). The agent is stopped the instant its submission is accepted, a missing submission is
nudged twice and then recorded as a gap, never thrown, and **the runner writes every record: agents never write
`_phases`**. Anything an agent wrote that goes into another agent's prompt - the design, a verdict, a finding, a
digest - is fenced as `<<<DATA ... DATA>>>` with the markers stripped from inside it.

## Ownership and change requests

Every file in the design has exactly one owner, and that is enforced, not advised: an owner's dispatcher refuses
`edit_file` outside its files, and a merge restores any path an owner changed that it does not own (so a
`run_command` that wrote elsewhere is caught at merge). The design's contract - the interface stubs - is committed
before any owner starts, so owners build against it in parallel.

An owner whose work needs another owner's file does not edit it:

- `request_change` (non-blocking, returns an id), `await_change`, and for the owner asked: `respond_change` (accept,
  or reject with reasons - rejecting is a legitimate answer) and `change_done`.
- A rejection can be appealed once, by the requester, to the lead, whose ruling is final (`appeal_change`).
- `claim_new_file` claims a file nobody owns that does not exist yet and is not one that defines how the solution
  builds.
- An owner with an unanswered request cannot edit, commit or merge until it answers. A wait cycle is refused. An owner
  that has finished is *called back* in a short new conversation to answer or act - at most twice per stage.

## The gate

The gate is not an agent. In a throwaway checkout of the team head it builds, runs the tests, and runs the
conformity check (formatting and style) on the C# files the run changed. The checkout is reset after each use, so a
formatter's rewrites never reach the team branch. A build error or a failed test is a high finding, a style note a low
one. A **red gate skips the judges** - a build that does not build is not worth their time - and its errors go to the
owners like any finding.

## Judges and their verdicts

The judges run together and never see each other's findings. Each reads the team branch as it is now (they read
files; the IDE's symbol index is from the start of the run, and their prompt says so) and submits a verdict:

```
{ verdict: pass | fail, summary, findings[{ title, severity, category, files[], evidence, quote, fix, fixBy }], coverage[{ id, result, evidence }] }
```

A verdict is **validated** before it counts:

- the verdict word is required (a missing one would read as a pass);
- a `pass` cannot carry a finding at or above the severity bar, and a `fail` needs one;
- a medium or high finding needs evidence and a `quote` that really occurs in a file it names - a finding whose quote
  is not found is kept, but as *unverified*, and counts for nothing;
- `j3` must answer every misuse case the design listed (`coverage`);
- a high finding from a judge that writes tests needs its failing test, committed on the judge's own branch and
  quoted as `branch:<sha>`.

A refused verdict is sent back with why. After three refusals - or no submission - the judge is *unavailable* for the
round: the round goes on without it, it is a gap in the record, and it never holds a round up.

`j4` (and any custom judge whose scope is tests only) has a worktree of its own to write failing tests in. They are
evidence, kept on its branch and never merged.

## The loop

A round **passes** when the gate is green and no finding at or above the severity bar (default medium) is still open.
What decides it is the *ledger* of every finding of the run (`FindingLedger`): a finding is the same finding in a later
round when the same judge reports it against the same primary file and category with a title that shares at least half
its words; one reported again after a repair is *escalated* instead of being handed to the same owner again; one a
judge stopped reporting is *fixed*; a judge that gave no verdict does not clear what it found before.

A round that does not pass is followed by a repair, until one of:

| End | Why |
|---|---|
| `AllDone` | the round passed - or triage dismissed or deferred everything that was open, and nothing changed |
| `LoopCapReached` | the repairs are used (default 3) and the last round, which is judged and not repaired, did not pass |
| `NoProgress` | what is open (3 per high finding, 1 per medium) did not shrink for two rounds in a row, or a repair changed nothing |
| `BudgetSpent`, `TimeUp` | a cap was reached |
| `WorkersStopped` | the designer gave no design that could be accepted, so there was nothing to build |
| `Cancelled` | the user stopped it |

From round 2 a judge is told the owners repaired the code, shown **only what changed since the last code it judged**,
shown its **own** earlier open findings to check (and to report again under the same title if they stand), and given
the *digest*: how each round went in numbers, what each owner said it did and could not do, and what the lead
dismissed or left to the user - so a judge is not asked to report that again.

## Triage and repair

Triage is rules first (`RepairPlanner`):

- a finding the judge says is outside the goal is **deferred to the user**, with a request they could run later;
- a finding about one owner's file, that the judge says an owner can fix, is **assigned to that owner**, with the
  judge's own fix as the instruction;
- anything else - it spans owners' files or none, it is about the design, or it is a finding that came back after a
  repair - goes to the **lead**, which assigns, dismisses (with a reason) or defers it, and must not hand an escalated
  finding back in the same words. If the lead cannot decide, those findings stay open with nobody to repair them.

The repair is done by the owners that were given findings: **the same agents, in the checkouts they built the files in.**
A repairing owner is told to `team_sync` first, is shown each finding - the judge's evidence and quote, fenced - and
what to do about it, and the history of what was tried before. It works under the build's own rules (its own files,
change requests to others, merge small) and reports through `build_submit`, listing under `unmet` what it could not
fix. A round counts as a change only if the team branch moved.

## What the user gets

A run that passes lands like any Ultra run. A run that stops at its cap or because it was not making progress is not a
silent failure: the code that builds has landed, and the result says what is **still open**, each finding with its
severity and evidence, what is below the bar, how many were fixed, dismissed or set aside as unverified, and a list of
**suggested next prompts** - what was left to the user, then one per open finding. The chat shows a short form; the
full one is `report.md` in the run's `_phases` folder.

### The record

```
<work folder>\<runId>\_phases\
  roster.json  survey\uN.json  design.json  ownership.json
  build\bN.json  build\change-requests.json
  loop-N\gate.json  verdicts\jK.json  findings.json  triage.json  repairs.json  change-requests.json  digest.md
  summary.json  report.md
```

Records are written atomically and versioned. A record that cannot be written is a gap in `summary.json`, nothing more.

## Settings

Under Settings, Ultra team, Strategy: the strategy (Team or Pipeline) and, for the pipeline, the repair rounds (1 to 8,
default 3), the severity bar (low, medium or high; default medium), the most surveyors (0 to 5, default 3), owners (1 to
16, default 6) and extra judges (0 to 3, default 2) the lead may choose. The pipeline runs once, through its stages, and
is bounded by the same caps as a run until done: the run-time cap and the token cap. Time and tokens are shared out by
weight - understand 2, design 2, build 5, then each judge round 2 and each repair round 3 - out of what is *left* when
a stage starts, so the loop's reserve is kept while the build runs and a stage that finishes early hands its time on.

Settings, Ultra team, Conventions is the user's standing rules for code, appended to **every** agent's system prompt - it is
added once, in `PipelineAgentHost`, which runs the surveyors, designer, owners, judges and the lead alike. It is not the chat's
system prompt, which names tools a pipeline agent does not have. See [copilot-system-prompt.md](copilot-system-prompt.md).

## Cost and limits

It costs more than the team: roughly surveyors, a designer, up to six owners and four to six judges, for every round.
What keeps that bounded is the token and time caps, the repair limit, stopping early on `NoProgress`, judging only
what changed after the first round, and a shared read-only checkout for the readers. Every owner and every
test-writing judge has a checkout of its own, so the disk check counts them all - and a judge that cannot be given
one degrades to "unavailable" rather than costing the run its work.

Deliberately **not** in this version: more than one build wave, mutation testing in the gate, owners keeping their
conversation across stages (what they know of their work is in their checkout and in the digest), carrying the
board of an earlier run, judging the design before anything is built, and moving the Ultra team onto the same
workspace code. The Ultra role roster described in [ultra-roles-design.md](ultra-roles-design.md) is not used by it.

## Where it lives

| | |
|---|---|
| `src/IdePlusPlus.Shell/Team/Pipeline/UltraPipelineRun*.cs` | the runner, one file per stage: `.cs` (setup, roster, understand, finalize), `.Design.cs`, `.Build.cs`, `.Changes.cs`, `.Gate.cs`, `.Judge.cs`, `.Loop.cs`, `.Repair.cs` |
| `src/IdePlusPlus.Shell/Team/Pipeline/` | `PipelineAgentHost` (runs one agent), `PipelineConductor` (the lead's three decisions), `PipelineGate`, `RunWorkspace` (the snapshot, worktrees and teardown) |
| `src/IdePlusPlus.Copilot.Core/Team/Pipeline/` | the schemas, validators, prompts and tools; `FindingLedger`, `ChangeRequestRegistry`, `RepairPlanner`, `PipelineDigest`, `PipelineReport`, `PipelineBudget` |
| `src/IdePlusPlus.Shell/Team/ITeamRunStatus.cs` | what the status card and the result read from a run of either kind |
| `tests/IdePlusPlus.Shell.Tests/UltraPipelineRunTests*.cs` | the run end to end, against a real git repository and a scripted provider |
