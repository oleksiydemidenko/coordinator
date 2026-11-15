# Coordinator

Agent task cards for Unity projects. Released: 0.2.1 (see the [changelog](CHANGELOG.md)). The model, validation, git-computed states, cards, agent access and the MCP server are complete; the window is a draft until its final design.

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

The open Unity Editor serves a transport-neutral API (`CoordinatorAgentApi`) through a local bridge. The MCP server in `Tools~/CoordinatorMcp` forwards to it. Edits need the open Editor; reads are also answered without it (see below):

- `coordinator_status`: project, data and whether the Editor is reachable;
- `coordinator_query`: `capabilities`, `tasks`, `board`, `task`, `card`, `validate`, `scope` (compares the delivered commits with the task branch; with `checkout`, also lists the executor's uncommitted changes);
- `coordinator_edit`: `create_task`, `update_task`, `set_decision`, `set_assignment`, `remove_assignment`, `cancel_assignment`, `record_review`, `generate_cards`. An edit that adds validation errors is rolled back. `approve_task` always refuses: a person approves in the window, and acceptance names the person who decided. Acceptance runs the scope check on the accepted revision, records it in the review and is refused when a path leaves the zone.

The same binary is also a command line, for agents that run shell commands and have no MCP server registered. With a command it answers one line of JSON and exits; without one it is the MCP server, so an MCP registration (`--project <path>` or a bare project folder) keeps working:

```
coordinator-mcp status
coordinator-mcp query capabilities
coordinator-mcp query board --include_closed false
coordinator-mcp query card --task <task> --assignment <assignment>
coordinator-mcp edit set_assignment --task <task> --assignment assignment.json
coordinator-mcp help
```

`query` and `edit` take any action of `coordinator_query` and `coordinator_edit`, with each parameter as `--<name> <value>`. A record parameter (`--task` for `create_task` and `update_task`, `--assignment` for `set_assignment`, `--decision`, `--round`) takes a `.json` file or JSON text; `true` and `false` are booleans; everything else, numbers too, is text. The project is the Unity project that contains the current folder, or `--project <path>`; `--pretty` indents the JSON. `status` includes the binary's own path (`cli`), so an agent connected over MCP can find the command line. `commands` lists the commands as JSON. Exit codes: 0 success, 1 internal error, 2 usage, 4 no Unity project, 6 the reply has `success: false`, 7 the Unity Editor is not reachable after the wait (an edit, or a query with `--source editor`).

Every reply of `query` and `edit` has `source`: `editor` or `files`.

- **The call waits for the Editor.** While Unity runs for the project (`Library/EditorInstance.json` names a live Unity process) but the bridge does not answer, for example during a recompile or a domain reload, the call tries again every 0.5 s for up to `--wait <seconds>` (default 60, because a domain reload of a large project can keep the bridge away for about 30 s; `--wait 0` does not wait). An answer after a wait has `waited_seconds`. An edit that the Editor took but did not answer is not repeated: exit 7, and check the result with a query.
- **Reads without the Editor.** When no Editor answers, `query` runs the same `CoordinatorAgentApi.Query` inside the binary on the Coordinator files and git, and `source_reason` says why. `Tools~/CoordinatorCore` compiles the package's Editor code without Unity for this; files that need Unity are excluded there, and the others keep their Unity references inside `#if UNITY_EDITOR` (an Editor test checks it). The binary reads the Editor's git memo and never writes it. `--source editor` never reads the files; `--source files` never asks the Editor. Edits always go through the Editor.
- **Git worktrees.** In a linked git worktree the project is its main checkout when that checkout is a Unity project with `Coordinator/`: the lead's Editor answers, or its files do. `status` names the worktree as `checkout`, and `query scope` without `--checkout` or `--revision` checks that worktree. So an executor reads its card with `coordinator-mcp query card --task <task> --assignment <assignment>` and checks its zone with `coordinator-mcp query scope --task <task> --assignment <assignment>` from its own worktree.

The Editor side is the same for both: the bridge starts with every Editor that is not in batch mode and writes its port and token to `Library/Coordinator/agent-bridge.json`; nothing has to be enabled.

**Coordinator window > Agents** shows the **Coordinator MCP server** (tag MCP) and the **Coordinator CLI** (tag CLI) separately, with the colours of the Architector integrations. The CLI item shows the binary path, the version and whether the Editor bridge answers the binary (it runs `status` in the background), what works without the Editor, and copyable examples for a shell and PowerShell. The window downloads the server for this package version from its GitHub release (checked against the release's SHA-256 list) into `~/.coordinator/mcp/<version>` and connects Claude Code or Codex. When the package version changes, connected agents are moved to the matching server automatically. Before the first release, a developer can publish the server from `Tools~/CoordinatorMcp` with `dotnet publish` into the same folder. Copy `Skills~/coordinator-workflow` into the project's skills folder; it explains the workflow and the record format to agents.

## License

MIT
