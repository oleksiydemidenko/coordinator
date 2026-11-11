---
name: coordinator-workflow
description: Split a task from the technical design into Coordinator assignments with exclusive write zones, keep shared decisions ahead of parallel work, deliver an assignment from its card, and review deliveries. Use in a Unity project with a Coordinator folder, as the lead or as an executor.
---

# Coordinator Workflow

Coordinator keeps parallel work in `Coordinator/` at the project root: a task, its decisions and its assignments, each assignment with the only paths it may change. States come from git. Coordinator never launches agents.

## Roles

Coordinator is for parallel work of agents on one task, in one discipline or several. A narrow change one agent does alone needs no task.

- **Lead**: one per project. Splits the task, makes or records decisions, reviews every card and, at the end, the whole task branch. Works through the open Unity Editor with `coordinator_query` and `coordinator_edit`.
- **Operator**: the person who takes an assignment and runs agents for it (a developer, an artist, the sound designer). Checks the result, hands the card in to the lead, pushes and opens pull requests. An agent never pushes.
- **Executor**: the operator's main agent for one assignment. If the operator splits the work across several agent sessions, the executor orchestrates them and merges their work into its branch. Reads its card, changes only its zone, writes its delivery file. Its `environment` is `worktree` (its own git worktree without Unity) or `editor` (the operator's checkout with the Unity Editor open, which sound work always needs).
- **Person with authority**: approves the split and decides acceptance. An agent never approves a task, never accepts its own work and never widens its own zone.

## Lead

1. `coordinator_query` `capabilities`, then `board` or `tasks`.
2. Read the technical design the task delivers. Find the shared contracts first: each becomes a **decision**. Decide it with the user, or keep it `open`; record a one- or two-line `answer` and a `record` link (document section or Architector layer). Do not copy the full text.
3. Split the rest into **assignments**: one executor, one write zone, one checkable output. Discipline is an attribute, not the boundary. An assignment whose method is not yet decided depends on its decision.
4. `create_task` (a draft), then `set_decision` and `set_assignment`. Every edit is validated; an edit that adds errors is rolled back and returns them. Fix and resend.
5. Check `validate`: no zone overlaps, outputs inside zones, inputs that exist at the base commit, no dependency cycles, no reserved paths in zones. Warnings `layer_shared` and `layers_connected` mean two assignments touch the same or connected Architector layers: agree the contract first or merge them.
6. `generate_cards`, commit the `Coordinator` folder on the target branch, and ask the user to **Approve** the task in the Coordinator window. `approve_task` always refuses.
7. A card that waits for no other card starts from `base` (or the task branch tip); a card that waits for another card's output starts from the task branch tip once that output is merged there. Review every card when its operator hands it in: `card` shows the delivered revision, the delivery file and any problem, such as commits after the delivered revision that nobody reviewed. `scope` compares the delivered commits with the task branch, from their merge base to the revision, so work inherited from the task branch is not reported; with `checkout` it also lists the executor's uncommitted changes, which are not part of the delivery. Read the diff and run the lead's acceptance checks.
8. `record_review` with `verdict` `returned` and `findings`, or `accepted` with `acceptedBy` and `instructedBy` (the person who decided). Acceptance runs the scope check on the round's `revision` and records it in the round's `checks`; a path outside the zone or a reserved path refuses it with the paths: return the card, or widen the zone with `set_assignment` first, which is a recorded act. Regenerate the cards, so the card prints the accepted revision. The executor then merges the accepted revision, not the branch tip, into the task branch (`integrationBranch`); cards that wait for its output unblock once it is there. The board shows a card merged past its accepted revision.
9. Approval records in each task source the commit that last changed its file on the target branch. When a source gets a newer commit there, the board marks the task "requirement changed" with the file and the commits; nothing is blocked. Handle it as the requirements say: a draft task is edited; a card not yet taken is edited and regenerated; a card in progress gets a decision, or for a large change is cancelled and reissued; a delivered or accepted card gets a new card. The mark clears when a decided decision's `record` names that `path` with a `revision` at or after the change, or when you set the source's `revision` with `update_task` after reissuing the work.
10. When every card is merged, check the whole task branch against the cards, with the operators when it needs testing together. Only after the lead's approval is the task branch merged into the target branch.

