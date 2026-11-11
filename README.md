# Coordinator

Agent task cards for Unity projects. Released: 0.1.1 (see the [changelog](CHANGELOG.md)). The model, validation, git-computed states, cards, agent access and the MCP server are complete; the window is a draft until its final design.

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

The open Unity Editor serves a transport-neutral API (`CoordinatorAgentApi`) through a local bridge. The MCP server in `Tools~/CoordinatorMcp` only forwards to it, so Coordinator works only with the Editor open:

- `coordinator_status`: project, data and whether the Editor is reachable;
- `coordinator_query`: `capabilities`, `tasks`, `board`, `task`, `card`, `validate`, `scope` (with `checkout`, checks an executor's worktree);
- `coordinator_edit`: `create_task`, `update_task`, `set_decision`, `set_assignment`, `remove_assignment`, `cancel_assignment`, `record_review`, `generate_cards`. An edit that adds validation errors is rolled back. `approve_task` always refuses: a person approves in the window, and acceptance names the person who decided.

**Coordinator window > Agents** downloads the server for this package version from its GitHub release (checked against the release's SHA-256 list) into `~/.coordinator/mcp/<version>` and connects Claude Code or Codex. When the package version changes, connected agents are moved to the matching server automatically. Before the first release, a developer can publish the server from `Tools~/CoordinatorMcp` with `dotnet publish` into the same folder. Copy `Skills~/coordinator-workflow` into the project's skills folder; it explains the workflow and the record format to agents.

## License

MIT
