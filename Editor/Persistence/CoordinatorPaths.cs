using System.IO;

namespace Coordinator.Editor
{
    public sealed class CoordinatorPaths
    {
        public const string RootFolder = "Coordinator";
        public const string ProjectFile = "coordinator.project.json";
        public const string TasksFolder = "Tasks";
        public const string TaskFile = "task.json";
        public const string TaskReviewFile = "task.review.json";
        public const string AssignmentSuffix = ".assignment.json";
        public const string DeliverySuffix = ".delivery.json";
        public const string ReviewSuffix = ".review.json";

        public CoordinatorPaths(string projectRoot)
        {
            ProjectRoot = Path.GetFullPath(projectRoot);
            DataRoot = Path.Combine(ProjectRoot, RootFolder);
        }

        public string ProjectRoot { get; }
        public string DataRoot { get; }
        public string ProjectPath => Path.Combine(DataRoot, ProjectFile);
        public string TasksRoot => Path.Combine(DataRoot, TasksFolder);

        public string TaskFolder(string taskId) => Path.Combine(TasksRoot, taskId);

        public static string RepositoryTaskFolder(string taskId) => $"{RootFolder}/{TasksFolder}/{taskId}";

        public static string RepositoryDeliveryPath(string taskId, string assignmentId) =>
            $"{RepositoryTaskFolder(taskId)}/{assignmentId}{DeliverySuffix}";

        public string RepositoryPath(string fullPath) =>
            Path.GetRelativePath(ProjectRoot, fullPath).Replace('\\', '/');
    }
}
