# Coordinator

Agent task cards for Unity projects. Released: 0.2.5 (see the [changelog](CHANGELOG.md)). The model, validation, git-computed states, cards, agent access and the MCP server are complete; the window is a draft until its final design.

Coordinator is for parallel work of agents on one task, in one discipline or several. A lead splits a task from the technical design into assignments; each is taken by an operator, the person who runs an executor agent for it. Every assignment states what to read, its inputs and outputs, the only paths it may change, its route and its acceptance. Shared decisions are made before parallel work starts. Progress is computed from git, so no server is needed.

Coordinator does not launch or supervise agents. An executor works in its own git worktree without Unity or in its operator's checkout with the Unity Editor open, as its card states; it reads a generated Markdown card, checks its write zone with the git commands in it and writes a small delivery file. The operator hands the card in, the lead reviews every card in the Editor, the executor merges an accepted card into the task branch, and only the lead's approval lets the task branch into the target branch.

It is a sibling of [Architector](https://github.com/oleksiydemidenko/architector). With an `Architector` folder in the project, cards can name layers; Coordinator reads those files and has no assembly reference to Architector.

## Data

Everything lives in `Coordinator/` at the project root as deterministic JSON and is committed:

```text
Coordinator/
  coordinator.project.json          disciplines, reserved paths, variables, common checks, rules
  Tasks/<task>/
    task.json                       task, decisions, integration plan
    task.review.json                merges and combined results
    <assignment>.assignment.json    the assignment
    <assignment>.delivery.json      written only by its executor, in its branch
    <assignment>.review.json        review rounds
```

Each file has one writer, so branches merge without conflicts.

## Agents

The open Unity Editor serves a transport-neutral API (`CoordinatorAgentApi`) through a local bridge. The MCP server in `Tools~/CoordinatorMcp` forwards to it. Reads and edits are also answered without it, on the Coordinator files (see below):

- `coordinator_status`: project, data and whether the Editor is reachable;
- `coordinator_query`: `capabilities` (with the workflow mode), `tasks`, `board` (`state` filters the cards, for example `Delivered`), `task`, `card` (with the card's diagnostics), `validate` (`task` for one task), `scope` (compares the delivered commits with the task branch; with `checkout`, also lists the executor's uncommitted changes). Every read but `capabilities` takes `ref`, a branch, tag or commit: it then reads the Coordinator files of that commit from git, so the lead sees `develop`'s tasks from a checkout on a task branch. States always come from the branches;
- `coordinator_edit`: `create_task`, `update_task`, `set_decision`, `set_assignment`, `remove_assignment`, `cancel_assignment`, `record_review`, `generate_cards`, `approve_task`. An edit that adds validation errors is rolled back. `approve_task` follows the workflow mode (below). Acceptance names the person who decided; it runs the scope check on the accepted revision, records it in the review and is refused when a path leaves the zone.

**Workflow modes.** As in Architector, the person picks how much the lead agent does on its own, with **Mode** in the Coordinator window toolbar: **Safe** (the default: the agent shows every split, decision and verdict and waits for the go-ahead), **Free** (the agent splits, decides and returns cards on its own) or **Lazy** (as Free, and the agent approves draft tasks). The mode is kept per user and project; the Editor mirrors it to `Library/Coordinator/agent-mode.json`, which the command line reads, and writes the file again on every window refresh. In Safe and Free mode `approve_task` refuses (`needs_person`): a person approves in the window. In Lazy mode it approves a draft when `validate` shows nothing for that task, errors and warnings alike, zones shared with other tasks included (`not_clean` with the diagnostics otherwise); it needs `by`, the agent, and `instructed_by`, the person who instructed it, and records both in the task's `approval`.

The same binary is also a command line, for agents that run shell commands and have no MCP server registered. With a command it answers one line of JSON and exits; without one it is the MCP server, so an MCP registration (`--project <path>` or a bare project folder) keeps working:

```
coordinator-mcp status
coordinator-mcp query capabilities
coordinator-mcp query board --include_closed false
coordinator-mcp query board --ref develop --state Delivered
coordinator-mcp query card --task <task> --assignment <assignment>
coordinator-mcp query validate --task <task>
coordinator-mcp edit set_assignment --task <task> --assignment assignment.json
coordinator-mcp help
```

`query` and `edit` take any action of `coordinator_query` and `coordinator_edit`, with each parameter as `--<name> <value>`. A record parameter (`--task` for `create_task` and `update_task`, `--assignment` for `set_assignment`, `--decision`, `--round`) takes a `.json` file or JSON text; `true` and `false` are booleans; everything else, numbers too, is text. The project is the Unity project that contains the current folder, or `--project <path>`; `--pretty` indents the JSON. `status` includes the binary's own path (`cli`), so an agent connected over MCP can find the command line. `commands` lists the commands as JSON. Exit codes: 0 success, 1 internal error, 2 usage, 4 no Unity project, 6 the reply has `success: false`, 7 the Unity Editor is not reachable with `--source editor` after the wait, or it took an edit and gave no answer.

Every reply of `query` and `edit` has `source`: `editor` or `files`. A read with `--ref` never asks the Editor: it reads that commit from git.

- **No queue on one Editor.** A call goes to the Editor when its bridge answers now. When it does not (no Editor, a recompile, a domain reload) the binary answers at once from the files, a read and an edit alike, and `source_reason` says why. A busy Editor gets 5 s to start the call (`claim_seconds` in the request); a call it does not start in that time is cancelled there and never runs, and the binary runs it on the files.
- **Reads and edits without the Editor.** The binary runs the same `CoordinatorAgentApi.Query` and `CoordinatorAgentApi.Edit` on the Coordinator files and git, with the same validation and rollback: an edit that adds validation errors is rolled back and answers `validation_failed`. `approve_task` follows the workflow mode, which the binary reads from `Library/Coordinator/agent-mode.json`. `Tools~/CoordinatorCore` compiles the package's Editor code without Unity for this; files that need Unity are excluded there, and the others keep their Unity references inside `#if UNITY_EDITOR` (an Editor test checks it). The binary reads the Editor's git memo and never writes it.
- **One writer at a time.** Every edit, in the Editor (bridge, window buttons, Undo and Redo) and in every command line process, holds `Library/Coordinator/write.lock` for the whole change: read, change, write, validate, roll back. A second writer waits up to 10 s, then answers `busy` and changes nothing. The operating system frees the lock when its process ends.
- **The open Editor picks up the change.** An edit made without the Editor leaves its undo entry (name, every file before and after) in `Library/Coordinator/history-inbox`. The Editor checks the folder once a second, adds the entries to its Undo history and reloads the window, so a person can undo an agent's edit. Undo refuses, as always, when a file changed again since.
- **`--source`.** `--source editor` never reads the files and waits for the Editor: while Unity runs for the project (`Library/EditorInstance.json` names a live Unity process) but the bridge does not answer, the call tries again every 0.5 s for up to `--wait <seconds>` (default 60; `--wait 0` does not wait). An answer after a wait has `waited_seconds`. `--source files` never asks the Editor. An edit that the Editor took but did not answer is not repeated: exit 7, and check the result with a query.
- **Git worktrees.** In a linked git worktree the project is its main checkout when that checkout is a Unity project with `Coordinator/`: the lead's Editor answers, or its files do. `status` names the worktree as `checkout`, and `query scope` without `--checkout` or `--revision` checks that worktree. So an executor reads its card with `coordinator-mcp query card --task <task> --assignment <assignment>` and checks its zone with `coordinator-mcp query scope --task <task> --assignment <assignment>` from its own worktree.

The Editor side is the same for both: the bridge starts with every Editor that is not in batch mode and writes its port and token to `Library/Coordinator/agent-bridge.json`; nothing has to be enabled.

**Coordinator window > Agents** shows the **Coordinator MCP server** (tag MCP) and the **Coordinator CLI** (tag CLI) separately, with the colours of the Architector integrations. The CLI item shows the binary path, the version and whether the Editor bridge answers the binary (it runs `status` in the background), what works without the Editor, and copyable examples for a shell and PowerShell. The window downloads the server for this package version from its GitHub release (checked against the release's SHA-256 list) into `~/.coordinator/mcp/<version>` and connects Claude Code or Codex. When the package version changes, connected agents are moved to the matching server automatically. Before the first release, a developer can publish the server from `Tools~/CoordinatorMcp` with `dotnet publish` into the same folder. Copy `Skills~/coordinator-workflow` into the project's skills folder; it explains the workflow and the record format to agents.

## License

MIT
