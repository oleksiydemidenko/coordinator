using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Coordinator.Editor
{
    public sealed class CoordinatorTaskData
    {
        public CoordinatorTaskData(string folderId, CoordinatorTaskRecord task)
        {
            FolderId = folderId;
            Task = task;
        }

        public string FolderId { get; }
        public CoordinatorTaskRecord Task { get; }
        public CoordinatorTaskReviewRecord TaskReview { get; set; }
        public List<CoordinatorAssignmentRecord> Assignments { get; } = new();
        public Dictionary<string, CoordinatorDeliveryRecord> Deliveries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CoordinatorReviewRecord> Reviews { get; } = new(StringComparer.Ordinal);

        public string FilePath(string name) => CoordinatorPaths.RepositoryTaskFolder(FolderId) + "/" + name;
    }

    public sealed class CoordinatorWorkspace
    {
        private CoordinatorWorkspace(CoordinatorPaths paths)
        {
            Paths = paths;
        }

        public CoordinatorPaths Paths { get; }
        public CoordinatorProjectRecord Project { get; private set; } = new();
        public List<CoordinatorTaskData> Tasks { get; } = new();
        public List<CoordinatorDiagnostic> LoadDiagnostics { get; } = new();
        public CoordinatorArchitector Architector { get; private set; }

        public CoordinatorTaskData FindTask(string id) => Tasks.FirstOrDefault(task => task.Task.id == id);

        public static CoordinatorWorkspace Load(string projectRoot)
        {
            var workspace = new CoordinatorWorkspace(new CoordinatorPaths(projectRoot));
            workspace.LoadAll();
            workspace.Architector = CoordinatorArchitector.Load(workspace.Paths.ProjectRoot);
            foreach (var problem in workspace.Architector?.Problems ?? new List<string>())
                workspace.LoadDiagnostics.Add(new CoordinatorDiagnostic("architector_unreadable", CoordinatorSeverities.Warning, CoordinatorArchitector.RootFolder, string.Empty, problem));
            return workspace;
        }

        private void LoadAll()
        {
            if (File.Exists(Paths.ProjectPath))
                Project = Read<CoordinatorProjectRecord>(Paths.ProjectPath) ?? new CoordinatorProjectRecord();
            if (!Directory.Exists(Paths.TasksRoot))
                return;
            foreach (var folder in Directory.GetDirectories(Paths.TasksRoot).OrderBy(Path.GetFileName, StringComparer.Ordinal))
                LoadTask(folder);
        }

        private void LoadTask(string folder)
        {
            var folderId = Path.GetFileName(folder);
            var taskPath = Path.Combine(folder, CoordinatorPaths.TaskFile);
            if (!File.Exists(taskPath))
            {
                Report("task_file_missing", Paths.RepositoryPath(folder), folderId, $"The task folder has no {CoordinatorPaths.TaskFile}.");
                return;
            }
            var task = Read<CoordinatorTaskRecord>(taskPath);
            if (task == null)
                return;
            if (task.id != folderId)
                Report("file_name_mismatch", Paths.RepositoryPath(taskPath), task.id, $"Task id '{task.id}' differs from its folder '{folderId}'.");
            var data = new CoordinatorTaskData(folderId, task);
            Tasks.Add(data);

            var reviewPath = Path.Combine(folder, CoordinatorPaths.TaskReviewFile);
            if (File.Exists(reviewPath))
                data.TaskReview = Read<CoordinatorTaskReviewRecord>(reviewPath);
            foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (name == CoordinatorPaths.TaskFile || name == CoordinatorPaths.TaskReviewFile)
                    continue;
                if (name.EndsWith(CoordinatorPaths.AssignmentSuffix, StringComparison.Ordinal))
                {
                    var assignment = Read<CoordinatorAssignmentRecord>(file);
                    if (assignment == null)
                        continue;
                    ExpectId(file, name, CoordinatorPaths.AssignmentSuffix, assignment.id);
                    data.Assignments.Add(assignment);
                }
                else if (name.EndsWith(CoordinatorPaths.DeliverySuffix, StringComparison.Ordinal))
                {
                    var delivery = Read<CoordinatorDeliveryRecord>(file);
                    if (delivery == null)
                        continue;
                    ExpectId(file, name, CoordinatorPaths.DeliverySuffix, delivery.assignment);
                    data.Deliveries[delivery.assignment] = delivery;
                }
                else if (name.EndsWith(CoordinatorPaths.ReviewSuffix, StringComparison.Ordinal))
                {
                    var review = Read<CoordinatorReviewRecord>(file);
                    if (review == null)
                        continue;
                    ExpectId(file, name, CoordinatorPaths.ReviewSuffix, review.assignment);
                    data.Reviews[review.assignment] = review;
                }
                else
                    Report("unknown_file", Paths.RepositoryPath(file), name, "Only task, assignment, delivery and review files belong in a task folder.");
            }
            data.Assignments.Sort((left, right) => string.CompareOrdinal(left.id, right.id));
        }

        private void ExpectId(string file, string name, string suffix, string id)
        {
            var expected = name.Substring(0, name.Length - suffix.Length);
            if (id != expected)
                Report("file_name_mismatch", Paths.RepositoryPath(file), id, $"The file is named '{expected}' but holds '{id}'.");
        }

        private T Read<T>(string path) where T : class
        {
            try
            {
                return CoordinatorJson.Parse<T>(File.ReadAllText(path));
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                Report("invalid_json", Paths.RepositoryPath(path), string.Empty, exception.Message);
                return null;
            }
        }

        private void Report(string code, string file, string subject, string message)
        {
            LoadDiagnostics.Add(new CoordinatorDiagnostic(code, CoordinatorSeverities.Error, file, subject, message));
        }
    }
}
