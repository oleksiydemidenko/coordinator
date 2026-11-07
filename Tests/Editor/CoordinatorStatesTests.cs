using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorStatesTests
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
            project.WriteFile("Assets/Scripts/Identity.cs", "class Identity {}");
            Commit("base");
            project.Task.baseRevision.commit = Git("rev-parse", "HEAD").Trim();
            project.Task.approval = new CoordinatorSignatureRecord { by = "owner", date = "2026-11-04" };
            project.Assignments[1].dependsOn.decisions.Add("light-timing");
            project.Save();
            Commit("task");
        }

        [TearDown]
        public void TearDown() => project.Dispose();

        private string Git(params string[] arguments) => git.Run(arguments).Output;

        private void Commit(string message)
        {
            Git("add", "-A");
            Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "--allow-empty", "-m", message);
        }

        private CoordinatorTaskStatus States() =>
            CoordinatorStates.Compute(CoordinatorWorkspace.Load(project.Root).FindTask("doors"), git);

        private void Deliver(string state)
        {
            CoordinatorJson.Write(Path.Combine(project.Root, CoordinatorPaths.RepositoryDeliveryPath("doors", "door-rules")),
                new CoordinatorDeliveryRecord { assignment = "door-rules", state = state });
            Commit("door " + state);
        }

        private void Review(string revision, string verdict)
        {
            var path = Path.Combine(project.Paths.TaskFolder("doors"), "door-rules" + CoordinatorPaths.ReviewSuffix);
            var review = File.Exists(path) ? CoordinatorJson.Parse<CoordinatorReviewRecord>(File.ReadAllText(path)) : new CoordinatorReviewRecord { assignment = "door-rules" };
            review.rounds.Add(new CoordinatorReviewRoundRecord { revision = revision, date = "2026-11-04", verdict = verdict, acceptedBy = verdict == "accepted" ? "lead" : null });
            CoordinatorJson.Write(path, review);
            Commit("review " + verdict);
        }

        [Test]
        public void StatesFollowTheExecutorBranchAndTheLeadsReviews()
        {
            var states = States();
            Assert.That(states.State, Is.EqualTo(CoordinatorTaskStates.Approved));
            Assert.That(states.Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Ready));
            Assert.That(states.Find("light-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Blocked));
            Assert.That(states.Find("light-rules").BlockedBy.Single(), Does.Contain("light-timing"));

            Git("checkout", "-q", "-b", "task/door-rules");
            Deliver("inProgress");
            project.WriteFile("Assets/Scripts/Doors/Door.cs", "class Door {}");
            Deliver("delivered");
            var first = Git("rev-parse", "HEAD").Trim();
            Git("checkout", "-q", "develop");
            Assert.That(States().Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Delivered));
            Assert.That(States().Find("door-rules").DeliveredRevision, Is.EqualTo(first));
            Assert.That(States().State, Is.EqualTo(CoordinatorTaskStates.InWork));

            Review(first.Substring(0, 10), "returned");
            Assert.That(States().Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Returned));

            Git("checkout", "-q", "task/door-rules");
            Deliver("inProgress");
            Git("checkout", "-q", "develop");
            Assert.That(States().Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.InProgress));

            Git("checkout", "-q", "task/door-rules");
            Deliver("delivered");
            var second = Git("rev-parse", "HEAD").Trim();
            Git("checkout", "-q", "develop");
            Assert.That(States().Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Delivered));

            Review(second, "accepted");
            Assert.That(States().Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Accepted));

            Git("branch", "integration/doors", project.Task.baseRevision.commit);
            MergeInto("integration/doors", "task/door-rules");
            Assert.That(States().Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Merged));

            MergeInto("develop", "integration/doors");
            Assert.That(States().Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Closed));
        }

        [Test]
        public void ADependentCardWaitsUntilTheAcceptedOutputIsInTheTaskBranch()
        {
            project.Assignments[1].dependsOn.decisions.Clear();
            project.Assignments[1].dependsOn.outputs.Add(new CoordinatorOutputRef { assignment = "door-rules", output = "door-rules-code" });
            project.Save();
            Commit("light waits for doors");
            Git("branch", "integration/doors");
            Assert.That(States().Find("light-rules").BlockedBy.Single(), Does.Contain("is not accepted"));

            Git("checkout", "-q", "-b", "task/door-rules");
            project.WriteFile("Assets/Scripts/Doors/Door.cs", "class Door {}");
            Deliver("delivered");
            var delivered = Git("rev-parse", "HEAD").Trim();
            Git("checkout", "-q", "develop");
            Review(delivered, "accepted");
            Assert.That(States().Find("light-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Blocked));
            Assert.That(States().Find("light-rules").BlockedBy.Single(), Does.Contain("not merged into the task branch"));

            MergeInto("integration/doors", "task/door-rules");
            Assert.That(States().Find("door-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Merged));
            Assert.That(States().Find("light-rules").State, Is.EqualTo(CoordinatorAssignmentStates.Ready));
        }

        private void MergeInto(string target, string source)
        {
            Git("checkout", "-q", target);
            Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "-q", "--no-ff", "--no-edit", source);
            Git("checkout", "-q", "develop");
        }

        [Test]
        public void SourceInputsAreCheckedAtTheTaskBase()
        {
            project.Assignments[0].inputs.Add(new CoordinatorInputRecord { kind = "source", path = "Assets/Scripts/Identity.cs" });
            project.Assignments[0].inputs.Add(new CoordinatorInputRecord { kind = "source", path = "Assets/Scripts/Later.cs" });
            project.Save();
            project.WriteFile("Assets/Scripts/Later.cs", "class Later {}");
            Commit("later");

            var missing = CoordinatorValidation.Validate(CoordinatorWorkspace.Load(project.Root), git)
                .Where(diagnostic => diagnostic.Code == "input_missing").ToArray();

            Assert.That(missing.Length, Is.EqualTo(1));
            Assert.That(missing[0].Message, Does.Contain("Later.cs"));
        }

        [Test]
        public void AnInvalidDeliveryOnTheBranchIsReportedNotTrusted()
        {
            Git("checkout", "-q", "-b", "task/door-rules");
            project.WriteFile(CoordinatorPaths.RepositoryDeliveryPath("doors", "door-rules"), "{ \"assignment\": \"door-rules\", \"state\": ");
            Commit("broken");
            Git("checkout", "-q", "develop");

            var status = States().Find("door-rules");

            Assert.That(status.State, Is.EqualTo(CoordinatorAssignmentStates.Ready));
            Assert.That(status.Problem, Does.Contain("invalid"));
        }
    }
}
