using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorRequirementChangeTests
    {
        private CoordinatorTestProject project;
        private CoordinatorGit git;

        [SetUp]
        public void SetUp()
        {
            project = new CoordinatorTestProject();
            Directory.CreateDirectory(project.Root);
            git = new CoordinatorGit(project.Root);
            Git("init", "-q", "-b", "develop");
            project.WriteFile("Docs/Doors.md", "# Doors\n\nThe host opens doors.\n");
            project.WriteFile("Docs/Lights.md", "# Lights\n");
            Commit("design");
            project.Task.baseRevision.commit = Git("rev-parse", "HEAD").Trim();
            project.Task.sources = new List<CoordinatorDocumentRef> { new() { path = "Docs/Doors.md", requirements = new List<string> { "DR-001" } }, new() { path = "Docs/Lights.md" } };
            project.Save();
            Commit("task");
        }

        [TearDown]
        public void TearDown()
        {
            CoordinatorHistory.Clear();
            project.Dispose();
        }

        private string Git(params string[] arguments) => git.Run(arguments).Output;

        private void Commit(string message)
        {
            Git("add", "-A");
            Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "--allow-empty", "-m", message);
        }

        private CoordinatorTaskStatus Status() =>
            CoordinatorStates.Compute(CoordinatorWorkspace.Load(project.Root).FindTask("doors"), git);

        private void Approve()
        {
            Assert.That(CoordinatorAgentApi.ApproveByPerson(project.Root, "doors", "owner").Success, Is.True);
            Commit("approve");
        }

        [Test]
        public void ApprovalRecordsTheRevisionOfEachSource()
        {
            var design = Git("log", "-1", "--format=%H", "--", "Docs/Doors.md").Trim();

            Approve();

            var sources = CoordinatorWorkspace.Load(project.Root).FindTask("doors").Task.sources;
            Assert.That(sources.Select(source => source.revision), Is.EqualTo(new[] { design, design }));
            Assert.That(Status().RequirementChanges, Is.Empty);
        }

        [Test]
        public void ASourceChangedAfterApprovalMarksTheTaskAndAnUntouchedOneDoesNot()
        {
            Approve();
            project.WriteFile("Docs/Doors.md", "# Doors\n\nThe client opens doors.\n");
            Commit("doors change");
            var change = Git("rev-parse", "HEAD").Trim();
            project.WriteFile("Assets/Scripts/Unrelated.cs", "class Unrelated {}");
            Commit("unrelated");

            var changes = Status().RequirementChanges;

            Assert.That(changes.Select(item => item.Path), Is.EqualTo(new[] { "Docs/Doors.md" }));
            Assert.That(changes.Single().Commits, Is.EqualTo(new[] { change }));
            Assert.That(changes.Single().Text("develop"), Does.StartWith("Requirement changed: Docs/Doors.md has 1 commit(s) on develop"));
            var board = JObject.Parse(CoordinatorAgentApi.Query(JObject.FromObject(new { action = "board" }), project.Root).ToJson());
            Assert.That(board["data"]["columns"][0]["cards"][0]["requirement_changed"].Values<string>().Single(), Does.Contain("Docs/Doors.md"));
        }

        [Test]
        public void ADecisionThatRecordsTheChangeAnswersIt()
        {
            Approve();
            project.WriteFile("Docs/Doors.md", "# Doors\n\nThe client opens doors.\n");
            Commit("doors change");
            var change = Git("rev-parse", "HEAD").Trim();
            Assert.That(Status().RequirementChanges, Has.Count.EqualTo(1));

            var decided = CoordinatorAgentApi.Edit(JObject.FromObject(new
            {
                action = "set_decision",
                task = "doors",
                decision = new { id = "door-client", question = "Who opens doors now?", owner = "lead", state = "decided", answer = "The client.", record = new { path = "Docs/Doors.md", revision = change } }
            }), project.Root);
            Assert.That(decided.Success, Is.True, decided.ToJson());
            Commit("decision");

            Assert.That(Status().RequirementChanges, Is.Empty);
        }

        [Test]
        public void ATaskApprovedBeforeTheFieldExistedLoadsWithoutMarks()
        {
            var path = Path.Combine(project.Paths.TaskFolder("doors"), CoordinatorPaths.TaskFile);
            var json = JObject.Parse(File.ReadAllText(path));
            json["approval"] = new JObject { ["by"] = "owner", ["date"] = "2026-11-01" };
            File.WriteAllText(path, json.ToString());
            Commit("approved by an older version");
            project.WriteFile("Docs/Doors.md", "# Doors\n\nChanged.\n");
            Commit("doors change");

            var workspace = CoordinatorWorkspace.Load(project.Root);

            Assert.That(CoordinatorValidation.Validate(workspace, git).Where(diagnostic => diagnostic.IsError), Is.Empty);
            Assert.That(workspace.FindTask("doors").Task.sources.All(source => source.revision == null), Is.True);
            Assert.That(CoordinatorStates.Compute(workspace.FindTask("doors"), git).RequirementChanges, Is.Empty);
        }
    }
}
