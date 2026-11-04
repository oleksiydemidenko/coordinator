using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorBoardTests
    {
        [Test]
        public void ColumnsFollowTheProjectDisciplinesAndCardsGoToTheirFirstDiscipline()
        {
            using var project = new CoordinatorTestProject();
            project.Project.disciplines.Add(new CoordinatorDisciplineRecord { id = "art", title = "Artist" });
            project.Project.disciplines.Add(new CoordinatorDisciplineRecord { id = "sound", title = "Sound" });
            var icon = CoordinatorTestProject.Assignment("door-icon", "Assets/Art/Doors");
            icon.disciplines = new List<string> { "art", "code" };
            var music = CoordinatorTestProject.Assignment("door-music", "Assets/Audio/Doors");
            music.disciplines = new List<string> { "music" };
            project.Assignments.Add(icon);
            project.Assignments.Add(music);
            var workspace = project.Save();

            var columns = CoordinatorBoard.Build(workspace, new Dictionary<string, CoordinatorTaskStatus>(StringComparer.Ordinal));

            Assert.That(columns.Select(column => column.Title), Is.EqualTo(new[] { "Code", "Artist", "Sound", "Other" }));
            Assert.That(columns[0].Cards.Select(card => card.Assignment.id), Is.EqualTo(new[] { "door-rules", "light-rules" }));
            Assert.That(columns[1].Cards.Single().Assignment.id, Is.EqualTo("door-icon"));
            Assert.That(columns[2].Cards, Is.Empty);
            Assert.That(columns[3].Cards.Single().Key, Is.EqualTo("doors/door-music"));
        }

        [Test]
        public void CardsAreOrderedByWorkStateAndFinishedOnesCanBeHidden()
        {
            using var project = new CoordinatorTestProject();
            var workspace = project.Save();
            var status = new CoordinatorTaskStatus { TaskId = "doors" };
            status.Assignments.Add(new CoordinatorAssignmentStatus { AssignmentId = "door-rules", State = CoordinatorAssignmentStates.Closed });
            status.Assignments.Add(new CoordinatorAssignmentStatus { AssignmentId = "light-rules", State = CoordinatorAssignmentStates.InProgress });
            var statuses = new Dictionary<string, CoordinatorTaskStatus>(StringComparer.Ordinal) { ["doors"] = status };

            Assert.That(CoordinatorBoard.Build(workspace, statuses)[0].Cards.Select(card => card.State),
                Is.EqualTo(new[] { CoordinatorAssignmentStates.InProgress, CoordinatorAssignmentStates.Closed }));
            Assert.That(CoordinatorBoard.Build(workspace, statuses, includeFinished: false)[0].Cards.Single().Assignment.id, Is.EqualTo("light-rules"));
            Assert.That(CoordinatorBoard.Build(workspace, statuses, taskId: "other")[0].Cards, Is.Empty);
        }
    }
}
