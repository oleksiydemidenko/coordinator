using System.Collections.Generic;
using System.Linq;

namespace Coordinator.Editor
{
    public static class CoordinatorScope
    {
        public static IReadOnlyList<string> Zone(CoordinatorTaskRecord task, CoordinatorAssignmentRecord assignment)
        {
            return assignment.writeZone
                .Append(CoordinatorPaths.RepositoryDeliveryPath(task.id, assignment.id))
                .Distinct()
                .ToArray();
        }

        public static IReadOnlyList<string> Reserved(CoordinatorProjectRecord project, CoordinatorTaskRecord task)
        {
            return project.reservedPaths.Concat(task.reservedPaths)
                .Select(reserved => reserved.pattern)
                .Distinct()
                .ToArray();
        }

        public static string Base(CoordinatorTaskRecord task) =>
            string.IsNullOrEmpty(task.baseRevision.commit) ? task.baseRevision.branch : task.baseRevision.commit;

        public static IReadOnlyList<string> Commands(CoordinatorProjectRecord project, CoordinatorTaskRecord task, CoordinatorAssignmentRecord assignment)
        {
            var baseRevision = Base(task);
            var outsideZone = string.Join(" ", Zone(task, assignment).Select(pattern => $"':(exclude,glob){pattern}'"));
            var commands = new List<string>
            {
                $"git merge-base --is-ancestor {baseRevision} HEAD || echo \"base is not an ancestor of HEAD\"",
                $"git diff --no-renames --name-only {baseRevision} -- . {outsideZone}",
                $"git ls-files --others --exclude-standard -- . {outsideZone}"
            };
            var reserved = Reserved(project, task);
            if (reserved.Count > 0)
            {
                var inReserved = string.Join(" ", reserved.Select(pattern => $"':(glob){pattern}'"));
                commands.Add($"git diff --no-renames --name-only {baseRevision} -- {inReserved}");
                commands.Add($"git ls-files --others --exclude-standard -- {inReserved}");
            }
            return commands;
        }
    }
}
