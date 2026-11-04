using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Coordinator.Editor
{
    public static class CoordinatorValidation
    {
        public const int SchemaVersion = 1;
        private static readonly Regex IdFormat = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
        private static readonly Regex VariableUse = new(@"\$\{([^}]*)\}", RegexOptions.Compiled);
        private static readonly string[] OutputKinds = { "code", "asset", "configuration", "document" };
        private static readonly string[] LayerRoles = { "location", "reads", "zone" };

        public static IReadOnlyList<CoordinatorDiagnostic> Validate(CoordinatorWorkspace workspace, Func<string, bool> pathExists = null)
        {
            var context = new Context(workspace, pathExists);
            context.Diagnostics.AddRange(workspace.LoadDiagnostics);
            context.ValidateProject();
            foreach (var task in workspace.Tasks)
                context.ValidateTask(task);
            context.ValidateZonesAcrossAssignments();
            return context.Diagnostics;
        }

        private sealed class Context
        {
            private readonly CoordinatorWorkspace workspace;
            private readonly Func<string, bool> pathExists;
            private readonly HashSet<string> checkIds;
            private readonly HashSet<string> disciplineIds;
            private readonly HashSet<string> variableIds;

            public Context(CoordinatorWorkspace workspace, Func<string, bool> pathExists)
            {
                this.workspace = workspace;
                this.pathExists = pathExists;
                checkIds = workspace.Project.checks.Select(check => check.id).ToHashSet(StringComparer.Ordinal);
                disciplineIds = workspace.Project.disciplines.Select(discipline => discipline.id).ToHashSet(StringComparer.Ordinal);
                variableIds = workspace.Project.variables.Select(variable => variable.id).ToHashSet(StringComparer.Ordinal);
            }

            public List<CoordinatorDiagnostic> Diagnostics { get; } = new();

            public void ValidateProject()
            {
                var file = CoordinatorPaths.RootFolder + "/" + CoordinatorPaths.ProjectFile;
                var project = workspace.Project;
                Schema(file, "project", project.schemaVersion);
                Ids(file, "discipline", project.disciplines.Select(discipline => discipline.id));
                Ids(file, "variable", project.variables.Select(variable => variable.id));
                Ids(file, "check", project.checks.Select(check => check.id));
                Ids(file, "rule", project.rules.Select(rule => rule.id));
                Patterns(file, "reserved path", project.reservedPaths.Select(reserved => reserved.pattern));
                foreach (var check in project.checks)
                {
                    RunBy(file, check.id, check.runBy);
                    if (string.IsNullOrEmpty(check.command) == (check.generated != "scope"))
                        Error("check_command", file, check.id, "A check has either a command or generated: \"scope\".");
                    Variables(file, check.id, check.command);
                }
            }

            public void ValidateTask(CoordinatorTaskData data)
            {
                var task = data.Task;
                var file = data.FilePath(CoordinatorPaths.TaskFile);
                Schema(file, task.id, task.schemaVersion);
                Id(file, "task", task.id);
                Required(file, task.id, "lead", task.lead);
                Required(file, task.id, "targetBranch", task.targetBranch);
                Required(file, task.id, "base.branch", task.baseRevision.branch);
                Patterns(file, "reserved path", task.reservedPaths.Select(reserved => reserved.pattern));
                Ids(file, "decision", task.decisions.Select(decision => decision.id));
                Ids(file, "assignment", data.Assignments.Select(assignment => assignment.id));

                var decisions = task.decisions.ToDictionary(decision => decision.id, StringComparer.Ordinal);
                foreach (var decision in task.decisions)
                {
                    if (decision.state != CoordinatorDecisionStates.Open && decision.state != CoordinatorDecisionStates.Decided)
                        Error("decision_state", file, decision.id, $"Decision state '{decision.state}' is not open or decided.");
                    if (decision.state == CoordinatorDecisionStates.Decided && string.IsNullOrWhiteSpace(decision.answer))
                        Error("decision_without_answer", file, decision.id, "A decided decision records its short answer, so cards stay self-sufficient.");
                    if (decision.record == null || string.IsNullOrEmpty(decision.record.path) && string.IsNullOrEmpty(decision.record.layer))
                        Warning("decision_without_record", file, decision.id, "The decision does not link to where it is recorded in full.");
                }

                var outputs = data.Assignments
                    .SelectMany(assignment => assignment.outputs.Select(output => (assignment.id, output.id)))
                    .ToHashSet();
                if (task.integration != null)
                {
                    Dependencies(file, "integration", task.integration.dependsOn, decisions, outputs);
                    Acceptance(file, "integration", task.integration.acceptance);
                    Route(file, "integration", task.integration.route, null);
                }

                var reserved = CoordinatorScope.Reserved(workspace.Project, task);
                foreach (var assignment in data.Assignments)
                    ValidateAssignment(data, assignment, decisions, outputs, reserved);
                DependencyCycles(data);
                foreach (var delivery in data.Deliveries.Values)
                    ValidateDelivery(data, delivery);
                foreach (var review in data.Reviews.Values)
                    ValidateReview(data, review);
            }

            private void ValidateAssignment(
                CoordinatorTaskData data,
                CoordinatorAssignmentRecord assignment,
                Dictionary<string, CoordinatorDecisionRecord> decisions,
                HashSet<(string, string)> outputs,
                IReadOnlyList<string> reserved)
            {
                var file = data.FilePath(assignment.id + CoordinatorPaths.AssignmentSuffix);
                var subject = assignment.id;
                Schema(file, subject, assignment.schemaVersion);
                Required(file, subject, "goal", assignment.goal);
                Required(file, subject, "executor.branch", assignment.executor?.branch);
                foreach (var discipline in assignment.disciplines.Where(discipline => !disciplineIds.Contains(discipline)))
                    Error("unknown_discipline", file, subject, $"Discipline '{discipline}' is not declared in the project file.");
                if (assignment.disciplines.Count == 0)
                    Error("missing_discipline", file, subject, "An assignment names at least one discipline.");
                foreach (var layer in assignment.layers.Where(layer => !LayerRoles.Contains(layer.role)))
                    Error("layer_role", file, subject, $"Layer '{layer.path}' has role '{layer.role}'; expected location, reads or zone.");

                Ids(file, "output", assignment.outputs.Select(output => output.id));
                Ids(file, "acceptance", assignment.acceptance.Select(acceptance => acceptance.id));
                Patterns(file, "write zone", assignment.writeZone);
                if (assignment.writeZone.Count == 0 && assignment.layers.All(layer => layer.role != "zone"))
                    Error("empty_write_zone", file, subject, "An assignment has a write zone.");
                foreach (var pattern in assignment.writeZone)
                {
                    var covering = reserved.FirstOrDefault(reservedPattern => CoordinatorGlob.Covers(reservedPattern, pattern));
                    if (covering != null)
                        Error("zone_reserved", file, subject, $"Write zone '{pattern}' lies inside reserved path '{covering}'.");
                }

                foreach (var output in assignment.outputs)
                {
                    if (!OutputKinds.Contains(output.kind))
                        Error("output_kind", file, output.id, $"Output kind '{output.kind}' is not code, asset, configuration or document.");
                    if (output.paths.Count == 0)
                        Error("output_without_paths", file, output.id, "An output names its paths.");
                    foreach (var path in output.paths.Where(path => !assignment.writeZone.Any(zone => CoordinatorGlob.Covers(zone, path))))
                        Error("output_outside_zone", file, output.id, $"Output path '{path}' is not inside the write zone.");
                }

                foreach (var input in assignment.inputs)
                    Input(file, subject, input, decisions, outputs, data);
                Dependencies(file, subject, assignment.dependsOn, decisions, outputs);
                if (assignment.dependsOn.outputs.Any(output => output.assignment == assignment.id))
                    Error("self_dependency", file, subject, "An assignment does not depend on its own output.");

                var acceptanceIds = assignment.acceptance.Select(acceptance => acceptance.id).ToHashSet(StringComparer.Ordinal);
                Route(file, subject, assignment.route, acceptanceIds);
                Acceptance(file, subject, assignment.acceptance);
                if (assignment.acceptance.Count == 0)
                    Error("missing_acceptance", file, subject, "An assignment states how it is accepted.");
            }

            private void Input(
                string file,
                string subject,
                CoordinatorInputRecord input,
                Dictionary<string, CoordinatorDecisionRecord> decisions,
                HashSet<(string, string)> outputs,
                CoordinatorTaskData data)
            {
                switch (input.kind)
                {
                    case CoordinatorInputKinds.Source:
                        if (string.IsNullOrEmpty(input.path))
                            Error("input_missing", file, subject, "A source input names its path.");
                        else if (pathExists != null && !pathExists(input.path))
                            Error("input_missing", file, subject, $"Source input '{input.path}' does not exist.");
                        break;
                    case CoordinatorInputKinds.Output:
                        if (!outputs.Contains((input.assignment, input.output)))
                            Error("unknown_output", file, subject, $"Input output '{input.assignment}/{input.output}' is not declared in task '{data.Task.id}'.");
                        break;
                    case CoordinatorInputKinds.Decision:
                        if (input.decision == null || !decisions.ContainsKey(input.decision))
                            Error("unknown_decision", file, subject, $"Input decision '{input.decision}' is not declared in task '{data.Task.id}'.");
                        break;
                    case CoordinatorInputKinds.Resource:
                        if (string.IsNullOrWhiteSpace(input.text))
                            Error("input_missing", file, subject, "A resource input describes the resource.");
                        break;
                    default:
                        Error("input_kind", file, subject, $"Input kind '{input.kind}' is not source, output, decision or resource.");
                        break;
                }
            }

            private void Dependencies(
                string file,
                string subject,
                CoordinatorDependenciesRecord dependencies,
                Dictionary<string, CoordinatorDecisionRecord> decisions,
                HashSet<(string, string)> outputs)
            {
                if (dependencies == null)
                    return;
                foreach (var decision in dependencies.decisions.Where(decision => !decisions.ContainsKey(decision)))
                    Error("unknown_decision", file, subject, $"Dependency on decision '{decision}', which the task does not declare.");
                foreach (var output in dependencies.outputs.Where(output => !outputs.Contains((output.assignment, output.output))))
                    Error("unknown_output", file, subject, $"Dependency on output '{output.assignment}/{output.output}', which no assignment declares.");
            }

            private void Route(string file, string subject, IEnumerable<CoordinatorRouteStepRecord> route, HashSet<string> acceptanceIds)
            {
                foreach (var step in route)
                {
                    var references = (step.checks ?? new List<string>()).Concat(step.check == null ? Array.Empty<string>() : new[] { step.check });
                    foreach (var reference in references.Where(reference => !checkIds.Contains(reference) && acceptanceIds?.Contains(reference) != true))
                        Error("unknown_check", file, subject, $"Route step '{step.text}' names check '{reference}', which is neither a project check nor an acceptance item.");
                }
            }

            private void Acceptance(string file, string subject, IEnumerable<CoordinatorAcceptanceRecord> acceptance)
            {
                foreach (var item in acceptance)
                {
                    RunBy(file, $"{subject}/{item.id}", item.runBy);
                    var references = (item.checks ?? new List<string>()).Concat(item.check == null ? Array.Empty<string>() : new[] { item.check });
                    foreach (var reference in references.Where(reference => !checkIds.Contains(reference)))
                        Error("unknown_check", file, $"{subject}/{item.id}", $"Acceptance names check '{reference}', which the project file does not declare.");
                    Variables(file, $"{subject}/{item.id}", item.command);
                }
            }

            private void ValidateDelivery(CoordinatorTaskData data, CoordinatorDeliveryRecord delivery)
            {
                var file = data.FilePath(delivery.assignment + CoordinatorPaths.DeliverySuffix);
                Schema(file, delivery.assignment, delivery.schemaVersion);
                var assignment = data.Assignments.FirstOrDefault(candidate => candidate.id == delivery.assignment);
                if (assignment == null)
                {
                    Error("unknown_assignment", file, delivery.assignment, $"Task '{data.Task.id}' has no assignment '{delivery.assignment}'.");
                    return;
                }
                if (delivery.state != CoordinatorDeliveryStates.InProgress && delivery.state != CoordinatorDeliveryStates.Delivered)
                    Error("delivery_state", file, delivery.assignment, $"Delivery state '{delivery.state}' is not inProgress or delivered.");
                var acceptanceIds = assignment.acceptance.Select(acceptance => acceptance.id).ToHashSet(StringComparer.Ordinal);
                foreach (var check in delivery.checks)
                {
                    if (check.acceptance != null && !acceptanceIds.Contains(check.acceptance))
                        Error("unknown_acceptance", file, delivery.assignment, $"Check result for acceptance '{check.acceptance}', which the assignment does not declare.");
                    if (check.check != null && !checkIds.Contains(check.check))
                        Error("unknown_check", file, delivery.assignment, $"Check result for '{check.check}', which the project file does not declare.");
                    if (check.acceptance == null && check.check == null)
                        Error("check_without_reference", file, delivery.assignment, "A check result names its acceptance item or project check.");
                }
                foreach (var unverified in delivery.unverified.Where(item => item.acceptance != null && !acceptanceIds.Contains(item.acceptance)))
                    Error("unknown_acceptance", file, delivery.assignment, $"Unverified item for acceptance '{unverified.acceptance}', which the assignment does not declare.");
            }

            private void ValidateReview(CoordinatorTaskData data, CoordinatorReviewRecord review)
            {
                var file = data.FilePath(review.assignment + CoordinatorPaths.ReviewSuffix);
                Schema(file, review.assignment, review.schemaVersion);
                if (data.Assignments.All(candidate => candidate.id != review.assignment))
                    Error("unknown_assignment", file, review.assignment, $"Task '{data.Task.id}' has no assignment '{review.assignment}'.");
                foreach (var round in review.rounds)
                {
                    if (round.verdict != CoordinatorVerdicts.Returned && round.verdict != CoordinatorVerdicts.Accepted)
                        Error("review_verdict", file, review.assignment, $"Verdict '{round.verdict}' is not returned or accepted.");
                    if (string.IsNullOrEmpty(round.revision))
                        Error("review_revision", file, review.assignment, "A review round names the revision it reviewed.");
                    if (round.verdict == CoordinatorVerdicts.Accepted && string.IsNullOrEmpty(round.acceptedBy))
                        Error("acceptance_without_person", file, review.assignment, "An accepted round names who accepted it.");
                }
            }

            private void DependencyCycles(CoordinatorTaskData data)
            {
                var edges = data.Assignments.ToDictionary(
                    assignment => assignment.id,
                    assignment => assignment.dependsOn.outputs.Select(output => output.assignment)
                        .Concat(assignment.inputs.Where(input => input.kind == CoordinatorInputKinds.Output).Select(input => input.assignment))
                        .Where(id => id != null && id != assignment.id)
                        .Distinct()
                        .ToArray(),
                    StringComparer.Ordinal);
                var visiting = new List<string>();
                var done = new HashSet<string>(StringComparer.Ordinal);
                var reported = new HashSet<string>(StringComparer.Ordinal);

                void Visit(string id)
                {
                    if (done.Contains(id) || !edges.ContainsKey(id))
                        return;
                    var index = visiting.IndexOf(id);
                    if (index >= 0)
                    {
                        var cycle = visiting.Skip(index).Append(id).ToArray();
                        if (reported.Add(string.Join(">", cycle.Skip(1).OrderBy(value => value, StringComparer.Ordinal))))
                            Error("dependency_cycle", data.FilePath(CoordinatorPaths.TaskFile), id, "Assignments wait for each other: " + string.Join(" -> ", cycle) + ".");
                        return;
                    }
                    visiting.Add(id);
                    foreach (var next in edges[id])
                        Visit(next);
                    visiting.RemoveAt(visiting.Count - 1);
                    done.Add(id);
                }

                foreach (var id in edges.Keys.OrderBy(value => value, StringComparer.Ordinal))
                    Visit(id);
            }

            public void ValidateZonesAcrossAssignments()
            {
                var open = workspace.Tasks
                    .Where(data => data.Task.cancelled == null)
                    .SelectMany(data => data.Assignments.Where(assignment => assignment.cancelled == null)
                        .Select(assignment => (data, assignment)))
                    .ToArray();
                for (var i = 0; i < open.Length; i++)
                for (var j = i + 1; j < open.Length; j++)
                {
                    var left = open[i];
                    var right = open[j];
                    foreach (var leftPattern in left.assignment.writeZone)
                    foreach (var rightPattern in right.assignment.writeZone.Where(rightPattern => CoordinatorGlob.Overlaps(leftPattern, rightPattern)))
                        Error("zone_overlap", left.data.FilePath(left.assignment.id + CoordinatorPaths.AssignmentSuffix), left.assignment.id,
                            $"Write zone '{leftPattern}' overlaps '{rightPattern}' of {right.data.Task.id}/{right.assignment.id}.");
                }
            }

            private void Schema(string file, string subject, int version)
            {
                if (version != SchemaVersion)
                    Error("schema_version", file, subject, $"schemaVersion {version}; this Coordinator reads version {SchemaVersion}.");
            }

            private void Id(string file, string kind, string id)
            {
                if (string.IsNullOrEmpty(id) || !IdFormat.IsMatch(id))
                    Error("id_format", file, id, $"{kind} id '{id}' is not lowercase kebab-case.");
            }

            private void Ids(string file, string kind, IEnumerable<string> ids)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    Id(file, kind, id);
                    if (!string.IsNullOrEmpty(id) && !seen.Add(id))
                        Error("duplicate_id", file, id, $"{kind} id '{id}' is used twice.");
                }
            }

            private void Patterns(string file, string kind, IEnumerable<string> patterns)
            {
                foreach (var pattern in patterns)
                {
                    var problem = CoordinatorGlob.Problem(pattern);
                    if (problem.Length > 0)
                        Error("invalid_pattern", file, pattern, $"{kind} '{pattern}': {problem}");
                }
            }

            private void Required(string file, string subject, string field, string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    Error("missing_field", file, subject, $"'{field}' is required.");
            }

            private void RunBy(string file, string subject, string runBy)
            {
                if (runBy != CoordinatorRunBy.Executor && runBy != CoordinatorRunBy.Lead)
                    Error("run_by", file, subject, $"runBy '{runBy}' is not executor or lead.");
            }

            private void Variables(string file, string subject, string command)
            {
                if (string.IsNullOrEmpty(command))
                    return;
                foreach (Match match in VariableUse.Matches(command))
                    if (!variableIds.Contains(match.Groups[1].Value))
                        Error("unknown_variable", file, subject, $"Command uses ${{{match.Groups[1].Value}}}, which the project file does not declare.");
            }

            private void Error(string code, string file, string subject, string message) =>
                Diagnostics.Add(new CoordinatorDiagnostic(code, CoordinatorSeverities.Error, file, subject, message));

            private void Warning(string code, string file, string subject, string message) =>
                Diagnostics.Add(new CoordinatorDiagnostic(code, CoordinatorSeverities.Warning, file, subject, message));
        }
    }
}
