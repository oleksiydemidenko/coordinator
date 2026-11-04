using System.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorScopeTests
    {
        [Test]
        public void CommandsExcludeTheZoneAndTheDeliveryFileAndListReservedChanges()
        {
            using var project = new CoordinatorTestProject();
            var commands = CoordinatorScope.Commands(project.Project, project.Task, project.Assignments[0]);
            const string zone = "':(exclude,glob)Assets/Scripts/Doors/**' ':(exclude,glob)Assets/Scripts/Doors.meta' ':(exclude,glob)Coordinator/Tasks/doors/door-rules.delivery.json'";
            const string reserved = "':(glob)**/*.unity' ':(glob)Assets/Core/**'";

            Assert.That(commands, Is.EqualTo(new[]
            {
                "git merge-base --is-ancestor abc1234 HEAD || echo \"base is not an ancestor of HEAD\"",
                "git diff --no-renames --name-only abc1234 -- . " + zone,
                "git ls-files --others --exclude-standard -- . " + zone,
                "git diff --no-renames --name-only abc1234 -- " + reserved,
                "git ls-files --others --exclude-standard -- " + reserved
            }));
        }

        [Test]
        public void WithoutACommitTheBaseBranchIsUsed()
        {
            using var project = new CoordinatorTestProject();
            project.Task.baseRevision.commit = null;

            Assert.That(CoordinatorScope.Commands(project.Project, project.Task, project.Assignments[0]).First(),
                Does.StartWith("git merge-base --is-ancestor coordination/doors-start HEAD"));
        }
    }
}
