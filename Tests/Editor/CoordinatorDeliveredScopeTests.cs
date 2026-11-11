using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorDeliveredScopeTests
    {
        private const string TaskBranch = "integration/doors";
        private CoordinatorTestProject project;
        private CoordinatorGit git;
        private string baseCommit;

        [SetUp]
        public void SetUp()
        {
            project = new CoordinatorTestProject();
            Directory.CreateDirectory(project.Root);
            git = new CoordinatorGit(project.Root);
            Git("init", "-q", "-b", "develop");
            project.WriteFile("Assets/Scripts/Identity.cs", "class Identity {}");
            Commit("base");
            baseCommit = Git("rev-parse", "HEAD").Trim();
            project.Task.baseRevision.commit = baseCommit;
            project.Task.approval = new CoordinatorSignatureRecord { by = "owner", date = "2026-11-11" };
            project.Assignments[1].dependsOn.outputs.Add(new CoordinatorOutputRef { assignment = "door-rules", output = "door-rules-code" });
            project.Save();
            Commit("task");
            Git("branch", TaskBranch);
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

        private string Deliver(string assignment, params string[] files)
        {
            foreach (var file in files)
                project.WriteFile(file, "class X {}");
            CoordinatorJson.Write(Path.Combine(project.Root, CoordinatorPaths.RepositoryDeliveryPath("doors", assignment)),
                new CoordinatorDeliveryRecord { assignment = assignment, state = CoordinatorDeliveryStates.Delivered });
            Commit(assignment + " delivered");
            return Git("rev-parse", "HEAD").Trim();
        }

        private void MergeInto(string target, string source)
        {
            Git("checkout", "-q", target);
            Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "-q", "--no-ff", "--no-edit", source);
            Git("checkout", "-q", "develop");
        }

        private CoordinatorScopeReport Check(string assignment, string revision) =>
            CoordinatorScope.Check(git, project.Project, project.Task, project.Assignments.Single(candidate => candidate.id == assignment), revision);

        private CoordinatorAssignmentStatus Status(string assignment) =>
            CoordinatorStates.Compute(CoordinatorWorkspace.Load(project.Root).FindTask("doors"), git).Find(assignment);

        private CoordinatorAgentResult Edit(object parameters) => CoordinatorAgentApi.Edit(JObject.FromObject(parameters), project.Root);

        [Test]
        public void ADependentCardStartedFromTheTaskBranchTipIsClean()
        {
            Git("checkout", "-q", "-b", "task/door-rules", TaskBranch);
            Deliver("door-rules", "Assets/Scripts/Doors/Door.cs");
            MergeInto(TaskBranch, "task/door-rules");
            Git("checkout", "-q", "-b", "task/light-rules", TaskBranch);
            var light = Deliver("light-rules", "Assets/Scripts/Lights/Lamp.cs");
            Git("checkout", "-q", "develop");

            var report = Check("light-rules", light);

            Assert.That(report.Clean, Is.True, string.Join(", ", report.OutsideZone));
            Assert.That(report.StartIsBase, Is.False);
            Assert.That(report.Start, Is.EqualTo(Git("rev-parse", TaskBranch).Trim()));
            Assert.That(Git("diff", "--name-only", baseCommit, light), Does.Contain("Assets/Scripts/Doors/Door.cs"),
                "Compared with the task base, the inherited producer work would be reported.");
        }

        [Test]
        public void ABranchStartedOutsideTheTaskBranchReportsWhatItCarries()
        {
            project.WriteFile("Assets/Scripts/Other.cs", "class Other {}");
            Commit("develop moves on");
            Git("checkout", "-q", "-b", "task/door-rules");
            var door = Deliver("door-rules", "Assets/Scripts/Doors/Door.cs");
            Git("checkout", "-q", "develop");

            var report = Check("door-rules", door);

            Assert.That(report.Clean, Is.False);
            Assert.That(report.OutsideZone, Does.Contain("Assets/Scripts/Other.cs"));
        }

        [Test]
        public void MovingTheTaskBaseAfterTheBranchStartedKeepsItClean()
        {
            Git("checkout", "-q", "-b", "task/door-rules", TaskBranch);
            var door = Deliver("door-rules", "Assets/Scripts/Doors/Door.cs");
            Git("checkout", "-q", "develop");
            project.WriteFile("Assets/Scripts/Other.cs", "class Other {}");
            Commit("develop moves on");
            var moved = Git("rev-parse", "HEAD").Trim();
            MergeInto(TaskBranch, "develop");
            project.Task.baseRevision.commit = moved;

            var report = Check("door-rules", door);

            Assert.That(report.Clean, Is.True, string.Join(", ", report.OutsideZone));
            Assert.That(Git("diff", "--name-only", moved, door), Does.Contain("Assets/Scripts/Other.cs"),
                "Compared with the moved base, the newer develop work would be reported.");
        }

        [Test]
        public void UncommittedFilesAreListedApartFromCleanCommits()
        {
            var worktree = project.Root + "-door-rules";
            Git("worktree", "add", "-q", "-b", "task/door-rules", worktree, TaskBranch);
            try
            {
                var executor = new CoordinatorGit(worktree);
                File.WriteAllText(Path.Combine(worktree, "Assets/Scripts/Identity.cs"), "class Identity { int changed; }");
                Directory.CreateDirectory(Path.Combine(worktree, "Assets/Scripts/Doors"));
                File.WriteAllText(Path.Combine(worktree, "Assets/Scripts/Doors/Door.cs"), "class Door {}");
                File.WriteAllText(Path.Combine(worktree, "Assets/Scripts/Doors/Draft.cs"), "class Draft {}");
                File.WriteAllText(Path.Combine(worktree, "Notes.txt"), "notes");
                executor.Run(new[] { "add", "Assets/Scripts/Doors/Door.cs" });
                executor.Run(new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "door" });

                var report = JObject.Parse(CoordinatorAgentApi.Query(JObject.FromObject(new { action = "scope", task = "doors", assignment = "door-rules", checkout = worktree }), project.Root).ToJson())["data"];

                Assert.That((bool)report["delivered"]["clean"], Is.True, report.ToString());
                Assert.That(report["uncommitted"]["outside_zone"].Values<string>(), Is.EqualTo(new[] { "Assets/Scripts/Identity.cs", "Notes.txt" }));
                Assert.That(report["uncommitted"]["inside_zone"].Values<string>(), Is.EqualTo(new[] { "Assets/Scripts/Doors/Draft.cs" }));
            }
            finally
            {
                Directory.Delete(worktree, true);
            }
        }

        [Test]
        public void CommitsAfterTheDeliveryAreShownBeforeAndAfterTheMerge()
        {
            Git("checkout", "-q", "-b", "task/door-rules", TaskBranch);
            var door = Deliver("door-rules", "Assets/Scripts/Doors/Door.cs");
            project.WriteFile("Assets/Scripts/Doors/Later.cs", "class Later {}");
            Commit("later source change");
            Git("checkout", "-q", "develop");

            Assert.That(Status("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Delivered));
            Assert.That(Status("door-rules").Problem, Does.StartWith("1 commit(s) after the delivered revision are not reviewed"));

            var review = new CoordinatorReviewRecord { assignment = "door-rules" };
            review.rounds.Add(new CoordinatorReviewRoundRecord { revision = door, date = "2026-11-11", verdict = CoordinatorVerdicts.Accepted, acceptedBy = "lead", instructedBy = "owner" });
            CoordinatorJson.Write(Path.Combine(project.Paths.TaskFolder("doors"), "door-rules" + CoordinatorPaths.ReviewSuffix), review);
            Commit("accept");
            Assert.That(Status("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Accepted));
            Assert.That(Status("door-rules").Problem, Does.Contain("merge the accepted revision"));

            MergeInto(TaskBranch, "task/door-rules");

            Assert.That(Status("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Merged));
            Assert.That(Status("door-rules").Problem, Does.StartWith("Merged past the accepted revision: 1 commit(s)"));
        }

        [Test]
        public void AcceptanceIsRefusedWhenTheDeliveredCommitsLeaveTheZone()
        {
            Git("checkout", "-q", "-b", "task/door-rules", TaskBranch);
            var door = Deliver("door-rules", "Assets/Scripts/Doors/Door.cs", "Assets/Scripts/Lights/Lamp.cs");
            Git("checkout", "-q", "develop");
            Assert.That(Edit(new { action = "record_review", task = "doors", assignment = "door-rules", round = new { revision = door, verdict = "returned" } }).Success, Is.True);
            var path = Path.Combine(project.Paths.TaskFolder("doors"), "door-rules" + CoordinatorPaths.ReviewSuffix);
            var before = File.ReadAllText(path);

            var accepted = Edit(new { action = "record_review", task = "doors", assignment = "door-rules", round = new { revision = door, verdict = "accepted", acceptedBy = "lead", instructedBy = "owner" } });

            Assert.That(accepted.Success, Is.False);
            Assert.That(accepted.Code, Is.EqualTo("scope_failed"));
            Assert.That(accepted.Message, Does.Contain("Assets/Scripts/Lights/Lamp.cs"));
            Assert.That(File.ReadAllText(path), Is.EqualTo(before));
        }

        [Test]
        public void AnOutOfZonePathMakesTheReportNotCleanAlthoughGitExitsWithZero()
        {
            Git("checkout", "-q", "-b", "task/door-rules", TaskBranch);
            var door = Deliver("door-rules", "Assets/Scripts/Lights/Lamp.cs");
            Git("checkout", "-q", "develop");

            var report = Check("door-rules", door);
            var diff = git.Run(new[] { "diff", "--no-renames", "--name-only", report.Start, door, "--", ".", ":(exclude,glob)Assets/Scripts/Doors/**" });

            Assert.That(diff.ExitCode, Is.Zero);
            Assert.That(report.OutsideZone, Is.EqualTo(new[] { "Assets/Scripts/Lights/Lamp.cs" }));
            Assert.That(report.Clean, Is.False);
        }

        [Test]
        public void WithoutATaskBranchTheBaseIsTheStartAndTheCardsFirstLineSaysSo()
        {
            Git("branch", "-D", TaskBranch);
            Git("checkout", "-q", "-b", "task/door-rules", baseCommit);
            var door = Deliver("door-rules", "Assets/Scripts/Doors/Door.cs");
            Git("checkout", "-q", "develop");

            var report = Check("door-rules", door);
            var firstLine = CoordinatorScope.Commands(project.Project, project.Task, project.Assignments[0]).First();

            Assert.That(report.StartIsBase, Is.True);
            Assert.That(report.Start, Is.EqualTo(baseCommit));
            Assert.That(report.Clean, Is.True, string.Join(", ", report.OutsideZone));
            Assert.That(firstLine, Does.Contain($"The task branch {TaskBranch} is missing").And.Contain($"use {baseCommit}...HEAD"));
        }
    }
}
