using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorAgentApiTests
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
            CoordinatorJson.Write(project.Paths.ProjectPath, project.Project);
            project.WriteFile("Assets/Scripts/Doors/Door.cs", "class Door {}");
            Commit("base");
            project.Task.baseRevision.commit = Git("rev-parse", "HEAD").Trim();
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

        private static JObject Json(object record) => JObject.Parse(CoordinatorJson.Serialize(record));

        private CoordinatorAgentResult Edit(object parameters) => CoordinatorAgentApi.Edit(JObject.FromObject(parameters), project.Root);

        private CoordinatorAgentResult Query(object parameters) => CoordinatorAgentApi.Query(JObject.FromObject(parameters), project.Root);

        private void CreateTask()
        {
            Assert.That(Edit(new { action = "create_task", task = Json(project.Task) }).Success, Is.True);
            foreach (var assignment in project.Assignments)
                Assert.That(Edit(new { action = "set_assignment", task = "doors", assignment = Json(assignment) }).Success, Is.True);
        }

        [Test]
        public void TheLeadBuildsADraftAndAnEditThatAddsErrorsIsRolledBack()
        {
            CreateTask();
            var path = Path.Combine(project.Paths.TaskFolder("doors"), "door-rules" + CoordinatorPaths.AssignmentSuffix);
            var before = File.ReadAllText(path);
            var overlapping = CoordinatorTestProject.Assignment("door-rules", "Assets/Scripts/Lights");

            var result = Edit(new { action = "set_assignment", task = "doors", assignment = Json(overlapping) });

            Assert.That(result.Success, Is.False);
            Assert.That(result.Code, Is.EqualTo("validation_failed"));
            Assert.That(result.ToJson(), Does.Contain("zone_overlap"));
            Assert.That(File.ReadAllText(path), Is.EqualTo(before));
            Assert.That(CoordinatorWorkspace.Load(project.Root).FindTask("doors").Task.approval, Is.Null);
        }

        [Test]
        public void EditsCanBeUndoneAndRedoneUnlessTheFilesChangedSince()
        {
            CoordinatorHistory.Clear();
            CreateTask();
            var taskFile = Path.Combine(project.Paths.TaskFolder("doors"), CoordinatorPaths.TaskFile);
            var approved = CoordinatorAgentApi.ApproveByPerson(project.Root, "doors", "owner");
            Assert.That(approved.Success, Is.True);
            var approvedText = File.ReadAllText(taskFile);
            Assert.That(CoordinatorHistory.NextUndoName, Does.Contain("approved"));

            Assert.That(CoordinatorHistory.Undo(), Does.StartWith("Undone"));
            Assert.That(File.ReadAllText(taskFile), Does.Not.Contain("\"approval\": {"));
            Assert.That(CoordinatorHistory.Redo(), Does.StartWith("Redone"));
            Assert.That(File.ReadAllText(taskFile), Is.EqualTo(approvedText));

            File.WriteAllText(taskFile, approvedText.Replace("owner", "someone else"));
            Assert.That(CoordinatorHistory.Undo(), Does.StartWith("Cannot undo").And.Contain(CoordinatorPaths.TaskFile));
            Assert.That(File.ReadAllText(taskFile), Does.Contain("someone else"), "A refused undo leaves the files as they are.");
            File.WriteAllText(taskFile, approvedText);

            while (CoordinatorHistory.CanUndo)
                Assert.That(CoordinatorHistory.Undo(), Does.StartWith("Undone"));
            Assert.That(Directory.Exists(project.Paths.TaskFolder("doors")), Is.False, "Undoing create_task removes the task folder it created.");
            Assert.That(CoordinatorHistory.Redo(), Does.StartWith("Redone"));
            Assert.That(File.Exists(taskFile), Is.True);
        }

        [Test]
        public void ApproveAllApprovesEveryDraftInOneUndoStep()
        {
            CreateTask();
            var second = Json(project.Task);
            second["id"] = "lights";
            second["title"] = "Lights";
            second["integrationBranch"] = "task/lights";
            Assert.That(Edit(new { action = "create_task", task = second }).Success, Is.True);
            CoordinatorHistory.Clear();

            var approved = CoordinatorAgentApi.ApproveAllByPerson(project.Root, new[] { "doors", "lights" }, "owner");

            Assert.That(approved.Success, Is.True, approved.ToJson());
            var workspace = CoordinatorWorkspace.Load(project.Root);
            Assert.That(workspace.FindTask("doors").Task.approval.by, Is.EqualTo("owner"));
            Assert.That(workspace.FindTask("lights").Task.approval.by, Is.EqualTo("owner"));
            Assert.That(CoordinatorAgentApi.ApproveAllByPerson(project.Root, new[] { "doors", "lights" }, "owner").Code, Is.EqualTo("nothing_to_approve"));

            Assert.That(CoordinatorHistory.Undo(), Does.StartWith("Undone"));
            workspace = CoordinatorWorkspace.Load(project.Root);
            Assert.That(workspace.FindTask("doors").Task.approval, Is.Null);
            Assert.That(workspace.FindTask("lights").Task.approval, Is.Null);
            Assert.That(CoordinatorHistory.CanUndo, Is.False, "Approving several tasks is one Undo step.");
        }

        [Test]
        public void OnlyAPersonApprovesAndAcceptanceNamesThePersonWhoDecided()
        {
            CreateTask();

            Assert.That(Edit(new { action = "approve_task", task = "doors" }).Code, Is.EqualTo("needs_person"));
            var withoutPerson = Edit(new { action = "record_review", task = "doors", assignment = "door-rules", round = new { revision = "abc", verdict = "accepted", acceptedBy = "agent" } });
            Assert.That(withoutPerson.Code, Is.EqualTo("needs_person"));

            Assert.That(CoordinatorAgentApi.ApproveByPerson(project.Root, "doors", "owner").Success, Is.True);
            Assert.That(CoordinatorAgentApi.ApproveByPerson(project.Root, "doors", "owner").Code, Is.EqualTo("already_approved"));
            var accepted = Edit(new { action = "record_review", task = "doors", assignment = "door-rules", round = new { revision = "abc", verdict = "accepted", acceptedBy = "agent", instructedBy = "owner" } });
            Assert.That(accepted.Success, Is.True, accepted.ToJson());
            Assert.That(CoordinatorWorkspace.Load(project.Root).FindTask("doors").Reviews["door-rules"].rounds.Single().instructedBy, Is.EqualTo("owner"));
        }

        [Test]
        public void UnknownFieldsAndUnknownTasksAreRejectedWithACode()
        {
            CreateTask();
            var record = Json(project.Assignments[0]);
            record["zone"] = new JArray("Assets/**");

            Assert.That(Edit(new { action = "set_assignment", task = "doors", assignment = record }).Code, Is.EqualTo("invalid_parameters"));
            Assert.That(Query(new { action = "task", task = "missing" }).Code, Is.EqualTo("task_not_found"));
            Assert.That(Edit(new { action = "create_task", task = Json(new CoordinatorTaskRecord { id = "../escape", title = "x", lead = "x", targetBranch = "x" }) }).Code,
                Is.EqualTo("invalid_id"));
        }

        [Test]
        public void BoardTaskAndCardShowComputedStates()
        {
            CreateTask();
            CoordinatorAgentApi.ApproveByPerson(project.Root, "doors", "owner");
            Commit("task");

            var board = JObject.Parse(Query(new { action = "board" }).ToJson());
            var card = JObject.Parse(Query(new { action = "card", task = "doors", assignment = "door-rules" }).ToJson());
            var task = JObject.Parse(Query(new { action = "task", task = "doors" }).ToJson());

            Assert.That((string)board["data"]["columns"][0]["cards"][0]["state"], Is.EqualTo(CoordinatorAssignmentStates.Ready));
            Assert.That((string)card["data"]["card"], Does.Contain("- `Assets/Scripts/Doors/**`"));
            Assert.That((string)task["data"]["state"], Is.EqualTo(CoordinatorTaskStates.Approved));
            Assert.That(task["data"]["diagnostics"].Count(), Is.Zero);
        }

        [Test]
        public void ScopeChecksAnExecutorCheckoutAgainstTheZoneAndReservedPaths()
        {
            CreateTask();
            Commit("task");
            var worktree = project.Root + "-door-rules";
            Git("worktree", "add", "-q", "-b", "task/door-rules", worktree, project.Task.baseRevision.commit);
            try
            {
                File.WriteAllText(Path.Combine(worktree, "Assets/Scripts/Doors/Door.cs"), "class Door { int open; }");
                File.WriteAllText(Path.Combine(worktree, "Assets/Scripts/Doors/Hall.unity"), "scene");
                Directory.CreateDirectory(Path.Combine(worktree, "Assets/Scripts/Lights"));
                File.WriteAllText(Path.Combine(worktree, "Assets/Scripts/Lights/Lamp.cs"), "class Lamp {}");

                var report = JObject.Parse(Query(new { action = "scope", task = "doors", assignment = "door-rules", checkout = worktree }).ToJson())["data"];

                Assert.That((bool)report["clean"], Is.False);
                Assert.That((bool)report["base_is_ancestor"], Is.True);
                Assert.That(report["outside_zone"].Values<string>(), Is.EqualTo(new[] { "Assets/Scripts/Lights/Lamp.cs" }));
                Assert.That(report["reserved"].Values<string>(), Is.EqualTo(new[] { "Assets/Scripts/Doors/Hall.unity" }));
            }
            finally
            {
                Directory.Delete(worktree, true);
            }
        }
    }
}
