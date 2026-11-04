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

## License

MIT
