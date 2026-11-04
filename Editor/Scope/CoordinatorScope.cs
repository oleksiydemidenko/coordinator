using System.Collections.Generic;
using System.Linq;

namespace Coordinator.Editor
{
    public sealed class CoordinatorScopeReport
    {
        public bool BaseIsAncestor { get; internal set; }
        public List<string> OutsideZone { get; } = new();
        public List<string> Reserved { get; } = new();
        public bool Clean => BaseIsAncestor && OutsideZone.Count == 0 && Reserved.Count == 0;
    }

    public static class CoordinatorScope
    {
        public static CoordinatorScopeReport Check(
            CoordinatorGit git,
            CoordinatorProjectRecord project,
            CoordinatorTaskRecord task,
            CoordinatorAssignmentRecord assignment,
            CoordinatorArchitector architector = null)
        {
            var baseRevision = Base(task);
            var report = new CoordinatorScopeReport { BaseIsAncestor = git.IsAncestor(baseRevision, "HEAD") };
            var outside = Zone(task, assignment, architector).Select(pattern => ":(exclude,glob)" + pattern).Prepend(".").ToArray();
            report.OutsideZone.AddRange(Paths(git, baseRevision, outside));
            var reserved = Reserved(project, task).Select(pattern => ":(glob)" + pattern).ToArray();
            if (reserved.Length > 0)
                report.Reserved.AddRange(Paths(git, baseRevision, reserved));
            return report;
        }

        private static IEnumerable<string> Paths(CoordinatorGit git, string baseRevision, IReadOnlyCollection<string> pathspecs)
        {
            var changed = git.Run(new[] { "diff", "--no-renames", "--name-only", baseRevision, "--" }.Concat(pathspecs).ToArray()).Output;
            var untracked = git.Run(new[] { "ls-files", "--others", "--exclude-standard", "--" }.Concat(pathspecs).ToArray()).Output;
            return (changed + "\n" + untracked).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Distinct().OrderBy(line => line, System.StringComparer.Ordinal);
        }

        public static IReadOnlyList<string> AuthoredZone(CoordinatorAssignmentRecord assignment, CoordinatorArchitector architector = null)
        {
            var layerPatterns = architector == null
                ? Enumerable.Empty<string>()
                : assignment.layers.Where(layer => layer.role == "zone")
                    .Select(layer => architector.FindByPath(layer.path))
                    .Where(layer => layer != null)
                    .SelectMany(CoordinatorArchitector.ZonePatterns);
            return assignment.writeZone.Concat(layerPatterns).Distinct().ToArray();
        }

        public static IReadOnlyList<string> Zone(CoordinatorTaskRecord task, CoordinatorAssignmentRecord assignment, CoordinatorArchitector architector = null)
        {
            return AuthoredZone(assignment, architector)
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

        public static IReadOnlyList<string> Commands(
            CoordinatorProjectRecord project,
            CoordinatorTaskRecord task,
            CoordinatorAssignmentRecord assignment,
            CoordinatorArchitector architector = null)
        {
            var baseRevision = Base(task);
            var outsideZone = string.Join(" ", Zone(task, assignment, architector).Select(pattern => $"':(exclude,glob){pattern}'"));
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
