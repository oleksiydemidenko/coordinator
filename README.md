# Coordinator

Agent task cards for Unity projects. In development; nothing is released yet.

A lead splits a task from the technical design into assignments for several executors, each a person working with an agent. Every assignment states what to read, its inputs and outputs, the only paths it may change, its route and its acceptance. Shared decisions are made before parallel work starts. Progress is computed from git, so no server is needed.

Coordinator does not launch or supervise agents. Executors work where they like, usually in their own git worktree without Unity; they read a generated Markdown card, check their write zone with the git commands in it and write a small delivery file. The lead reviews and accepts in the Editor.

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

**Coordinator window > Agents** builds the server (with the .NET SDK until releases ship prebuilt servers) and connects Claude Code or Codex. Copy `Skills~/coordinator-workflow` into the project's skills folder; it explains the workflow and the record format to agents.

## License

MIT
