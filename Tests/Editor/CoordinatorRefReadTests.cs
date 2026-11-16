using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    // Reads with ref see the Coordinator files of a commit, so the lead reads develop's tasks from a checkout on a task branch.
    public sealed class CoordinatorRefReadTests
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
            project.WriteFile("Assets/Scripts/Doors/Door.cs", "class Door {}");
            Commit("base");
            project.Task.baseRevision.commit = Git("rev-parse", "HEAD").Trim();
            project.Task.approval = new CoordinatorSignatureRecord { by = "owner", date = "2026-11-17" };
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

        private JToken Query(object parameters)
        {
            var result = CoordinatorAgentApi.Query(JObject.FromObject(parameters), project.Root);
            Assert.That(result.Success, Is.True, result.ToJson());
            return JObject.Parse(result.ToJson())["data"];
        }

        [Test]
        public void ReadsWithRefSeeTheCommitAndReadsWithoutItSeeTheCheckout()
        {
            var develop = Git("rev-parse", "develop").Trim();
            Git("checkout", "-q", "-b", "task/other");
            project.Task.title = "Doors on the task branch";
            project.Assignments.Add(CoordinatorTestProject.Assignment("bell-rules", "Assets/Scripts/Bells"));
            project.Save();

            var here = Query(new { action = "tasks" });
            var there = Query(new { action = "tasks", @ref = "develop" });

            Assert.That(here["at"], Is.Null);
            Assert.That((string)here["tasks"][0]["title"], Is.EqualTo("Doors on the task branch"));
            Assert.That((int)here["tasks"][0]["assignments"], Is.EqualTo(3));
            Assert.That((string)there["at"]["ref"], Is.EqualTo("develop"));
            Assert.That((string)there["at"]["commit"], Is.EqualTo(develop));
            Assert.That((string)there["tasks"][0]["title"], Is.EqualTo("Doors and lights"));
            Assert.That((int)there["tasks"][0]["assignments"], Is.EqualTo(2));
            Assert.That((string)there["tasks"][0]["state"], Is.EqualTo(CoordinatorTaskStates.Approved));

            var board = Query(new { action = "board", @ref = "develop" });
            Assert.That(board["columns"][0]["cards"].Select(card => (string)card["assignment"]), Is.EqualTo(new[] { "door-rules", "light-rules" }));
            var task = Query(new { action = "task", task = "doors", @ref = develop.Substring(0, 10) });
            Assert.That((string)task["task"]["title"], Is.EqualTo("Doors and lights"));
            Assert.That(task["diagnostics"].Count(), Is.Zero);
            var card = Query(new { action = "card", task = "doors", assignment = "door-rules", @ref = "develop" });
            Assert.That((string)card["card"], Does.Contain("- `Assets/Scripts/Doors/**`"));
            Assert.That(card["diagnostics"].Count(), Is.Zero);
            Assert.That((bool)Query(new { action = "validate", @ref = "develop" })["clean"], Is.True);
            Assert.That(Query(new { action = "scope", task = "doors", assignment = "door-rules", @ref = "develop" })["commands"], Is.Not.Null);

            var missing = CoordinatorAgentApi.Query(JObject.FromObject(new { action = "card", task = "doors", assignment = "bell-rules", @ref = "develop" }), project.Root);
            Assert.That(missing.Code, Is.EqualTo("assignment_not_found"));
            Assert.That(missing.Message, Does.Contain("at develop"));
            Assert.That(CoordinatorAgentApi.Query(JObject.FromObject(new { action = "board", @ref = "no-such-branch" }), project.Root).Code, Is.EqualTo("ref_not_found"));
            Assert.That(CoordinatorAgentApi.Edit(JObject.FromObject(new { action = "generate_cards", @ref = "develop" }), project.Root).Code, Is.EqualTo("invalid_parameters"));
        }

        [Test]
        public void TheBoardFiltersByStateAndTasksLeaveClosedOnes()
        {
            var delivered = Query(new { action = "board", state = "ready" });
            Assert.That(delivered["columns"][0]["cards"].Select(card => (string)card["state"]), Has.All.EqualTo(CoordinatorAssignmentStates.Ready));
            Assert.That(Query(new { action = "board", state = "Delivered, in progress" })["columns"].Count(), Is.Zero, "Columns without a matching card are left out.");
            Assert.That(CoordinatorAgentApi.Query(JObject.FromObject(new { action = "board", state = "sleeping" }), project.Root).Code, Is.EqualTo("invalid_parameters"));
            Assert.That(Query(new { action = "tasks", include_closed = false })["tasks"].Count(), Is.EqualTo(1), "An approved task is open.");
        }

        [Test]
        public void ATreeSourceServesTheFilesOfACommitUnderTheProjectRoot()
        {
            var files = new Dictionary<string, string>
            {
                ["Architector/Groups/group_a.Gameplay.group.json"] = "{ \"id\": \"group_a\", \"name\": \"Gameplay\", \"parentGroupId\": \"\" }",
                ["Architector/Layers/layer_doors.Doors.layer.json"] = "{ \"layerId\": \"layer_doors\", \"name\": \"Doors\", \"parentGroupId\": \"group_a\", \"scope\": { \"folders\": [ \"Assets/Scripts/Doors\" ] } }",
                ["Coordinator/coordinator.project.json"] = CoordinatorJson.Serialize(project.Project),
                ["Coordinator/Tasks/doors/task.json"] = CoordinatorJson.Serialize(project.Task),
                ["Coordinator/Tasks/doors/door-rules.assignment.json"] = CoordinatorJson.Serialize(project.Assignments[0]),
                ["Coordinator/Tasks/doors/Cards/door-rules.md"] = null
            };
            var source = new CoordinatorTreeSource(project.Root, files);

            var workspace = CoordinatorWorkspace.Load(project.Root, source);

            Assert.That(workspace.LoadDiagnostics, Is.Empty);
            Assert.That(workspace.FindTask("doors").Assignments.Select(assignment => assignment.id), Is.EqualTo(new[] { "door-rules" }));
            Assert.That(workspace.Architector.FindByPath("Gameplay/Doors"), Is.Not.Null);
            Assert.That(source.DirectoryExists(Path.Combine(project.Root, "Coordinator", "Tasks", "doors", "Cards")), Is.True);
            Assert.That(source.Files(Path.Combine(project.Root, "Coordinator", "Tasks", "doors"), "*.json").Count(), Is.EqualTo(2));
        }

        [Test]
        public void ATreeHoldsEveryPathAndTheTextOfTheJsonFiles()
        {
            project.WriteFile("Coordinator/Tasks/doors/Cards/notes.md", "# Notes\r\n");
            project.WriteFile("Coordinator/Tasks/doors/extra.json", "{ \"name\": \"Grüße\" }\r\n");
            Commit("files");

            var tree = git.Tree("develop", new[] { "Coordinator" }, new[] { ".json" });

            Assert.That(tree.ContainsKey("Coordinator/Tasks/doors/Cards/notes.md"), Is.True);
            Assert.That(tree["Coordinator/Tasks/doors/Cards/notes.md"], Is.Null);
            Assert.That(tree["Coordinator/Tasks/doors/extra.json"], Is.EqualTo("{ \"name\": \"Grüße\" }\n"));
            Assert.That(tree["Coordinator/Tasks/doors/task.json"], Does.Contain("\"Doors and lights\""));
        }
    }
}
