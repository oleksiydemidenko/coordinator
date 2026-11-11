using System.Collections.Generic;
using System.Linq;

namespace Coordinator.Editor
{
    public sealed class CoordinatorScopeReport
    {
        public string TaskBranch { get; internal set; }
        public string Revision { get; internal set; }
        public string Start { get; internal set; }
        public bool StartIsBase { get; internal set; }
        public bool BaseIsAncestor { get; internal set; }
        public string Problem { get; internal set; }
        public List<string> OutsideZone { get; } = new();
        public List<string> Reserved { get; } = new();
        public bool Clean => Problem == null && BaseIsAncestor && OutsideZone.Count == 0 && Reserved.Count == 0;
    }

    public sealed class CoordinatorUncommittedReport
    {
        public List<string> InsideZone { get; } = new();
        public List<string> OutsideZone { get; } = new();
        public bool Clean => InsideZone.Count == 0 && OutsideZone.Count == 0;
    }

    public static class CoordinatorScope
    {
        public const string UncommittedCommand = "git status --porcelain";

        public static CoordinatorScopeReport Check(
            CoordinatorGit git,
            CoordinatorProjectRecord project,
            CoordinatorTaskRecord task,
            CoordinatorAssignmentRecord assignment,
            string revision,
            CoordinatorArchitector architector = null)
        {
            var report = new CoordinatorScopeReport { TaskBranch = TaskBranch(task) };
            report.Revision = git.Resolve(revision);
            if (report.Revision == null)
            {
                report.Problem = $"Revision '{revision}' is not in this repository.";
                return report;
            }
            var baseRevision = Base(task);
            var taskBranch = git.Resolve(report.TaskBranch);
            if (taskBranch != null)
            {
                report.Start = git.MergeBase(taskBranch, report.Revision);
                report.BaseIsAncestor = git.IsAncestor(baseRevision, taskBranch);
            }
            else
            {
                report.StartIsBase = true;
                report.Start = git.Resolve(baseRevision);
                report.BaseIsAncestor = report.Start != null && git.IsAncestor(report.Start, report.Revision);
            }
            if (report.Start == null)
            {
                report.Problem = report.StartIsBase
                    ? $"The task base '{baseRevision}' is not in this repository."
                    : $"The revision shares no history with the task branch '{report.TaskBranch}'.";
                return report;
            }
            var outside = Zone(task, assignment, architector).Select(pattern => ":(exclude,glob)" + pattern).Prepend(".").ToArray();
            report.OutsideZone.AddRange(Changed(git, report.Start, report.Revision, outside));
            var reserved = Reserved(project, task).Select(pattern => ":(glob)" + pattern).ToArray();
            if (reserved.Length > 0)
                report.Reserved.AddRange(Changed(git, report.Start, report.Revision, reserved));
            return report;
        }

        public static CoordinatorUncommittedReport Uncommitted(
            CoordinatorGit checkout,
            CoordinatorProjectRecord project,
            CoordinatorTaskRecord task,
            CoordinatorAssignmentRecord assignment,
            CoordinatorArchitector architector = null)
        {
            var report = new CoordinatorUncommittedReport();
            var all = Status(checkout, new[] { "." });
            var zone = Zone(task, assignment, architector).Select(pattern => ":(exclude,glob)" + pattern).Prepend(".").ToArray();
            var outside = new HashSet<string>(Status(checkout, zone), System.StringComparer.Ordinal);
            var reserved = Reserved(project, task).Select(pattern => ":(glob)" + pattern).ToArray();
            if (reserved.Length > 0)
                outside.UnionWith(Status(checkout, reserved));
            report.OutsideZone.AddRange(outside.OrderBy(path => path, System.StringComparer.Ordinal));
            report.InsideZone.AddRange(all.Where(path => !outside.Contains(path)));
            return report;
        }

        private static IEnumerable<string> Changed(CoordinatorGit git, string start, string revision, IReadOnlyCollection<string> pathspecs)
        {
            var output = git.Run(new[] { "diff", "--no-renames", "--name-only", start, revision, "--" }.Concat(pathspecs).ToArray()).Output;
            return Lines(output.Split('\n'));
        }

        private static IReadOnlyList<string> Status(CoordinatorGit git, IReadOnlyCollection<string> pathspecs)
        {
            var output = git.Run(new[] { "status", "--porcelain=v1", "-z", "--no-renames", "--untracked-files=all", "--" }.Concat(pathspecs).ToArray()).Output;
            return Lines(output.Split('\0').Where(entry => entry.Length > 3).Select(entry => entry.Substring(3))).ToArray();
        }

        private static IEnumerable<string> Lines(IEnumerable<string> lines) =>
            lines.Select(line => line.Trim()).Where(line => line.Length > 0).Distinct().OrderBy(line => line, System.StringComparer.Ordinal);

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

        public static string TaskBranch(CoordinatorTaskRecord task) =>
            string.IsNullOrEmpty(task.integrationBranch) ? Base(task) : task.integrationBranch;

        public static IReadOnlyList<string> Commands(
            CoordinatorProjectRecord project,
            CoordinatorTaskRecord task,
            CoordinatorAssignmentRecord assignment,
            CoordinatorArchitector architector = null)
        {
            var baseRevision = Base(task);
            var taskBranch = TaskBranch(task);
            var outsideZone = string.Join(" ", Zone(task, assignment, architector).Select(pattern => $"':(exclude,glob){pattern}'"));
            var commands = new List<string>
            {
                $"git merge-base --is-ancestor {baseRevision} {taskBranch} || echo \"The task branch {taskBranch} is missing or does not contain the task base: ask the lead. Until the branch exists, use {baseRevision}...HEAD in the commands below.\"",
                $"git diff --no-renames --name-only {taskBranch}...HEAD -- . {outsideZone}"
            };
            var reserved = Reserved(project, task);
            if (reserved.Count > 0)
                commands.Add($"git diff --no-renames --name-only {taskBranch}...HEAD -- {string.Join(" ", reserved.Select(pattern => $"':(glob){pattern}'"))}");
            return commands;
        }
    }
}
