using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    // Safe, Free and Lazy: only Lazy lets the lead agent approve a task, after validate shows nothing for it.
    public sealed class CoordinatorWorkflowModeTests
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
            Assert.That(Edit(new { action = "create_task", task = Json(project.Task) }).Success, Is.True);
            foreach (var assignment in project.Assignments)
                Assert.That(Edit(new { action = "set_assignment", task = "doors", assignment = Json(assignment) }).Success, Is.True);
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

        private void UseMode(string id) => Assert.That(CoordinatorWorkflowModes.Write(project.Root, CoordinatorWorkflowModes.Find(id)), Is.True);

        private CoordinatorSignatureRecord Approval() => CoordinatorWorkspace.Load(project.Root).FindTask("doors").Task.approval;

        [Test]
        public void SafeAndFreeKeepApprovalForAPerson()
        {
            Assert.That(CoordinatorWorkflowModes.Read(project.Root).Id, Is.EqualTo(CoordinatorWorkflowModes.Safe), "No mirror file is Safe.");
            var safe = Edit(new { action = "approve_task", task = "doors", by = "lead-agent", instructed_by = "owner" });
            Assert.That(safe.Code, Is.EqualTo("needs_person"));
            Assert.That(safe.Message, Does.Contain("Safe"));

            UseMode(CoordinatorWorkflowModes.Free);
            var free = Edit(new { action = "approve_task", task = "doors", by = "lead-agent", instructed_by = "owner" });
            Assert.That(free.Code, Is.EqualTo("needs_person"));
            Assert.That(free.Message, Does.Contain("Free"));
            Assert.That(Approval(), Is.Null);
        }

        [Test]
        public void LazyApprovalNamesTheAgentAndThePersonWhoInstructed()
        {
            UseMode(CoordinatorWorkflowModes.Lazy);

            Assert.That(Edit(new { action = "approve_task", task = "doors" }).Code, Is.EqualTo("needs_person"));
            Assert.That(Edit(new { action = "approve_task", task = "doors", by = "lead-agent" }).Code, Is.EqualTo("needs_person"));
            Assert.That(Edit(new { action = "approve_task", task = "doors", by = "owner", instructed_by = "Owner" }).Code, Is.EqualTo("invalid_parameters"));
            Assert.That(Edit(new { action = "approve_task", task = "missing", by = "lead-agent", instructed_by = "owner" }).Code, Is.EqualTo("task_not_found"));
            Assert.That(Approval(), Is.Null);

            var approved = Edit(new { action = "approve_task", task = "doors", by = "lead-agent", instructed_by = "owner" });

            Assert.That(approved.Success, Is.True, approved.ToJson());
            Assert.That(approved.Message, Does.Contain("lead-agent").And.Contain("owner"));
            Assert.That(Approval().by, Is.EqualTo("lead-agent"));
            Assert.That(Approval().instructedBy, Is.EqualTo("owner"));
            Assert.That(CoordinatorHistory.NextUndoName, Does.Contain("approved by lead-agent"), "An agent's approval is one Undo step.");
            var workspace = CoordinatorWorkspace.Load(project.Root);
            Assert.That(CoordinatorCards.Task(workspace.Project, workspace.FindTask("doors")), Does.Contain("Approved by lead-agent, instructed by owner, on"));
            Assert.That(Edit(new { action = "approve_task", task = "doors", by = "lead-agent", instructedBy = "owner" }).Code, Is.EqualTo("already_approved"));
            var tasks = JObject.Parse(Query(new { action = "tasks" }).ToJson());
            Assert.That((string)tasks["data"]["tasks"][0]["approval"]["instructed_by"], Is.EqualTo("owner"));
        }

        [Test]
        public void LazyApprovalNeedsValidateToShowNothingForTheTask()
        {
            UseMode(CoordinatorWorkflowModes.Lazy);
            var unlinked = new CoordinatorDecisionRecord { id = "lamp-colour", question = "Which colour?", owner = "lead" };
            Assert.That(Edit(new { action = "set_decision", task = "doors", decision = Json(unlinked) }).Success, Is.True, "A warning does not roll an edit back.");

            var refused = Edit(new { action = "approve_task", task = "doors", by = "lead-agent", instructed_by = "owner" });

            Assert.That(refused.Code, Is.EqualTo("not_clean"));
            Assert.That(refused.ToJson(), Does.Contain("decision_without_record"));
            Assert.That(Approval(), Is.Null);
            var validate = JObject.Parse(Query(new { action = "validate", task = "doors" }).ToJson())["data"];
            Assert.That((bool)validate["clean"], Is.False);
            Assert.That((int)validate["warnings"], Is.EqualTo(1));

            unlinked.record = new CoordinatorRecordLink { path = "Docs/Lights.md" };
            Assert.That(Edit(new { action = "set_decision", task = "doors", decision = Json(unlinked) }).Success, Is.True);
            Assert.That((bool)JObject.Parse(Query(new { action = "validate", task = "doors" }).ToJson())["data"]["clean"], Is.True);
            Assert.That(Edit(new { action = "approve_task", task = "doors", by = "lead-agent", instructed_by = "owner" }).Success, Is.True);
        }

        [Test]
        public void LazyApprovalSeesAZoneThatAnotherTaskSharesAndLeavesCancelledTasks()
        {
            UseMode(CoordinatorWorkflowModes.Lazy);
            // The other task sorts first, so the overlap is reported on its file; the check still belongs to doors too.
            var other = Json(project.Task);
            other["id"] = "alarms";
            other["integrationBranch"] = "task/alarms";
            project.WriteFile("Coordinator/Tasks/alarms/task.json", other.ToString());
            CoordinatorJson.Write(Path.Combine(project.Paths.TaskFolder("alarms"), "alarm-rules" + CoordinatorPaths.AssignmentSuffix),
                CoordinatorTestProject.Assignment("alarm-rules", "Assets/Scripts/Doors"));

            var refused = Edit(new { action = "approve_task", task = "doors", by = "lead-agent", instructed_by = "owner" });

            Assert.That(refused.Code, Is.EqualTo("not_clean"), refused.ToJson());
            Assert.That(refused.ToJson(), Does.Contain("zone_overlap"));
            var doors = JObject.Parse(Query(new { action = "validate", task = "doors" }).ToJson())["data"];
            Assert.That(doors["diagnostics"].Select(item => (string)item["file"]), Has.All.StartWith("Coordinator/Tasks/alarms/"));
            Assert.That(Query(new { action = "validate", task = "nope" }).Code, Is.EqualTo("task_not_found"));

            Directory.Delete(project.Paths.TaskFolder("alarms"), true);
            var cancelled = CoordinatorWorkspace.Load(project.Root).FindTask("doors").Task;
            cancelled.cancelled = new CoordinatorCancellationRecord { by = "owner", reason = "Not needed", date = "2026-11-17" };
            CoordinatorJson.Write(Path.Combine(project.Paths.TaskFolder("doors"), CoordinatorPaths.TaskFile), cancelled);
            Assert.That(Edit(new { action = "approve_task", task = "doors", by = "lead-agent", instructed_by = "owner" }).Code, Is.EqualTo("task_cancelled"));
        }

        [Test]
        public void TheMirrorFileIsReadByIdAndAnythingElseIsSafe()
        {
            var path = CoordinatorWorkflowModes.PathFor(project.Root);
            UseMode(CoordinatorWorkflowModes.Lazy);
            var text = File.ReadAllText(path);
            Assert.That((string)JObject.Parse(text)["workflowMode"], Is.EqualTo("lazy"));
            Assert.That((bool)JObject.Parse(text)["agentMayApprove"], Is.True);
            var written = File.GetLastWriteTimeUtc(path);
            UseMode(CoordinatorWorkflowModes.Lazy);
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(written), "The same mode is not written again.");

            File.WriteAllText(path, "{ \"workflowMode\": \"reckless\", \"agentMayApprove\": true }");
            Assert.That(CoordinatorWorkflowModes.Read(project.Root).Id, Is.EqualTo(CoordinatorWorkflowModes.Safe), "Only the id counts.");
            File.WriteAllText(path, "not json");
            Assert.That(CoordinatorWorkflowModes.Read(project.Root).Id, Is.EqualTo(CoordinatorWorkflowModes.Safe));

            UseMode(CoordinatorWorkflowModes.Free);
            var capabilities = JObject.Parse(Query(new { action = "capabilities" }).ToJson())["data"];
            Assert.That((string)capabilities["workflow_mode"]["id"], Is.EqualTo("free"));
            Assert.That((bool)capabilities["workflow_mode"]["agent_may_approve"], Is.False);
            Assert.That(capabilities["rules"].Values<string>(), Has.Some.Contains("approve_task refuses"));
            UseMode(CoordinatorWorkflowModes.Lazy);
            capabilities = JObject.Parse(Query(new { action = "capabilities" }).ToJson())["data"];
            Assert.That(capabilities["rules"].Values<string>(), Has.Some.Contains("instructed_by"));
        }
    }
}
