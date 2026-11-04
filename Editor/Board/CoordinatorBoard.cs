using System;
using System.Collections.Generic;
using System.Linq;

namespace Coordinator.Editor
{
    public sealed class CoordinatorBoardCard
    {
        public CoordinatorTaskData Task { get; internal set; }
        public CoordinatorAssignmentRecord Assignment { get; internal set; }
        public CoordinatorAssignmentStatus Status { get; internal set; }
        public string State => Status?.State ?? string.Empty;
        public string Key => Task.Task.id + "/" + Assignment.id;
    }

    public sealed class CoordinatorBoardColumn
    {
        public string DisciplineId { get; internal set; } = string.Empty;
        public string Title { get; internal set; } = string.Empty;
        public List<CoordinatorBoardCard> Cards { get; } = new();
    }

    public static class CoordinatorBoard
    {
        public const string OtherColumn = "other";

        private static readonly string[] StateOrder =
        {
            CoordinatorAssignmentStates.Returned,
            CoordinatorAssignmentStates.InProgress,
            CoordinatorAssignmentStates.Delivered,
            CoordinatorAssignmentStates.Ready,
            CoordinatorAssignmentStates.Blocked,
            CoordinatorAssignmentStates.Accepted,
            CoordinatorAssignmentStates.Closed,
            CoordinatorAssignmentStates.Draft,
            CoordinatorAssignmentStates.Cancelled
        };

        public static IReadOnlyList<CoordinatorBoardColumn> Build(
            CoordinatorWorkspace workspace,
            IReadOnlyDictionary<string, CoordinatorTaskStatus> statuses,
            string taskId = null,
            bool includeFinished = true)
        {
            var columns = workspace.Project.disciplines
                .Select(discipline => new CoordinatorBoardColumn { DisciplineId = discipline.id, Title = string.IsNullOrEmpty(discipline.title) ? discipline.id : discipline.title })
                .ToList();
            var other = new CoordinatorBoardColumn { DisciplineId = OtherColumn, Title = "Other" };
            foreach (var data in workspace.Tasks.Where(data => string.IsNullOrEmpty(taskId) || data.Task.id == taskId))
            {
                statuses.TryGetValue(data.Task.id, out var taskStatus);
                foreach (var assignment in data.Assignments)
                {
                    var card = new CoordinatorBoardCard { Task = data, Assignment = assignment, Status = taskStatus?.Find(assignment.id) };
                    if (!includeFinished && (card.State == CoordinatorAssignmentStates.Closed || card.State == CoordinatorAssignmentStates.Cancelled))
                        continue;
                    var primary = assignment.disciplines.FirstOrDefault();
                    (columns.FirstOrDefault(column => column.DisciplineId == primary) ?? other).Cards.Add(card);
                }
            }
            if (other.Cards.Count > 0)
                columns.Add(other);
            foreach (var column in columns)
                column.Cards.Sort(Compare);
            return columns;
        }

        private static int Compare(CoordinatorBoardCard left, CoordinatorBoardCard right)
        {
            var byState = Rank(left.State).CompareTo(Rank(right.State));
            if (byState != 0)
                return byState;
            var byTask = string.CompareOrdinal(left.Task.Task.id, right.Task.Task.id);
            return byTask != 0 ? byTask : string.CompareOrdinal(left.Assignment.id, right.Assignment.id);
        }

        private static int Rank(string state)
        {
            var index = Array.IndexOf(StateOrder, state);
            return index < 0 ? StateOrder.Length : index;
        }
    }
}
