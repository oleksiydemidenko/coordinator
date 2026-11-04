---
name: coordinator-workflow
description: Split a task from the technical design into Coordinator assignments with exclusive write zones, keep shared decisions ahead of parallel work, deliver an assignment from its card, and review deliveries. Use in a Unity project with a Coordinator folder, as the lead or as an executor.
---

# Coordinator Workflow

Coordinator keeps parallel work in `Coordinator/` at the project root: a task, its decisions and its assignments, each assignment with the only paths it may change. States come from git. Coordinator never launches agents.

## Roles

- **Lead**: splits the task, makes or records decisions, reviews and integrates. Works through the open Unity Editor with `coordinator_query` and `coordinator_edit`.
- **Executor**: one person or agent per assignment, usually in its own git worktree without Unity. Reads its card, changes only its zone, writes its delivery file.
- **Person**: approves the split and decides acceptance. An agent never approves a task, never accepts its own work and never widens its own zone.

## Lead

1. `coordinator_query` `capabilities`, then `board` or `tasks`.
2. Read the technical design the task delivers. Find the shared contracts first: each becomes a **decision**. Decide it with the user, or keep it `open`; record a one- or two-line `answer` and a `record` link (document section or Architector layer). Do not copy the full text.
3. Split the rest into **assignments**: one executor, one write zone, one checkable output. Discipline is an attribute, not the boundary. An assignment whose method is not yet decided depends on its decision.
4. `create_task` (a draft), then `set_decision` and `set_assignment`. Every edit is validated; an edit that adds errors is rolled back and returns them. Fix and resend.
5. Check `validate`: no zone overlaps, outputs inside zones, inputs that exist at the base commit, no dependency cycles, no reserved paths in zones. Warnings `layer_shared` and `layers_connected` mean two assignments touch the same or connected Architector layers: agree the contract first or merge them.
6. `generate_cards`, commit the `Coordinator` folder on the target branch, and ask the user to **Approve** the task in the Coordinator window. `approve_task` always refuses.
7. Executors start from `base`. To review a delivery: `card` shows the delivered revision and the delivery file; `scope` with `checkout` checks an executor's worktree. Read the diff and run the lead's acceptance checks.
8. `record_review` with `verdict` `returned` and `findings`, or `accepted` with `acceptedBy` and `instructedBy` (the person who decided). Merge accepted revisions one at a time into the integration branch, then the target branch.

## Executor

1. Read your card from the lead's branch, not from your checkout, which has the card as it was at the base: `git show <target branch>:Coordinator/Tasks/<task>/Cards/<assignment>.md`.
2. Read what the card names under Read First, in order. Use only decided decisions; never settle an open one.
3. Write `Coordinator/Tasks/<task>/<assignment>.delivery.json` with `state` `inProgress` in your first commit.
4. Follow the route. Change only the write zone. Reserved paths stay unchanged even inside it. If the work needs another path, stop and tell the lead.
5. Run the acceptance checks marked for you and the card's scope commands: each prints nothing when you are inside the zone.
6. In your final commit set `state` to `delivered` and report: `checks` you ran (`acceptance` or `check` id, `command`, `exitCode`, `result`), public `contracts`, `integrationRequirements`, `unverified` (prepared but not executed, such as tests that need Unity), and `judgments`: every choice the card did not settle, with `text`, `reason` and optional route `step`. Do not log your actions. Do not write commit hashes: Coordinator reads them from git.

## Records

All files are JSON with `schemaVersion` 1; unknown fields are errors. Ids are lowercase kebab-case. Paths are repository-relative with `/`; zone and reserved patterns use git glob syntax (`**` crosses folders, `*` does not).

- **Task** (`task.json`): `id`, `title`, `lead`, `sources` (`path`, `sections`, `requirements`, `note`), `targetBranch`, `integrationBranch`, `base` (`branch`, `commit`), `reservedPaths` (`pattern`, `reason`), `decisions`, `forbidden` (`text`, `refs`), `integration` (`dependsOn`, `route`, `acceptance`, `limits`). `approval` and `cancelled` are set by people.
- **Decision**: `id`, `question`, `owner`, `state` (`open` or `decided`), `answer`, `record` (`path` and `section`, or `layer`).
- **Assignment** (`<id>.assignment.json`): `id`, `title`, `disciplines`, `executor` (`handle`, `branch`), `goal`, `readFirst`, `layers` (`path`, `role`: `location`, `reads` or `zone`), `inputs` (`kind` `source` with `path`; `output` with `assignment` and `output`; `decision` with `decision`; `resource` with `text` and `supplied`), `outputs` (`id`, `kind`: `code`, `asset`, `configuration` or `document`, `paths`, `summary`), `writeZone`, `dependsOn` (`decisions`, `outputs`), `route` (`text`, `check` or `checks`), `forbidden`, `acceptance` (`id`, `text`, `command`, `check` or `checks`, `testFilter`, `runBy`: `executor` or `lead`), `delivery` (`report`).
- **Review round**: `revision`, `date`, `reviews` (`author`, `kind`, `text`), `checks`, `findings` (`id`, `text`, `path`), `verdict`, `acceptedBy`, `instructedBy`.

Project-wide disciplines, reserved paths, variables, common checks and rules live in `Coordinator/coordinator.project.json`; commands use `${variable}` for machine-specific values.

## States

Draft (not approved), Blocked (open decision, output not accepted, resource not supplied), Ready, In Progress, Delivered, Returned, Accepted, Closed (accepted revision is in the target branch), Cancelled. A task is Draft, Approved, In Work, Integrated or Closed.
