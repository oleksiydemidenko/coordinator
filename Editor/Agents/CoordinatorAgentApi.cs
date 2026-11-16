using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using UnityEngine;
#endif

namespace Coordinator.Editor
{
    public static class CoordinatorAgentApi
    {
        public static readonly string[] QueryActions = { "capabilities", "tasks", "board", "task", "card", "validate", "scope" };
        public static readonly string[] EditActions =
        {
            "create_task", "update_task", "set_decision", "set_assignment", "remove_assignment", "cancel_assignment",
            "record_review", "generate_cards", "approve_task"
        };

        // The command line compiles this file without Unity (Tools~/CoordinatorCore): there the project root is always passed in.
#if UNITY_EDITOR
        public static string DefaultRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
#else
        public static string DefaultRoot => Directory.GetCurrentDirectory();
#endif

        public static CoordinatorAgentResult Query(JObject parameters, string projectRoot = null)
        {
            var root = projectRoot ?? DefaultRoot;
            var action = (string)parameters?["action"] ?? string.Empty;
            try
            {
                if (!QueryActions.Contains(action))
                    return CoordinatorAgentResult.Fail("unknown_action", "Query actions: " + string.Join(", ", QueryActions) + ".");
                if (action == "capabilities")
                    return Capabilities(root);
                var reading = Reading.Open(root, (string)parameters["ref"]);
                return action switch
                {
                    "tasks" => Tasks(reading, (bool?)parameters["include_closed"] ?? true),
                    "board" => Board(reading, (string)parameters["task"], (bool?)parameters["include_closed"] ?? true, StateFilter((string)parameters["state"])),
                    "task" => Task(reading, Required(parameters, "task")),
                    "card" => Card(reading, Required(parameters, "task"), Required(parameters, "assignment")),
                    "validate" => Validate(reading, (string)parameters["task"]),
                    _ => Scope(reading, Required(parameters, "task"), Required(parameters, "assignment"), (string)parameters["checkout"], (string)parameters["revision"])
                };
            }
            catch (CoordinatorAgentException exception)
            {
                return CoordinatorAgentResult.Fail(exception.Code, exception.Message);
            }
            catch (CoordinatorGitException exception)
            {
                return CoordinatorAgentResult.Fail("git_failed", exception.Message);
            }
        }

        public static CoordinatorAgentResult Edit(JObject parameters, string projectRoot = null)
        {
            var root = projectRoot ?? DefaultRoot;
            var action = (string)parameters?["action"] ?? string.Empty;
            try
            {
                if (parameters?["ref"] != null)
                    return CoordinatorAgentResult.Fail("invalid_parameters", "Edits change the Coordinator files of this checkout; ref is for reads.");
                // One writer at a time: the Editor and every command line take the same lock for the whole change.
                return !EditActions.Contains(action)
                    ? EditUnlocked(root, action, parameters)
                    : CoordinatorWriteLock.Run(root, () => EditUnlocked(root, action, parameters));
            }
            catch (CoordinatorAgentException exception)
            {
                return CoordinatorAgentResult.Fail(exception.Code, exception.Message);
            }
        }

        private static CoordinatorAgentResult EditUnlocked(string root, string action, JObject parameters)
        {
            try
            {
                return action switch
                {
                    "create_task" => CreateTask(root, Record<CoordinatorTaskRecord>(parameters, "task")),
                    "update_task" => UpdateTask(root, Record<CoordinatorTaskRecord>(parameters, "task")),
                    "set_decision" => SetDecision(root, Required(parameters, "task"), Record<CoordinatorDecisionRecord>(parameters, "decision")),
                    "set_assignment" => SetAssignment(root, Required(parameters, "task"), Record<CoordinatorAssignmentRecord>(parameters, "assignment")),
                    "remove_assignment" => RemoveAssignment(root, Required(parameters, "task"), Required(parameters, "assignment")),
                    "cancel_assignment" => CancelAssignment(root, Required(parameters, "task"), Required(parameters, "assignment"),
                        Required(parameters, "by"), Required(parameters, "reason")),
                    "record_review" => RecordReview(root, Required(parameters, "task"), Required(parameters, "assignment"),
                        Record<CoordinatorReviewRoundRecord>(parameters, "round")),
                    "generate_cards" => CoordinatorAgentResult.Ok("Cards generated.", new { written = CoordinatorCards.WriteAll(CoordinatorWorkspace.Load(root)) }),
                    "approve_task" => ApproveByAgent(root, parameters),
                    _ => CoordinatorAgentResult.Fail("unknown_action", "Edit actions: " + string.Join(", ", EditActions) + ".")
                };
            }
            catch (CoordinatorAgentException exception)
            {
                return CoordinatorAgentResult.Fail(exception.Code, exception.Message);
            }
        }

        public static CoordinatorAgentResult ApproveByPerson(string projectRoot, string taskId, string person)
        {
            try
            {
                return CoordinatorWriteLock.Run(projectRoot, () => ApproveByPersonUnlocked(projectRoot, taskId, person));
            }
            catch (CoordinatorAgentException exception)
            {
                return CoordinatorAgentResult.Fail(exception.Code, exception.Message);
            }
        }

