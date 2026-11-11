using System.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorScopeTests
    {
        [Test]
        public void CommandsCompareCommitsWithTheTaskBranchExcludeTheZoneAndListReservedChanges()
        {
            using var project = new CoordinatorTestProject();
            var commands = CoordinatorScope.Commands(project.Project, project.Task, project.Assignments[0]);
            const string zone = "':(exclude,glob)Assets/Scripts/Doors/**' ':(exclude,glob)Assets/Scripts/Doors.meta' ':(exclude,glob)Coordinator/Tasks/doors/door-rules.delivery.json'";
            const string reserved = "':(glob)**/*.unity' ':(glob)Assets/Core/**'";

            Assert.That(commands, Is.EqualTo(new[]
            {
                "git merge-base --is-ancestor abc1234 integration/doors || echo \"The task branch integration/doors is missing or does not contain the task base: ask the lead. Until the branch exists, use abc1234...HEAD in the commands below.\"",
                "git diff --no-renames --name-only integration/doors...HEAD -- . " + zone,
                "git diff --no-renames --name-only integration/doors...HEAD -- " + reserved
            }));
            Assert.That(string.Join("\n", commands.Skip(1)), Does.Not.Contain("abc1234"), "Only the first line names the base, so a moved base cannot make the diff stale.");
        }

        [Test]
        public void WithoutACommitTheBaseBranchIsNamedInTheFirstLine()
        {
            using var project = new CoordinatorTestProject();
            project.Task.baseRevision.commit = null;

            Assert.That(CoordinatorScope.Commands(project.Project, project.Task, project.Assignments[0]).First(),
                Does.StartWith("git merge-base --is-ancestor coordination/doors-start integration/doors"));
        }
    }
}