## Executor

1. Read your card from the lead's branch, not from your checkout, which has the card as it was at the base: `git show <target branch>:Coordinator/Tasks/<task>/Cards/<assignment>.md`. Start your branch where the card says: from `base` when it waits for no other card, otherwise from the task branch tip once the outputs it waits for are merged there.
2. Read what the card names under Read First, in order. Use only decided decisions; never settle an open one.
3. Write `Coordinator/Tasks/<task>/<assignment>.delivery.json` with `state` `inProgress` in your first commit.
4. Follow the route. Change only the write zone. Reserved paths stay unchanged even inside it. If the work needs another path, stop and tell the lead.
5. Run the acceptance checks marked for you and the card's scope commands. They compare your commits with the task branch, and each prints nothing when you are inside the zone. `git status --porcelain` lists uncommitted changes: they are not part of your delivery, so commit or remove each.
6. In your final commit set `state` to `delivered` and report: `checks` you ran (`acceptance` or `check` id, `command`, `exitCode`, `result`), public `contracts`, `integrationRequirements`, `unverified` (prepared but not executed, such as tests that need Unity), and `judgments`: every choice the card did not settle, with `text`, `reason` and optional route `step`. Do not log your actions. Do not write commit hashes: Coordinator reads them from git.
7. Never push: your operator hands the card in. When the lead accepts it, merge the accepted revision into the task branch with the commands in the card, never into the target branch. If your branch has commits after the accepted revision, they are not reviewed: deliver again instead.

## Records

All files are JSON with `schemaVersion` 1; unknown fields are errors. Ids are lowercase kebab-case. Paths are repository-relative with `/`; zone and reserved patterns use git glob syntax (`**` crosses folders, `*` does not).

- **Task** (`task.json`): `id`, `title`, `lead`, `sources` (`path`, `sections`, `requirements`, `note`, `revision`: written at approval), `targetBranch`, `integrationBranch` (the task branch, required and different from the target), `base` (`branch`, `commit`), `reservedPaths` (`pattern`, `reason`), `decisions`, `forbidden` (`text`, `refs`), `integration` (`dependsOn`, `route`, `acceptance`, `limits`). `approval` and `cancelled` are set by people.
- **Decision**: `id`, `question`, `owner`, `state` (`open` or `decided`), `answer`, `record` (`path` and `section`, or `layer`; `revision` when the decision answers a change of that source).
- **Assignment** (`<id>.assignment.json`): `id`, `title`, `disciplines`, `executor` (`handle`, `branch`, `environment`: `worktree` or `editor`), `goal`, `readFirst`, `layers` (`path`, `role`: `location`, `reads` or `zone`), `inputs` (`kind` `source` with `path`; `output` with `assignment` and `output`; `decision` with `decision`; `resource` with `text` and `supplied`), `outputs` (`id`, `kind`: `code`, `asset`, `configuration` or `document`, `paths`, `summary`), `writeZone`, `dependsOn` (`decisions`, `outputs`), `route` (`text`, `check` or `checks`), `forbidden`, `acceptance` (`id`, `text`, `command`, `check` or `checks`, `testFilter`, `runBy`: `executor` or `lead`), `delivery` (`report`).
- **Review round**: `revision`, `date`, `reviews` (`author`, `kind`, `text`), `checks`, `findings` (`id`, `text`, `path`), `verdict`, `acceptedBy`, `instructedBy`.

Project-wide disciplines, reserved paths, variables, common checks and rules live in `Coordinator/coordinator.project.json`; commands use `${variable}` for machine-specific values. A check or rule with `environments` (`worktree`, `editor`) applies only to executors in those environments; without it, it applies to all.

## States

Draft (not approved), Blocked (open decision, output not accepted and merged, resource not supplied), Ready, In Progress, Delivered, Returned, Accepted, Merged (accepted revision is in the task branch), Closed (accepted revision is in the target branch), Cancelled. A task is Draft, Approved, In Work, Integrated or Closed.
