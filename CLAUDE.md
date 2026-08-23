# Working in this repo

## Git workflow: commit before and after changes

- Before starting a nontrivial or multi-step change, make sure the working tree is either clean or
  already committed. If there's uncommitted work that isn't part of what you're about to do,
  commit it (or ask) first, so there's a clean checkpoint to fall back to.
- After finishing a change - or a natural sub-step of a larger one - commit it, even mid-task,
  rather than saving everything for one large commit at the end. Each commit should be a state
  worth being able to `git revert`/`git reset` back to on its own.
- This matters most for agent-driven work. An agent resuming a task from a compacted/summarized
  memory (context compaction, a new session picking up a plan) may not have full fidelity on
  exactly what it already changed. Frequent small commits make `git log`/`git diff` enough to
  reconstruct what happened, and make it cheap to back out one bad step without losing everything
  else done since.
- If build output is locked (e.g. a debug session is running and holding the app's DLLs), that
  blocks *building*, not *committing* - commit source changes as normal; don't let a file lock
  become a reason to defer committing.
- This does not license low-effort or broken checkpoint commits - each commit should still build
  (or be clearly marked WIP if it can't, e.g. because output is locked as above) and represent a
  real, coherent step, not an arbitrary save point.
