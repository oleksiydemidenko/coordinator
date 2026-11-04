using System;
using System.Collections.Generic;
using System.IO;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorTestProject : IDisposable
    {
        public CoordinatorTestProject()
        {
            Root = Path.Combine(Path.GetTempPath(), "CoordinatorTests", Guid.NewGuid().ToString("N"));
            Paths = new CoordinatorPaths(Root);
            Project = new CoordinatorProjectRecord
            {
                disciplines = new List<CoordinatorDisciplineRecord> { new() { id = "code", title = "Code" } },
                reservedPaths = new List<CoordinatorReservedPathRecord>
                {
                    new() { pattern = "**/*.unity", reason = "Scenes" },
                    new() { pattern = "Assets/Core/**", reason = "Core" }
                },
                variables = new List<CoordinatorVariableRecord> { new() { id = "snapshot", description = "Compiler snapshot" } },
                checks = new List<CoordinatorCheckRecord>
                {
                    new() { id = "compile", title = "Compiles", command = "compile ${snapshot}" },
                    new() { id = "scope", title = "Inside the zone", generated = "scope" }
                }
            };
            Task = new CoordinatorTaskRecord
            {
                id = "doors",
                title = "Doors and lights",
                lead = "lead",
                targetBranch = "develop",
                baseRevision = new CoordinatorBaseRecord { branch = "coordination/doors-start", commit = "abc1234" },
                decisions = new List<CoordinatorDecisionRecord>
                {
                    new() { id = "door-authority", question = "Who opens doors?", owner = "lead", state = "decided", answer = "The host.", record = new CoordinatorRecordLink { path = "Docs/Doors.md" } },
                    new() { id = "light-timing", question = "When do lights react?", owner = "lead", record = new CoordinatorRecordLink { path = "Docs/Lights.md" } }
                }
            };
            Assignments = new List<CoordinatorAssignmentRecord>
            {
                Assignment("door-rules", "Assets/Scripts/Doors"),
                Assignment("light-rules", "Assets/Scripts/Lights")
            };
        }

        public string Root { get; }
        public CoordinatorPaths Paths { get; }
        public CoordinatorProjectRecord Project { get; }
        public CoordinatorTaskRecord Task { get; }
        public List<CoordinatorAssignmentRecord> Assignments { get; }

        public static CoordinatorAssignmentRecord Assignment(string id, string folder)
        {
            return new CoordinatorAssignmentRecord
            {
                id = id,
                title = id,
                disciplines = new List<string> { "code" },
                executor = new CoordinatorExecutorRecord { branch = "task/" + id },
                goal = "Rules for " + id,
                outputs = new List<CoordinatorOutputRecord> { new() { id = id + "-code", kind = "code", paths = new List<string> { folder + "/**" } } },
                writeZone = new List<string> { folder + "/**", folder + ".meta" },
                route = new List<CoordinatorRouteStepRecord> { new() { text = "Implement.", checks = new List<string> { "compile", "scope" } } },
                acceptance = new List<CoordinatorAcceptanceRecord> { new() { id = "common", text = "Checks pass.", checks = new List<string> { "compile", "scope" } } }
            };
        }

        public CoordinatorWorkspace Save()
        {
            CoordinatorJson.Write(Paths.ProjectPath, Project);
            var folder = Paths.TaskFolder(Task.id);
            CoordinatorJson.Write(Path.Combine(folder, CoordinatorPaths.TaskFile), Task);
            foreach (var assignment in Assignments)
                CoordinatorJson.Write(Path.Combine(folder, assignment.id + CoordinatorPaths.AssignmentSuffix), assignment);
            return CoordinatorWorkspace.Load(Root);
        }

        public void WriteFile(string repositoryPath, string text)
        {
            var path = Path.Combine(Root, repositoryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
                return;
            foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
