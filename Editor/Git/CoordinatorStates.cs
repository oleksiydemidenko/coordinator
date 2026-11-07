using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Coordinator.Editor
{
    public static class CoordinatorAssignmentStates
    {
        public const string Draft = "Draft";
        public const string Blocked = "Blocked";
        public const string Ready = "Ready";
        public const string InProgress = "In Progress";
        public const string Delivered = "Delivered";
        public const string Returned = "Returned";
        public const string Accepted = "Accepted";
        public const string Merged = "Merged";
        public const string Closed = "Closed";
        public const string Cancelled = "Cancelled";
    }

    public static class CoordinatorTaskStates
    {
        public const string Draft = "Draft";
        public const string Approved = "Approved";
        public const string InWork = "In Work";
        public const string Integrated = "Integrated";
        public const string Closed = "Closed";
        public const string Cancelled = "Cancelled";
    }

    public sealed class CoordinatorAssignmentStatus
    {
        public string AssignmentId { get; internal set; } = string.Empty;
        public string State { get; internal set; } = string.Empty;
        public string DeliveredRevision { get; internal set; }
        public string AcceptedRevision { get; internal set; }
        public CoordinatorDeliveryRecord Delivery { get; internal set; }
        public List<string> BlockedBy { get; } = new();
        public string Problem { get; internal set; }
    }

    public sealed class CoordinatorTaskStatus
    {
        public string TaskId { get; internal set; } = string.Empty;
        public string State { get; internal set; } = string.Empty;
        public List<CoordinatorAssignmentStatus> Assignments { get; } = new();

        public CoordinatorAssignmentStatus Find(string assignmentId) =>
            Assignments.FirstOrDefault(status => status.AssignmentId == assignmentId);
    }

    public static class CoordinatorStates
    {
        public static CoordinatorTaskStatus Compute(CoordinatorTaskData data, CoordinatorGit git)
        {
            var task = data.Task;
            var status = new CoordinatorTaskStatus { TaskId = task.id };
            var byId = new Dictionary<string, CoordinatorAssignmentStatus>(StringComparer.Ordinal);
            foreach (var assignment in data.Assignments)
            {
                var assignmentStatus = Delivery(data, assignment, git);
                byId[assignment.id] = assignmentStatus;
                status.Assignments.Add(assignmentStatus);
            }
            foreach (var assignment in data.Assignments)
                Readiness(data, assignment, byId);

            var active = status.Assignments.Where(item => item.State != CoordinatorAssignmentStates.Cancelled).ToArray();
            status.State = task.cancelled != null ? CoordinatorTaskStates.Cancelled
                : task.approval == null ? CoordinatorTaskStates.Draft
                : active.Length > 0 && active.All(item => item.State == CoordinatorAssignmentStates.Closed)
                    ? CoordinatorTaskStates.Closed
                : active.Length > 0 && active.All(item => item.AcceptedRevision != null
                                                          && git.IsAncestor(item.AcceptedRevision, task.integrationBranch ?? task.targetBranch))
                    ? CoordinatorTaskStates.Integrated
                : active.Any(item => item.State != CoordinatorAssignmentStates.Ready && item.State != CoordinatorAssignmentStates.Blocked)
                    ? CoordinatorTaskStates.InWork
                : CoordinatorTaskStates.Approved;
            return status;
        }

        private static CoordinatorAssignmentStatus Delivery(CoordinatorTaskData data, CoordinatorAssignmentRecord assignment, CoordinatorGit git)
        {
            var status = new CoordinatorAssignmentStatus { AssignmentId = assignment.id };
            if (data.Task.cancelled != null || assignment.cancelled != null)
            {
                status.State = CoordinatorAssignmentStates.Cancelled;
                return status;
            }
            if (data.Task.approval == null)
            {
                status.State = CoordinatorAssignmentStates.Draft;
                return status;
            }

            var path = CoordinatorPaths.RepositoryDeliveryPath(data.Task.id, assignment.id);
            var branch = assignment.executor?.branch;
            CoordinatorDeliveryRecord latest = null;
            foreach (var commit in git.CommitsTouching(branch, path))
            {
                var delivery = Parse(git.Show(commit, path), out var problem);
                if (delivery == null)
                {
                    status.Problem ??= problem;
                    continue;
                }
                latest ??= delivery;
                if (delivery.state == CoordinatorDeliveryStates.Delivered)
                {
                    status.DeliveredRevision = commit;
                    break;
                }
            }
            status.Delivery = latest;
            if (latest == null)
                return status;

            var round = status.DeliveredRevision == null ? null : data.Reviews.TryGetValue(assignment.id, out var review)
                ? review.rounds.LastOrDefault(candidate => git.Resolve(candidate.revision) == status.DeliveredRevision)
                : null;
            if (latest.state != CoordinatorDeliveryStates.Delivered)
                status.State = CoordinatorAssignmentStates.InProgress;
            else if (round == null)
                status.State = CoordinatorAssignmentStates.Delivered;
            else if (round.verdict == CoordinatorVerdicts.Returned)
                status.State = CoordinatorAssignmentStates.Returned;
            else if (round.verdict == CoordinatorVerdicts.Accepted)
            {
                status.AcceptedRevision = status.DeliveredRevision;
                status.State = git.IsAncestor(status.AcceptedRevision, data.Task.targetBranch)
                    ? CoordinatorAssignmentStates.Closed
                    : !string.IsNullOrEmpty(data.Task.integrationBranch) && git.IsAncestor(status.AcceptedRevision, data.Task.integrationBranch)
                        ? CoordinatorAssignmentStates.Merged
                        : CoordinatorAssignmentStates.Accepted;
            }
            else
                status.State = CoordinatorAssignmentStates.Delivered;
            return status;
        }

        private static void Readiness(
            CoordinatorTaskData data,
            CoordinatorAssignmentRecord assignment,
            IReadOnlyDictionary<string, CoordinatorAssignmentStatus> statuses)
        {
            var status = statuses[assignment.id];
            if (!string.IsNullOrEmpty(status.State))
                return;
            var decisions = data.Task.decisions.ToDictionary(decision => decision.id, StringComparer.Ordinal);
            var decisionIds = assignment.dependsOn.decisions
                .Concat(assignment.inputs.Where(input => input.kind == CoordinatorInputKinds.Decision).Select(input => input.decision))
                .Where(id => id != null)
                .Distinct();
            foreach (var id in decisionIds)
                if (!decisions.TryGetValue(id, out var decision) || decision.state != CoordinatorDecisionStates.Decided)
                    status.BlockedBy.Add($"decision {id} is open");
            var outputs = assignment.dependsOn.outputs.Select(output => (output.assignment, output.output))
                .Concat(assignment.inputs.Where(input => input.kind == CoordinatorInputKinds.Output).Select(input => (input.assignment, input.output)))
                .Distinct();
            foreach (var (producer, output) in outputs)
            {
                var state = statuses.TryGetValue(producer ?? string.Empty, out var producerStatus) ? producerStatus.State : null;
                if (state != CoordinatorAssignmentStates.Merged && state != CoordinatorAssignmentStates.Closed)
                    status.BlockedBy.Add(state == CoordinatorAssignmentStates.Accepted
                        ? $"output {producer}/{output} is accepted but not merged into the task branch"
                        : $"output {producer}/{output} is not accepted");
            }
            foreach (var resource in assignment.inputs.Where(input => input.kind == CoordinatorInputKinds.Resource && input.supplied != true))
                status.BlockedBy.Add($"resource not supplied: {resource.text}");
            status.State = status.BlockedBy.Count > 0 ? CoordinatorAssignmentStates.Blocked : CoordinatorAssignmentStates.Ready;
        }

        private static CoordinatorDeliveryRecord Parse(string json, out string problem)
        {
            problem = null;
            if (json == null)
                return null;
            try
            {
                return CoordinatorJson.Parse<CoordinatorDeliveryRecord>(json);
            }
            catch (JsonException exception)
            {
                problem = "Delivery file is invalid: " + exception.Message;
                return null;
            }
        }
    }
}