        private static CoordinatorAgentResult ApproveByPersonUnlocked(string projectRoot, string taskId, string person)
        {
            var data = LoadTask(projectRoot, taskId);
            if (data.Task.approval != null)
                return CoordinatorAgentResult.Fail("already_approved", $"Task '{taskId}' is already approved.");
            data.Task.approval = new CoordinatorSignatureRecord { by = person, date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            RecordSourceRevisions(Git(projectRoot), data.Task);
            return Commit(projectRoot, $"Task '{taskId}' approved by {person}.", Documents((TaskFile(projectRoot, taskId), data.Task)));
        }

        public static CoordinatorAgentResult ApproveAllByPerson(string projectRoot, IReadOnlyList<string> taskIds, string person)
        {
            try
            {
                return CoordinatorWriteLock.Run(projectRoot, () => ApproveAllByPersonUnlocked(projectRoot, taskIds, person));
            }
            catch (CoordinatorAgentException exception)
            {
                return CoordinatorAgentResult.Fail(exception.Code, exception.Message);
            }
        }

        private static CoordinatorAgentResult ApproveAllByPersonUnlocked(string projectRoot, IReadOnlyList<string> taskIds, string person)
        {
            var workspace = CoordinatorWorkspace.Load(projectRoot);
            var date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var documents = new Dictionary<string, object>();
            var git = Git(projectRoot);
            foreach (var taskId in taskIds)
            {
                var data = workspace.FindTask(taskId) ?? throw new CoordinatorAgentException("task_not_found", $"No task '{taskId}'.");
                if (data.Task.approval != null || data.Task.cancelled != null)
                    continue;
                data.Task.approval = new CoordinatorSignatureRecord { by = person, date = date };
                RecordSourceRevisions(git, data.Task);
                documents[TaskFile(projectRoot, taskId)] = data.Task;
            }
            if (documents.Count == 0)
                return CoordinatorAgentResult.Fail("nothing_to_approve", "No draft task to approve.");
            return Commit(projectRoot, $"{documents.Count} task(s) approved by {person}.", documents);
        }

        // Lazy mode: the lead agent approves a draft after it checked it again. The workflow mode of this checkout must let
        // agents approve, validate must show nothing for the task (errors and warnings, its own files and zones shared with
        // other tasks), and the approval names the agent (by) and the person who instructed it (instructedBy).
        private static CoordinatorAgentResult ApproveByAgent(string root, JObject parameters)
        {
            var mode = CoordinatorWorkflowModes.Read(root);
            if (!mode.AgentMayApprove)
                return CoordinatorAgentResult.Fail("needs_person",
                    $"The workflow mode is {mode.DisplayName}: only a person approves a task, with Approve in the Coordinator window. Ask the user to review the split and approve it. "
                    + "In Lazy mode, which a person picks in the Coordinator window, the lead agent approves.");
            var taskId = Required(parameters, "task");
            var agent = ((string)parameters["by"])?.Trim();
            var person = ((string)parameters["instructed_by"] ?? (string)parameters["instructedBy"])?.Trim();
            if (string.IsNullOrEmpty(agent) || string.IsNullOrEmpty(person))
                return CoordinatorAgentResult.Fail("needs_person", "An agent's approval names the agent (by) and the person who instructed it (instructed_by).");
            if (string.Equals(agent, person, StringComparison.OrdinalIgnoreCase))
                return CoordinatorAgentResult.Fail("invalid_parameters", "by names the agent and instructed_by the person who instructed it; they differ. A person approves in the Coordinator window.");

            var workspace = CoordinatorWorkspace.Load(root);
            var data = workspace.FindTask(taskId) ?? throw new CoordinatorAgentException("task_not_found", $"No task '{taskId}'.");
            if (data.Task.approval != null)
                return CoordinatorAgentResult.Fail("already_approved", $"Task '{taskId}' is already approved.");
            if (data.Task.cancelled != null)
                return CoordinatorAgentResult.Fail("task_cancelled", $"Task '{taskId}' is cancelled.");
            var git = Git(root);
            var found = TaskDiagnostics(Validated(git, workspace), taskId).ToArray();
            if (found.Length > 0)
            {
                var errors = found.Count(diagnostic => diagnostic.IsError);
                return CoordinatorAgentResult.Fail("not_clean",
                    $"Approval refused: validate shows {errors} error(s) and {found.Length - errors} warning(s) for task '{taskId}'. Fix them, or ask the user to approve in the Coordinator window.",
                    new { diagnostics = Diagnostics(found) });
            }
            data.Task.approval = new CoordinatorSignatureRecord
            {
                by = agent,
                instructedBy = person,
                date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
            RecordSourceRevisions(git, data.Task);
            return Commit(root, $"Task '{taskId}' approved by {agent}, instructed by {person} (Lazy mode). Regenerate the cards and tell the user.",
                Documents((TaskFile(root, taskId), data.Task)));
        }

        private static void RecordSourceRevisions(CoordinatorGit git, CoordinatorTaskRecord task)
        {
            if (git == null)
                return;
            foreach (var source in task.sources.Where(source => !string.IsNullOrEmpty(source.path)))
            {
                try
                {
                    source.revision = git.CommitsTouching(task.targetBranch, source.path).FirstOrDefault();
                }
                catch (CoordinatorGitException)
                {
                    source.revision = null;
                }
            }
        }

        private static CoordinatorAgentResult Capabilities(string root)
        {
            var mode = CoordinatorWorkflowModes.Read(root);
            return CoordinatorAgentResult.Ok("Coordinator capabilities.", new
            {
                schema_version = CoordinatorValidation.SchemaVersion,
                query_actions = QueryActions,
                edit_actions = EditActions,
                workflow_mode = CoordinatorWorkflowModes.Json(mode),
                assignment_states = AssignmentStates,
                rules = new[]
                {
                    "Records use the JSON fields of the coordinator-workflow skill; unknown fields are rejected.",
                    "An edit that adds validation errors is rolled back and returns them.",
                    "Follow workflow_mode.agent_rule. A person picks the mode in the Coordinator window; never change it yourself.",
                    mode.AgentMayApprove
                        ? "Lazy mode: approve_task approves a draft when validate with the task shows nothing; it needs by (your agent name) and instructed_by (the person who gave you the work), and records both."
                        : "Only a person approves a task, in the Coordinator window: approve_task refuses in this workflow mode (needs_person).",
                    "Reads take ref (a branch, tag or commit): tasks, board, task, card, validate and scope then read the Coordinator files of that commit, so the lead sees, for example, develop's tasks from a checkout on a task branch. States always come from the branches in git.",
                    "An edit runs in the open Editor when its bridge answers, otherwise in the command line on the Coordinator files, with the same checks. One writer at a time: a second writer waits up to 10 s, then gets busy.",
                    "record_review with verdict accepted needs acceptedBy and instructedBy, the person who decided to accept.",
                    "record_review with verdict accepted runs the scope check on the round's revision against the task branch and records it in checks; a path outside the zone or a reserved path refuses acceptance.",
                    "scope compares the delivered commits with the task branch (merge base to revision); with checkout it also lists uncommitted changes, which are not part of the delivery.",
                    "validate with task lists only that task's diagnostics, zones shared with other tasks included. A Closed card is history: validation checks that its files read, not that its delivery and checks still match the project file.",
                    "States are computed from git: never write them. Executors write their delivery file in their own branch.",
                    "Approval records in each task source the commit that last changed its file on the target branch. A newer commit marks the task 'requirement changed'; a decided decision whose record names that path and a revision at or after the change answers it, or the lead sets the source's revision with update_task.",
                    "Changing a task after approval keeps the approval; tell the user what changed."
                }
            });
        }

        private static readonly string[] AssignmentStates =
        {
            CoordinatorAssignmentStates.Draft, CoordinatorAssignmentStates.Blocked, CoordinatorAssignmentStates.Ready,
            CoordinatorAssignmentStates.InProgress, CoordinatorAssignmentStates.Delivered, CoordinatorAssignmentStates.Returned,
            CoordinatorAssignmentStates.Accepted, CoordinatorAssignmentStates.Merged, CoordinatorAssignmentStates.Closed,
            CoordinatorAssignmentStates.Cancelled
        };

        // What a read sees: the Coordinator files of the checkout, or of a commit when the call names ref. States come from
        // the branches in git either way.
        private sealed class Reading
        {
            public CoordinatorWorkspace Workspace { get; private set; }
            public CoordinatorGit Git { get; private set; }
            public string Ref { get; private set; }
            public string Commit { get; private set; }
            public object At => Ref == null ? null : new { @ref = Ref, commit = Commit };

            public static Reading Open(string root, string gitRef)
            {
                var git = CoordinatorAgentApi.Git(root);
                if (string.IsNullOrWhiteSpace(gitRef))
                    return new Reading { Workspace = CoordinatorWorkspace.Load(root), Git = git };
                if (git == null)
                    throw new CoordinatorAgentException("not_git", "A read with ref needs git, and this project is not a git repository.");
                var name = gitRef.Trim();
                var commit = git.Resolve(name)
                             ?? throw new CoordinatorAgentException("ref_not_found", $"'{name}' is not a branch, tag or commit in this repository.");
                var files = git.Tree(commit, new[] { CoordinatorPaths.RootFolder, CoordinatorArchitector.RootFolder }, new[] { ".json" });
                return new Reading
                {
                    Workspace = CoordinatorWorkspace.Load(root, new CoordinatorTreeSource(root, files)),
                    Git = git,
                    Ref = name,
                    Commit = commit
                };
            }
        }

        // "delivered", "Delivered", "in progress", "in_progress" and "inprogress" all name In Progress or Delivered.
        private static HashSet<string> StateFilter(string states)
        {
            if (string.IsNullOrWhiteSpace(states))
                return null;
            static string Plain(string text) => new string(text.Where(char.IsLetter).ToArray());
            var filter = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in states.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(state => state.Trim()))
            {
                var match = AssignmentStates.FirstOrDefault(candidate => string.Equals(Plain(candidate), Plain(state), StringComparison.OrdinalIgnoreCase))
                            ?? throw new CoordinatorAgentException("invalid_parameters", $"State '{state}' is not one of: {string.Join(", ", AssignmentStates)}.");
                filter.Add(match);
            }
            return filter;
        }

        private static CoordinatorAgentResult Tasks(Reading reading, bool includeClosed)
        {
            var git = reading.Git;
            return CoordinatorAgentResult.Ok("Coordinator tasks.", new
            {
                at = reading.At,
                tasks = reading.Workspace.Tasks
                    .Select(data => (data, status: git == null ? null : CoordinatorStates.Compute(data, git)))
                    .Where(item => includeClosed || item.status == null
                                   || item.status.State != CoordinatorTaskStates.Closed && item.status.State != CoordinatorTaskStates.Cancelled)
                    .Select(item => new
                    {
                        id = item.data.Task.id,
                        title = item.data.Task.title,
                        state = item.status?.State,
                        lead = item.data.Task.lead,
                        target_branch = item.data.Task.targetBranch,
                        approval = Approval(item.data.Task),
                        assignments = item.data.Assignments.Count,
                        open_decisions = item.data.Task.decisions.Where(decision => decision.state == CoordinatorDecisionStates.Open).Select(decision => decision.id).ToArray(),
                        requirement_changes = RequirementChanges(item.status, item.data.Task)
                    }).ToArray()
            });
        }

        private static object Approval(CoordinatorTaskRecord task) =>
            task.approval == null ? null : new { by = task.approval.by, instructed_by = task.approval.instructedBy, date = task.approval.date };

        private static CoordinatorAgentResult Board(Reading reading, string taskId, bool includeClosed, HashSet<string> states)
        {
            var workspace = reading.Workspace;
            var statuses = Statuses(reading.Git, workspace);
            return CoordinatorAgentResult.Ok("Coordinator board.", new
            {
                at = reading.At,
                columns = CoordinatorBoard.Build(workspace, statuses, taskId, includeClosed).Select(column => new
                {
                    discipline = column.DisciplineId,
                    title = column.Title,
                    cards = column.Cards.Where(card => states == null || states.Contains(card.State)).Select(card => new
                    {
                        task = card.Task.Task.id,
                        assignment = card.Assignment.id,
                        title = card.Assignment.title,
                        state = card.State,
                        blocked_by = card.Status?.BlockedBy.Count > 0 ? card.Status.BlockedBy : null,
                        problem = card.Status?.Problem,
                        requirement_changed = card.TaskStatus?.RequirementChanges.Count > 0
                            ? card.TaskStatus.RequirementChanges.Select(change => change.Text(card.Task.Task.targetBranch)).ToArray()
                            : null,
                        executor = card.Assignment.executor?.handle
                    }).ToArray()
                }).Where(column => states == null || column.cards.Length > 0).ToArray()
            });
        }

        private static CoordinatorAgentResult Task(Reading reading, string taskId)
        {
            var workspace = reading.Workspace;
            var data = workspace.FindTask(taskId) ?? throw new CoordinatorAgentException("task_not_found", $"No task '{taskId}'{AtText(reading)}.");
            var git = reading.Git;
            var status = git == null ? null : CoordinatorStates.Compute(data, git);
            return CoordinatorAgentResult.Ok("Coordinator task.", new
            {
                at = reading.At,
                state = status?.State,
                requirement_changes = RequirementChanges(status, data.Task),
                task = Json(data.Task),
                assignments = data.Assignments.Select(assignment =>
                {
                    var assignmentStatus = status?.Find(assignment.id);
                    return new
                    {
                        id = assignment.id,
                        title = assignment.title,
                        disciplines = assignment.disciplines,
                        state = assignmentStatus?.State,
                        blocked_by = assignmentStatus?.BlockedBy.Count > 0 ? assignmentStatus.BlockedBy : null,
                        delivered_revision = assignmentStatus?.DeliveredRevision,
                        problem = assignmentStatus?.Problem
                    };
                }).ToArray(),
                diagnostics = Diagnostics(TaskDiagnostics(Validated(git, workspace), taskId))
            });
        }

        private static CoordinatorAgentResult Card(Reading reading, string taskId, string assignmentId)
        {
            var workspace = reading.Workspace;
            var data = workspace.FindTask(taskId) ?? throw new CoordinatorAgentException("task_not_found", $"No task '{taskId}'{AtText(reading)}.");
            var assignment = data.Assignments.FirstOrDefault(candidate => candidate.id == assignmentId)
                             ?? throw new CoordinatorAgentException("assignment_not_found", $"Task '{taskId}' has no assignment '{assignmentId}'{AtText(reading)}.");
            var git = reading.Git;
            var status = git == null ? null : CoordinatorStates.Compute(data, git).Find(assignmentId);
            data.Reviews.TryGetValue(assignmentId, out var review);
            return CoordinatorAgentResult.Ok("Coordinator card.", new
            {
                at = reading.At,
                state = status?.State,
                blocked_by = status?.BlockedBy.Count > 0 ? status.BlockedBy : null,
                delivered_revision = status?.DeliveredRevision,
                delivery_problem = status?.Problem,
                delivery = status?.Delivery == null ? null : Json(status.Delivery),
                review = review == null ? null : Json(review),
                diagnostics = Diagnostics(CardDiagnostics(Validated(git, workspace), taskId, assignmentId)),
                card = CoordinatorCards.Assignment(workspace.Project, data, assignment, workspace.Architector)
            });
        }

        private static CoordinatorAgentResult Validate(Reading reading, string taskId)
        {
            var workspace = reading.Workspace;
            var one = !string.IsNullOrWhiteSpace(taskId);
            if (one && workspace.FindTask(taskId) == null)
                throw new CoordinatorAgentException("task_not_found", $"No task '{taskId}'{AtText(reading)}.");
            var all = Validated(reading.Git, workspace);
            var diagnostics = one ? TaskDiagnostics(all, taskId).ToArray() : all;
            return CoordinatorAgentResult.Ok(one ? $"Coordinator validation of task '{taskId}'." : "Coordinator validation.", new
            {
                at = reading.At,
                task = one ? taskId : null,
                clean = diagnostics.Count == 0,
                errors = diagnostics.Count(diagnostic => diagnostic.IsError),
                warnings = diagnostics.Count(diagnostic => !diagnostic.IsError),
                diagnostics = Diagnostics(diagnostics)
            });
        }

        private static string AtText(Reading reading) => reading.Ref == null ? string.Empty : $" at {reading.Ref}";

        // A task's diagnostics: those in its own files, and those of zones and layers it shares with another task, which
        // are reported on the file of whichever assignment comes first ("... of <task>/<assignment>").
        private static IEnumerable<CoordinatorDiagnostic> TaskDiagnostics(IEnumerable<CoordinatorDiagnostic> diagnostics, string taskId)
        {
            var folder = CoordinatorPaths.RepositoryTaskFolder(taskId) + "/";
            var mention = " " + taskId + "/";
            return diagnostics.Where(diagnostic => diagnostic.File.StartsWith(folder, StringComparison.Ordinal)
                                                   || diagnostic.Code is "zone_overlap" or "layer_shared" or "layers_connected"
                                                   && diagnostic.Message.Contains(mention));
        }

        // One card's diagnostics: its assignment, delivery and review files, and zones and layers it shares with other cards.
        private static IEnumerable<CoordinatorDiagnostic> CardDiagnostics(IEnumerable<CoordinatorDiagnostic> diagnostics, string taskId, string assignmentId)
        {
            var prefix = CoordinatorPaths.RepositoryTaskFolder(taskId) + "/" + assignmentId + ".";
            var mention = " " + taskId + "/" + assignmentId + ".";
            var mentionEnd = " " + taskId + "/" + assignmentId + ";";
            return diagnostics.Where(diagnostic => diagnostic.File.StartsWith(prefix, StringComparison.Ordinal)
                                                   || diagnostic.Code is "zone_overlap" or "layer_shared" or "layers_connected"
                                                   && (diagnostic.Message.Contains(mention) || diagnostic.Message.Contains(mentionEnd)));
        }

        private static CoordinatorAgentResult Scope(Reading reading, string taskId, string assignmentId, string checkout, string revision)
        {
            var workspace = reading.Workspace;
            var data = workspace.FindTask(taskId) ?? throw new CoordinatorAgentException("task_not_found", $"No task '{taskId}'{AtText(reading)}.");
            var assignment = data.Assignments.FirstOrDefault(candidate => candidate.id == assignmentId)
                             ?? throw new CoordinatorAgentException("assignment_not_found", $"Task '{taskId}' has no assignment '{assignmentId}'{AtText(reading)}.");
            var commands = CoordinatorScope.Commands(workspace.Project, data.Task, assignment, workspace.Architector);
            CoordinatorGit git;
            if (!string.IsNullOrEmpty(checkout))
            {
                if (!Directory.Exists(checkout))
                    throw new CoordinatorAgentException("checkout_not_found", $"Checkout '{checkout}' does not exist.");
                git = new CoordinatorGit(checkout);
                revision ??= "HEAD";
            }
            else
            {
                git = reading.Git;
                if (git != null && string.IsNullOrEmpty(revision))
                    revision = CoordinatorStates.Compute(data, git).Find(assignmentId)?.DeliveredRevision;
            }
            if (git == null || string.IsNullOrEmpty(revision))
                return CoordinatorAgentResult.Ok("Scope commands; each prints nothing when the commits are inside the zone. Nothing is delivered yet to check.",
                    new { at = reading.At, commands, uncommitted_command = CoordinatorScope.UncommittedCommand });

            var delivered = CoordinatorScope.Check(git, workspace.Project, data.Task, assignment, revision, workspace.Architector);
            var uncommitted = string.IsNullOrEmpty(checkout) ? null : CoordinatorScope.Uncommitted(git, workspace.Project, data.Task, assignment, workspace.Architector);
            var message = (delivered.Clean ? "The commits are inside the zone." : "The commits leave the zone.")
                          + (uncommitted == null ? "" : uncommitted.Clean ? " Nothing is uncommitted." : " The checkout has uncommitted changes; they are not part of the delivery.");
            return CoordinatorAgentResult.Ok(message, new
            {
                at = reading.At,
                clean = delivered.Clean,
                delivered = ScopeJson(delivered),
                uncommitted = uncommitted == null ? null : new { inside_zone = uncommitted.InsideZone, outside_zone = uncommitted.OutsideZone },
                commands,
                uncommitted_command = CoordinatorScope.UncommittedCommand
            });
        }

        private static object[] RequirementChanges(CoordinatorTaskStatus status, CoordinatorTaskRecord task) =>
            status == null || status.RequirementChanges.Count == 0
                ? null
                : status.RequirementChanges.Select(change => (object)new
                {
                    path = change.Path,
                    recorded = change.Recorded,
                    commits = change.Commits,
                    text = change.Text(task.targetBranch)
                }).ToArray();

        private static object ScopeJson(CoordinatorScopeReport report) => new
        {
            clean = report.Clean,
            revision = report.Revision,
            task_branch = report.TaskBranch,
            start = report.Start,
            start_is_base = report.StartIsBase,
            base_is_ancestor = report.BaseIsAncestor,
            problem = report.Problem,
            outside_zone = report.OutsideZone,
            reserved = report.Reserved
        };

        private static CoordinatorAgentResult CreateTask(string root, CoordinatorTaskRecord task)
        {
            var path = TaskFile(root, task.id);
            if (File.Exists(path))
                throw new CoordinatorAgentException("task_exists", $"Task '{task.id}' already exists; use update_task.");
            task.approval = null;
            task.cancelled = null;
            return Commit(root, $"Task '{task.id}' created as a draft. A person approves it in the Coordinator window.", Documents((path, task)));
        }

        private static CoordinatorAgentResult UpdateTask(string root, CoordinatorTaskRecord task)
        {
            var existing = LoadTask(root, task.id);
            task.approval = existing.Task.approval;
            task.cancelled = existing.Task.cancelled;
            if (task.approval != null)
            {
                var git = Git(root);
                foreach (var source in task.sources.Where(source => string.IsNullOrEmpty(source.revision)))
                {
                    source.revision = existing.Task.sources.FirstOrDefault(known => known.path == source.path)?.revision;
                    if (source.revision == null && git != null)
                        RecordSourceRevisions(git, new CoordinatorTaskRecord { targetBranch = task.targetBranch, sources = new List<CoordinatorDocumentRef> { source } });
                }
            }
            return Commit(root, $"Task '{task.id}' updated.", Documents((TaskFile(root, task.id), task)));
        }

        private static CoordinatorAgentResult SetDecision(string root, string taskId, CoordinatorDecisionRecord decision)
        {
            var data = LoadTask(root, taskId);
            var index = data.Task.decisions.FindIndex(candidate => candidate.id == decision.id);
            if (index >= 0)
                data.Task.decisions[index] = decision;
            else
                data.Task.decisions.Add(decision);
            return Commit(root, $"Decision '{decision.id}' {(index >= 0 ? "updated" : "added")}.", Documents((TaskFile(root, taskId), data.Task)));
        }

        private static CoordinatorAgentResult SetAssignment(string root, string taskId, CoordinatorAssignmentRecord assignment)
        {
            var data = LoadTask(root, taskId);
            var existed = data.Assignments.Any(candidate => candidate.id == assignment.id);
            return Commit(root, $"Assignment '{assignment.id}' {(existed ? "updated" : "added")}.",
                Documents((AssignmentFile(root, taskId, assignment.id), assignment)));
        }

        private static CoordinatorAgentResult RemoveAssignment(string root, string taskId, string assignmentId)
        {
            var data = LoadTask(root, taskId);
            if (data.Assignments.All(candidate => candidate.id != assignmentId))
                throw new CoordinatorAgentException("assignment_not_found", $"Task '{taskId}' has no assignment '{assignmentId}'.");
            if (data.Task.approval != null)
                throw new CoordinatorAgentException("task_approved", "The task is approved; cancel the assignment instead of removing it.");
            return Commit(root, $"Assignment '{assignmentId}' removed.", new Dictionary<string, object>(), new[] { AssignmentFile(root, taskId, assignmentId) });
        }

        private static CoordinatorAgentResult CancelAssignment(string root, string taskId, string assignmentId, string by, string reason)
        {
            var data = LoadTask(root, taskId);
            var assignment = data.Assignments.FirstOrDefault(candidate => candidate.id == assignmentId)
                             ?? throw new CoordinatorAgentException("assignment_not_found", $"Task '{taskId}' has no assignment '{assignmentId}'.");
            assignment.cancelled = new CoordinatorCancellationRecord { by = by, reason = reason, date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            return Commit(root, $"Assignment '{assignmentId}' cancelled.", Documents((AssignmentFile(root, taskId, assignmentId), assignment)));
        }

        private static CoordinatorAgentResult RecordReview(string root, string taskId, string assignmentId, CoordinatorReviewRoundRecord round)
        {
            var data = LoadTask(root, taskId);
            if (data.Assignments.All(candidate => candidate.id != assignmentId))
                throw new CoordinatorAgentException("assignment_not_found", $"Task '{taskId}' has no assignment '{assignmentId}'.");
            if (round.verdict == CoordinatorVerdicts.Accepted && (string.IsNullOrWhiteSpace(round.acceptedBy) || string.IsNullOrWhiteSpace(round.instructedBy)))
                throw new CoordinatorAgentException("needs_person", "Acceptance needs acceptedBy and instructedBy: the person who decided to accept. Ask the user first.");
            if (round.verdict == CoordinatorVerdicts.Accepted)
                RecordScope(root, data, assignmentId, round);
            if (string.IsNullOrEmpty(round.date))
                round.date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var review = data.Reviews.TryGetValue(assignmentId, out var existing) ? existing : new CoordinatorReviewRecord { assignment = assignmentId };
            review.rounds.Add(round);
            return Commit(root, $"Review round for '{assignmentId}' recorded: {round.verdict}.",
                Documents((Path.Combine(new CoordinatorPaths(root).TaskFolder(taskId), assignmentId + CoordinatorPaths.ReviewSuffix), review)));
        }

        private static void RecordScope(string root, CoordinatorTaskData data, string assignmentId, CoordinatorReviewRoundRecord round)
        {
            var git = Git(root) ?? throw new CoordinatorAgentException("scope_unavailable", "Acceptance runs the scope check in git, and this project is not a git repository.");
            if (string.IsNullOrWhiteSpace(round.revision))
                throw new CoordinatorAgentException("invalid_parameters", "'round.revision' is required: the delivered revision the lead accepts.");
            var workspace = CoordinatorWorkspace.Load(root);
            var assignment = data.Assignments.First(candidate => candidate.id == assignmentId);
            CoordinatorScopeReport report;
            try
            {
                report = CoordinatorScope.Check(git, workspace.Project, data.Task, assignment, round.revision, workspace.Architector);
            }
            catch (CoordinatorGitException exception)
            {
                throw new CoordinatorAgentException("git_failed", exception.Message);
            }
            if (!report.Clean)
            {
                var reasons = new List<string>();
                if (report.Problem != null)
                    reasons.Add(report.Problem);
                if (report.Problem == null && !report.BaseIsAncestor)
                    reasons.Add(report.StartIsBase
                        ? $"The task base is not an ancestor of {Short(report.Revision)}."
                        : $"The task branch '{report.TaskBranch}' does not contain the task base.");
                if (report.OutsideZone.Count > 0)
                    reasons.Add("Outside the zone: " + string.Join(", ", report.OutsideZone) + ".");
                if (report.Reserved.Count > 0)
                    reasons.Add("Reserved: " + string.Join(", ", report.Reserved) + ".");
                throw new CoordinatorAgentException("scope_failed",
                    "Acceptance refused: the delivered commits leave the zone. " + string.Join(" ", reasons)
                    + " Return the card, or widen the zone with set_assignment first.");
            }
            var checkId = workspace.Project.checks.FirstOrDefault(check => check.generated == "scope")?.id ?? "scope";
            round.checks ??= new List<CoordinatorCheckResultRecord>();
            round.checks.RemoveAll(check => check.check == checkId);
            round.checks.Add(new CoordinatorCheckResultRecord
            {
                check = checkId,
                command = $"git diff --no-renames --name-only {report.Start} {report.Revision}",
                exitCode = 0,
                result = $"Inside the zone, no reserved path. Start {Short(report.Start)} "
                         + (report.StartIsBase ? "(the task base; the task branch does not exist)" : $"(merge base of {report.TaskBranch} and the revision)")
                         + $", revision {Short(report.Revision)}. Run by Coordinator at acceptance."
            });
        }

        private static string Short(string commit) => commit == null ? "?" : commit.Length > 8 ? commit.Substring(0, 8) : commit;

        private static CoordinatorAgentResult Commit(string root, string message, IReadOnlyDictionary<string, object> documents, IEnumerable<string> deletions = null)
        {
            var removed = (deletions ?? Enumerable.Empty<string>()).ToArray();
            var before = ErrorKeys(Validated(Git(root), CoordinatorWorkspace.Load(root)));
            var backups = documents.Keys.Concat(removed).Distinct().ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllText(path) : null);
            foreach (var document in documents)
                CoordinatorJson.Write(document.Key, document.Value);
            foreach (var path in removed.Where(File.Exists))
                File.Delete(path);

            var after = Validated(Git(root), CoordinatorWorkspace.Load(root));
            var introduced = after.Where(diagnostic => diagnostic.IsError && !before.Contains(Key(diagnostic))).ToArray();
            if (introduced.Length == 0)
            {
#if UNITY_EDITOR
                CoordinatorHistory.Record(message, backups);
#else
                CoordinatorHistoryInbox.Write(root, message, backups);
#endif
                return CoordinatorAgentResult.Ok(message, new { warnings = Diagnostics(after.Where(diagnostic => !diagnostic.IsError)) });
            }

            foreach (var backup in backups)
            {
                if (backup.Value == null)
                {
                    if (File.Exists(backup.Key))
                        File.Delete(backup.Key);
                }
                else
                    File.WriteAllText(backup.Key, backup.Value);
            }
            var folder = Path.GetDirectoryName(documents.Keys.FirstOrDefault() ?? string.Empty);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
            return CoordinatorAgentResult.Fail("validation_failed", "The change was rolled back: it adds validation errors.", new { errors = Diagnostics(introduced) });
        }

        private static Dictionary<string, object> Documents(params (string path, object document)[] documents) =>
            documents.ToDictionary(item => item.path, item => item.document);

        private static CoordinatorTaskData LoadTask(string root, string taskId)
        {
            return CoordinatorWorkspace.Load(root).FindTask(taskId) ?? throw new CoordinatorAgentException("task_not_found", $"No task '{taskId}'.");
        }

        private static string TaskFile(string root, string taskId) => Path.Combine(new CoordinatorPaths(root).TaskFolder(Safe(taskId)), CoordinatorPaths.TaskFile);

        private static string AssignmentFile(string root, string taskId, string assignmentId) =>
            Path.Combine(new CoordinatorPaths(root).TaskFolder(Safe(taskId)), Safe(assignmentId) + CoordinatorPaths.AssignmentSuffix);

        private static string Safe(string id)
        {
            if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..") || id.Contains('/'))
                throw new CoordinatorAgentException("invalid_id", $"'{id}' is not a valid id: use lowercase kebab-case.");
            return id;
        }

        private static IReadOnlyList<CoordinatorDiagnostic> Validated(CoordinatorGit git, CoordinatorWorkspace workspace)
        {
            return git == null ? CoordinatorValidation.Validate(workspace) : CoordinatorValidation.Validate(workspace, git);
        }

        private static Dictionary<string, CoordinatorTaskStatus> Statuses(CoordinatorGit git, CoordinatorWorkspace workspace)
        {
            var statuses = new Dictionary<string, CoordinatorTaskStatus>(StringComparer.Ordinal);
            if (git != null)
                foreach (var data in workspace.Tasks)
                    statuses[data.Task.id] = CoordinatorStates.Compute(data, git);
            return statuses;
        }

        private static CoordinatorGit Git(string root) =>
            Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")) ? new CoordinatorGit(root) : null;

        private static HashSet<string> ErrorKeys(IEnumerable<CoordinatorDiagnostic> diagnostics) =>
            diagnostics.Where(diagnostic => diagnostic.IsError).Select(Key).ToHashSet(StringComparer.Ordinal);

        private static string Key(CoordinatorDiagnostic diagnostic) => $"{diagnostic.Code}|{diagnostic.File}|{diagnostic.Subject}|{diagnostic.Message}";

        private static object[] Diagnostics(IEnumerable<CoordinatorDiagnostic> diagnostics) =>
            diagnostics.Select(diagnostic => (object)new
            {
                severity = diagnostic.Severity,
                code = diagnostic.Code,
                file = diagnostic.File,
                subject = diagnostic.Subject,
                message = diagnostic.Message
            }).ToArray();

        private static JToken Json(object record) => JToken.Parse(CoordinatorJson.Serialize(record));

        private static string Required(JObject parameters, string name)
        {
            var value = (string)parameters?[name];
            return string.IsNullOrWhiteSpace(value) ? throw new CoordinatorAgentException("invalid_parameters", $"'{name}' is required.") : value;
        }

        private static T Record<T>(JObject parameters, string name) where T : class
        {
            if (parameters?[name] is not JObject value)
                throw new CoordinatorAgentException("invalid_parameters", $"'{name}' must be a JSON object.");
            try
            {
                return CoordinatorJson.Parse<T>(value.ToString(Formatting.None));
            }
            catch (JsonException exception)
            {
                throw new CoordinatorAgentException("invalid_parameters", $"'{name}': {exception.Message}");
            }
        }
    }

    public sealed class CoordinatorAgentException : Exception
    {
        public CoordinatorAgentException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
