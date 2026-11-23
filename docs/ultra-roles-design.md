# Ultra roles: a fixed roster instead of N identical workers

> **Status.** The role types, the settings (presets and per-lane counts), `TeamReviewQueue` and the dispatcher's
> scope enforcement described below exist and are unit-tested, but `UltraTeamRun` does not use them yet: it does not
> pass a role to its workers, builds no review queue, names every worker `w<N>`, and nothing reports a quality summary.
> A run of the Ultra team today is N equal workers whatever the preset. Wiring the roster into the run is a separate piece
> of work. The [Ultra pipeline](ultra-pipeline-design.md) is a different strategy and does not depend on any of this.

Ultra effort used to start N identical workers ("w1", "w2", ...): one model, one prompt, one
backlog, no division of labour. In practice that produced linear work — a worker claimed the whole
goal, did it, merged it, and the rest idled — because nothing in the prompt, the board or the
runner ever made a *different* kind of work exist. Review (mutation testing, adversarial reading,
lint, gap finding) only happened if a worker chose to stop building and do it, which never made
sense against a shared backlog.

This design gives Ultra a **fixed roster of roles**. Builders do the goal's TASKs; specialist lanes
run *alongside* them, woken by a queue that the runner fills every time a merge lands, so review
work happens continuously on merged code instead of after it. Builder parallelism gets forced from
three directions: the plan must be wide, idle builders split the largest claim, and TASKs carry a
lane tag so each role only takes its own work.

Default preset is **Speed** (builders only) — exactly today's behaviour — so nothing changes for
existing users until they pick Balanced or Hardening.

## Roles

| Role | Name prefix | Trigger | Edit scope | Default effort |
|---|---|---|---|---|
| Builder | `b` | Backlog | Any | Medium |
| Mutation tester | `mut` | Merge queue | Tests only | Medium |
| Adversary | `adv` | Merge queue | Tests only (nothing merged) | High |
| Conformity | `lint` | Merge queue | Changed files, style only | Low |
| Gap finder | `gap` | Merge queue | None | Medium |
| Follow-up scout | `next` | Merge queue | None | Low |

`TeamRole` (`src/IdePlusPlus.Copilot.Core/Team/TeamRole.cs`) carries `Kind`, `NamePrefix`,
`DisplayName`, `Charter` (the text its prompt uses), `EditScope`, `Trigger` and `DefaultEffort`.
`TeamRoles.All` is the catalogue; `TeamRoles.ForWorkerName("mut2")` recovers a role from a name.

**Edit scopes** are hard limits, not prompt advice:

- `Any` — a builder's worktree.
- `TestsOnly` — `edit_file` refuses any path outside the run's `TestProjectPaths`. A suite that
  passes therefore cannot have been made to pass by bending the code it tests.
- `ChangedFilesStyleOnly` — only the files the review item the specialist is on changed.
- `None` — read and report; `edit_file` is not offered and a write is refused.

The adversary has `CanMerge = false` on top of `TestsOnly`: its failing test is *evidence*, not a
fix, and it travels with the finding as `evidence=branch:<sha>`.

## The review queue

`TeamReviewQueue` (`src/IdePlusPlus.Shell/Team/TeamReviewQueue.cs`) is filled by the runner: the
dispatcher's `MergeLanded` callback fires right after the fast-forward of the team branch succeeds,
next to `RecordMerge`. Each landing becomes a `ReviewItem`:

| Field | Meaning |
|---|---|
| `Id` | stable id used by findings and the result report |
| `FromSha`/`ToSha` | the merge range the specialist diffs |
| `Author` | the worker whose merge it was |
| `TaskId` | the TASK parsed from the next `PATCH_SUMMARY` |
| `Generation` | 0 for a merge from the backlog, +1 for each merge fixing a finding |
| `Lane` status | `Pending`/`Taken`/`Done` per specialist lane |

The tool `team_next_review` gives a specialist the oldest item for **its** lane, syncs its worktree
to the team branch, and returns the diff stat and the `PATCH_SUMMARY` that came with the merge. With
nothing queued it waits for nothing and makes no model call, the way `team_wait` does.

**Loop guard.** A merge whose `PATCH_SUMMARY` fixes a FINDING is enqueued at `Generation + 1`. Items
at generation ≥ 2 go *only* to the Conformity lane — style cannot introduce new behaviour, so the
findings → fixes → findings spiral stops there instead of feeding every lane forever.

After the 80 % `DeadlineReminder` the queue stops taking new items: whatever is left has to be
reviewed with the time that remains, and a merge landed in the last minute would otherwise be an
item nothing ever drains.

## Findings into work

A FINDING is a board entry (`TeamBoardEntryType.Finding`, cap 300 chars, must contain `severity=`
and `evidence=`) reported with `report_finding(severity, kind, files, text, evidence,
fix_by=builders|me|user)`. It is not just a note:

- **Without a lead** the runner turns one into work: `severity=high` with `fix_by=builders` becomes
  a TASK in the builders' lane, linked to the finding's id. Medium findings become TASKs only when
  they carry a test; low ones go to the report.
- **With a lead** `LeadSupervisionOpening` gains an "Open findings" section and the lead gets
  `lead_triage(finding, promote|dismiss, reason)`, so promotion is a judgement rather than a rule.
  `LeadReviewMode = Blocking | Advisory` — in Advisory mode the lead's review no longer holds up
  merges, removing the serial bottleneck in `ReviewMergeAsync` that a blocking lead reintroduces.
