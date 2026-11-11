# Changelog

## Unreleased

- **The scope check compares the delivered commits with the task branch.** It starts from the merge base of the task branch and the delivered revision (the task base while the task branch does not exist), so work a dependent card inherited from the task branch is not reported, and moving the task base does not change the answer. The task branch must contain the task base.
  - Card commands use the task branch by name: `git diff --no-renames --name-only <task branch>...HEAD` for the zone and the reserved paths, a first line that prints only when the task branch is missing or does not contain the base, and `git status --porcelain` for uncommitted changes, which are not part of the delivery. The card's own commands no longer carry the base commit, so they do not go stale when the base moves.
  - A card that waits for another card's output says it starts from the task branch tip.
  - `scope` returns `delivered` (the commit range) and, with `checkout`, `uncommitted` (modified and untracked files, inside and outside the zone). Without `checkout` it checks the assignment's delivered revision, or `revision` when given.
- **Acceptance records the scope check.** `record_review` with `accepted` runs it on the round's revision and writes one `checks` entry with the start, the revision and the result. A path outside the zone or a reserved path refuses acceptance with the paths, and the review file stays unchanged.
- **The merge takes the reviewed commit.** The card's merge step merges the accepted revision taken from the review. A card whose executor branch has commits after the delivered revision shows "N commit(s) after the delivered revision are not reviewed"; a task branch that holds such commits shows "Merged past the accepted revision". The board and `board`, `task` and `card` queries show the problem.
- **Approve All** in the window toolbar when two or more tasks are drafts: one dialog lists every draft with its assignment count and validation errors, and approves them all as one Undo step.

## 0.1.1

- **The window opens at once.** Git and the task data are read after the window is shown, with a loading bar on the first load only.
  - Each refresh starts with one listing of all branches and the data files, and stops there when nothing moved, which is the usual case when the window gets focus.
  - Git answers about commits are remembered across refreshes and script reloads (`Library/Coordinator/git-memo.json`), so only branches that moved cost git calls.
  - Later refreshes keep the board on screen and run quietly while they are short.
- **Undo and Redo** in the window toolbar, and Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) while the window has focus. Every change through Coordinator is one step: an agent edit or a task approval in the window.
  - A step restores the files it changed, and removes a task folder it created.
  - A step is refused, and the files stay as they are, when one of them changed since then outside Coordinator, for example by a git pull or a delivery.
  - The history lasts for the Editor session and survives script reloads.

## 0.1.0

First, test release: the model, validation, git-computed states, cards, agent access and the MCP server are complete; the window is a draft until its final design.

- Data model and files of `Coordinator/`: project, tasks, decisions, assignments, deliveries and reviews.
- Task validation: ids and references, write zones against reserved paths and against each other, outputs inside zones, inputs, dependency cycles.
- Scope check commands generated from an assignment's base and zone.
- A delivery lists its `judgments`: choices the card did not settle, each with its reason and optional route step.
- An executor declares its `environment`, `worktree` or `editor`; project checks and rules may name the `environments` they apply to, and cards repeat only those rules.
- The MCP server is installed like Architector's: downloaded from the release for the package version and checked against `checksums.txt`; connected agents move to the matching server when the package version changes. The source build is gone.
- A task names its task branch (`integrationBranch`, different from the target). An accepted card becomes Merged when its executor merges it into the task branch, and dependent cards unblock then. Cards tell the executor never to push and how to merge after acceptance.
- States computed from git: an assignment is Draft, Blocked (open decisions, outputs not accepted, resources not supplied), Ready, In Progress, Delivered, Returned, Accepted or Closed from its executor branch, the lead's reviews and the target branch; a task is Draft, Approved, In Work, Integrated or Closed.
- Source inputs are checked at the task's base commit.
- Generated Markdown cards and a task summary in each task's `Cards` folder.
- Architector files are read as data: a layer can be an assignment's zone (its scope folders), and two open assignments touching the same or connected layers are warned.
- A toolbar button and a draft window list tasks, states and diagnostics and generate cards.
- The window is a board: one column per project discipline in the project's order (Other for undeclared ones), each assignment in the column of its first discipline, ordered by work state; a task filter, a Closed toggle and diagnostics. Clicking a card opens its generated text beside the board.
- Agent access: `CoordinatorAgentApi` (query and edit, with rollback of edits that add validation errors; tasks are approved only by a person, acceptance names the person who decided), a local Editor bridge, the `coordinator-mcp` server in `Tools~/CoordinatorMcp` with a smoke test and a release workflow, an Agents window that builds the server and connects Claude Code or Codex, and the `coordinator-workflow` skill.
