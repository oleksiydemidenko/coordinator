# Changelog

## 0.2.3

- **Edits without the Editor.** `coordinator-mcp edit` runs the same `CoordinatorAgentApi.Edit` on the Coordinator files when the Editor bridge does not answer, with the same validation and rollback. `approve_task` still refuses. The MCP server's `coordinator_edit` does the same; `coordinator_status` adds `writes_without_editor`.
- **No queue on one Editor.** By default a read or an edit goes to the files at once when the bridge does not answer now, instead of waiting for a recompile. A busy Editor gets 5 s to start a call (`claim_seconds`); a call it does not start is cancelled there and runs on the files. `--source editor` keeps the wait (`--wait`, default 60). `--source files` now works for edits too.
- **One writer at a time.** Every edit in the Editor and in the command line holds `Library/Coordinator/write.lock` for the whole change; a second writer waits up to 10 s, then answers `busy` without a change. Generate Cards in the window runs the `generate_cards` edit under the same lock.
- **The Editor picks up command line edits.** An edit made without the Editor leaves an undo entry in `Library/Coordinator/history-inbox`; the Editor adds it to its Undo history within a second and reloads the window.

## 0.2.2

- **Coordinator CLI in the Agents window.** The window shows the Coordinator MCP server and the Coordinator CLI as two items with their own tags (MCP, CLI) in the colours of the Architector integrations. The CLI item shows the binary path and version, whether the Editor bridge answers the binary, and copyable examples for a shell and PowerShell. The check runs the binary in the background, so the window opens at once.
- **The command line waits for the Editor.** While Unity runs for the project but its bridge does not answer (a recompile, a domain reload), `query` and `edit` try again for up to `--wait <seconds>`, default 60 (a domain reload of the game took 28 s), before exit 7. An edit that the Editor took but did not answer is not repeated.
- **Reads without the Editor.** When no Editor answers, `query` answers from the Coordinator files and git with the package's own model code, compiled into the binary from `Editor/` (`Tools~/CoordinatorCore`). Every reply says its `source`: `editor` or `files`. `--source editor|files` picks one. Edits still need the Editor. An Editor test keeps the shared files free of unguarded Unity references, and the server workflow also runs for changes under `Editor/`.
- **Git worktrees.** Called in a linked worktree, the binary serves the main checkout, where the lead's Editor and the current Coordinator files are. `status` names the worktree as `checkout`; `query scope` without `--checkout` or `--revision` checks it. Executors read their card and check their zone from their worktree with the command line.
- The MCP server answers `coordinator_query` the same way; `coordinator_status` adds `checkout`, `editor_running`, `reads_without_editor` and `wait_seconds`.

## 0.2.1

- **Command line.** The server binary is also a command line, like Architector's: `coordinator-mcp status`, `query <action> --<parameter> <value>`, `edit <action> --<parameter> <value|file.json|JSON>`, `commands` and `help`. Each call answers one line of JSON through the same Editor bridge and exits with 0 success, 1 internal error, 2 usage, 4 no Unity project, 6 `success: false` or 7 Editor not reachable. Agents with a shell reach Coordinator without a registered MCP server. Without a command the binary is the MCP server as before; `coordinator_status` adds `cli`, the binary's path.
- **Approve All shows with one draft too.** The toolbar button appears whenever at least one task is a draft, so a single draft is approved without first picking it in the task filter.

## 0.2.0

- **The scope check compares the delivered commits with the task branch.** It starts from the merge base of the task branch and the delivered revision (the task base while the task branch does not exist), so work a dependent card inherited from the task branch is not reported, and moving the task base does not change the answer. The task branch must contain the task base.
  - Card commands use the task branch by name: `git diff --no-renames --name-only <task branch>...HEAD` for the zone and the reserved paths, a first line that prints only when the task branch is missing or does not contain the base, and `git status --porcelain` for uncommitted changes, which are not part of the delivery. The card's own commands no longer carry the base commit, so they do not go stale when the base moves.
  - A card that waits for another card's output says it starts from the task branch tip.
  - `scope` returns `delivered` (the commit range) and, with `checkout`, `uncommitted` (modified and untracked files, inside and outside the zone). Without `checkout` it checks the assignment's delivered revision, or `revision` when given.
- **Acceptance records the scope check.** `record_review` with `accepted` runs it on the round's revision and writes one `checks` entry with the start, the revision and the result. A path outside the zone or a reserved path refuses acceptance with the paths, and the review file stays unchanged.
- **"Requirement changed" mark.** Approval records in each task source (`sources[].revision`) the commit that last changed its file on the target branch. When the file has a newer commit there, the board, the task filter and the `tasks`, `task` and `board` queries mark the task with the file and the commits. The mark blocks nothing and is not shown for closed or cancelled tasks. It clears when a decided decision's `record` names the path with a `revision` at or after the change, or when the lead sets the source's `revision`. `update_task` keeps the recorded revisions. Tasks approved before this field have no mark. The check compares the file's blob first, so an unchanged source costs no history walk.
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