- A high-severity FINDING interrupts the worker that sees it (`TeamNotices.InterruptingTypes`), so
  a builder learns about it before starting the next thing rather than after.

Findings are lane-tagged (`[lane:adv]`) and the board digest groups by lane: a worker's own lane
first, other lanes only counted. Board review, the journal and `Import`/`Format` all round-trip the
tag, so a run carried on from an earlier branch keeps its lanes.

## Composition and presets

`TeamComposition(int Builders, IReadOnlyDictionary<TeamRoleKind,int> Specialists)` with `Total`,
`Validate()` (at least one builder, at most two of any specialist lane, at most
`MaxUltraWorkerCount`), and `Workers` (`b1..bN`, then `mut1`, `adv1`, `lint1`, `gap1`, `next1`).

| Preset | Roster |
|---|---|
| Speed | builders only — what Ultra shipped with |
| Balanced | 1 mutation, 1 adversary, 1 conformity, 1 gap finder |
| Hardening | 2 adversaries, 2 mutation testers |

`UltraTeamOptions.Composition` replaces the old `WorkerCount`; the old constructor still means "all
builders", so existing callers and tests compile unchanged. Auto-scale only ever adds **builders**
and counts them with a separate counter, so a new builder cannot collide with a specialist's name.

## Forcing builder parallelism

1. **The plan is measured.** `UltraOrchestrator.WritePlan` builds the plan's dependency graph from
   the TASKs' "after" references and refuses `lead_submit_plan` once with specific feedback when
   the first wave is narrower than the number of builders, or the longest chain is more than half
   the plan: split by file or area, put shared interfaces first as stubs, then fan out.
   `LeadPlanningPrompt` makes a parallel first wave the default it must justify leaving.
2. **Idle builders split the bottleneck.** When two or more builders have been in `team_wait`/idle
   for over two minutes and fewer TASKs are open than builders are idle, the runner DMs the builder
   holding the oldest active claim with `TeamPrompts.SplitNudge`, asking it to split the rest of
   that claim into lane-tagged TASKs.
3. **The builder prompt says so.** `WorkerSystemPrompt` takes the role and tells builders that
   specialists review, mutation-test and lint each merge: merge small and early so the lanes get
   work sooner, and do not do the specialists' jobs yourself.

## Wrapping up

`AnnounceWrapUpWhenDone` tells the team to wrap up only when all three hold: every builder TASK is
done, every review queue is drained, and no high-severity FINDING is open. Specialists call
`team_done` once their queue is empty and the wrap-up notice has been sent. The stall check and the
token budget count specialists as well, and `RequestHelpAsync` refuses specialist growth — a
specialist is woken by merges, so more of them cannot unblock a team that is not merging.

## Settings, UI and results

Settings gain `UltraCompositionPreset`, `UltraBuilders`, `UltraMutationTesters`, `UltraAdversaries`,
`UltraConformity`, `UltraGapFinders`, `UltraFollowUpScouts`, `UltraLeadReviewMode` and
`UltraSpecialistEffort`, each clamped on read. The Settings window shows a "Team composition" card
beside the Auto mode grid: preset, a count per role, a running total and a disk estimate (each
worker is a git worktree). `EnsureDiskSpace` counts every role.

`UltraTeamResult.QualitySummary` carries mutation killed/total per merge, findings by severity and
status, conformity fixes, gaps closed and follow-ups; `DescribeTeamResult` renders them as sections,
lists open high/medium findings with their evidence branch, and prints follow-ups as a copyable
"Suggested next prompts" list. That is how out-of-scope ideas reach the user without breaking the
"stay within the goal" rule: the follow-up scout never edits, never merges and never adds a TASK.

## Files

| Area | Files |
|---|---|
| Catalogue | `Copilot.Core/Team/TeamRole.cs`, `TeamPrompts.cs` |
| Board | `Copilot.Core/Team/TeamBoard.cs`, `TeamNotices.cs`, `TeamTools.cs` |
| Identity | `Copilot.Core/Team/TeamWorkerState.cs`, `TeamWorkerContext.cs`, `UltraTeamOptions` |
| Mutation | `IdePlusPlus.Roslyn/Mutation/MutantGenerator.cs`, `Shell/Team/TeamMutationRunner.cs` |
| Conformity | `Shell/Team/TeamConformityChecker.cs` |
| Queue | `Shell/Team/TeamReviewQueue.cs`, `TeamToolDispatcher.cs`, `UltraTeamRun.cs` |
| Parallelism | `UltraOrchestrator.cs`, `UltraTeamRun.cs`, `TeamPrompts.cs` |
| Settings/UI | `CopilotChatPaneViewModel.Team.cs`, `SettingsViewModel`, `SettingsWindow.xaml`, `TeamWorkerRowViewModel`, `TeamRunViewModel` |
| Results | `UltraTeamResult`, `DescribeTeamResult` |

Rollout was phased — catalogue, identity, board and tool profiles first; then the specialist lanes
and the queue; then parallelism, wrap-up, settings, results; tests and this document last. Speed is
the default throughout, so each phase landed without changing what an existing Ultra run does.
