# Changelog

## Unreleased

- Data model and files of `Coordinator/`: project, tasks, decisions, assignments, deliveries and reviews.
- Task validation: ids and references, write zones against reserved paths and against each other, outputs inside zones, inputs, dependency cycles.
- Scope check commands generated from an assignment's base and zone.
- A delivery lists its `judgments`: choices the card did not settle, each with its reason and optional route step.
- States computed from git: an assignment is Draft, Blocked (open decisions, outputs not accepted, resources not supplied), Ready, In Progress, Delivered, Returned, Accepted or Closed from its executor branch, the lead's reviews and the target branch; a task is Draft, Approved, In Work, Integrated or Closed.
- Source inputs are checked at the task's base commit.
- Generated Markdown cards and a task summary in each task's `Cards` folder.
- Architector files are read as data: a layer can be an assignment's zone (its scope folders), and two open assignments touching the same or connected layers are warned.
- A toolbar button and a draft window list tasks, states and diagnostics and generate cards.
- The window is a board: one column per project discipline in the project's order (Other for undeclared ones), each assignment in the column of its first discipline, ordered by work state; a task filter, a Closed toggle and diagnostics. Clicking a card opens its generated text beside the board.
- Agent access: `CoordinatorAgentApi` (query and edit, with rollback of edits that add validation errors; tasks are approved only by a person, acceptance names the person who decided), a local Editor bridge, the `coordinator-mcp` server in `Tools~/CoordinatorMcp` with a smoke test and a release workflow, an Agents window that builds the server and connects Claude Code or Codex, and the `coordinator-workflow` skill.
