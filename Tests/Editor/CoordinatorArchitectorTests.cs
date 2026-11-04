using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorArchitectorTests
    {
        private CoordinatorTestProject project;

        [SetUp]
        public void SetUp()
        {
            project = new CoordinatorTestProject();
            project.WriteFile("Architector/Groups/group_a.Gameplay.group.json", "{ \"id\": \"group_a\", \"name\": \"Gameplay\", \"parentGroupId\": \"\" }");
            project.WriteFile("Architector/Layers/layer_doors.Doors.layer.json",
                "{ \"layerId\": \"layer_doors\", \"name\": \"Doors\", \"parentGroupId\": \"group_a\", \"scope\": { \"folders\": [ \"Assets/Scripts/Doors\", \"Assets/Scripts/Shared/DoorId.cs\" ] } }");
            project.WriteFile("Architector/Layers/layer_lights.Lights.layer.json",
                "{ \"layerId\": \"layer_lights\", \"name\": \"Lights\", \"parentGroupId\": \"group_a\", \"scope\": { \"folders\": [ \"Assets/Scripts/Lights\" ] } }");
            project.WriteFile("Architector/Links/portal-pair_1.Doors--Lights.link.json",
                "{ \"endpoints\": [ { \"layerId\": \"layer_doors\" }, { \"layerId\": \"layer_lights\" } ] }");
        }

        [TearDown]
        public void TearDown() => project.Dispose();

        [Test]
        public void AZoneLayerResolvesToItsScopeFoldersAndFiles()
        {
            project.Assignments[0].writeZone.Clear();
            project.Assignments[0].layers.Add(new CoordinatorLayerRef { path = "Gameplay/Doors", role = "zone" });
            var workspace = project.Save();

            Assert.That(CoordinatorScope.AuthoredZone(workspace.FindTask("doors").Assignments[0], workspace.Architector),
                Is.EqualTo(new[] { "Assets/Scripts/Doors/**", "Assets/Scripts/Shared/DoorId.cs" }));
            Assert.That(CoordinatorValidation.Validate(workspace).Where(diagnostic => diagnostic.IsError), Is.Empty);
        }

        [Test]
        public void SharedAndConnectedLayersAreWarnedAndUnknownLayersAreErrors()
        {
            project.Assignments[0].layers.Add(new CoordinatorLayerRef { path = "Gameplay/Doors", role = "location" });
            project.Assignments[1].layers.Add(new CoordinatorLayerRef { path = "Gameplay/Lights", role = "location" });
            var extra = CoordinatorTestProject.Assignment("switch-rules", "Assets/Scripts/Switches");
            extra.layers.Add(new CoordinatorLayerRef { path = "Gameplay/Doors", role = "location" });
            extra.layers.Add(new CoordinatorLayerRef { path = "Gameplay/Missing", role = "reads" });
            project.Assignments.Add(extra);

            var codes = CoordinatorValidation.Validate(project.Save()).Select(diagnostic => diagnostic.Code).ToList();

            Assert.That(codes, Does.Contain("layer_shared"));
            Assert.That(codes, Does.Contain("layers_connected"));
            Assert.That(codes, Does.Contain("unknown_layer"));
        }

        [Test]
        public void WithoutArchitectorAZoneLayerCannotBeResolved()
        {
            using var plain = new CoordinatorTestProject();
            plain.Assignments[0].layers.Add(new CoordinatorLayerRef { path = "Gameplay/Doors", role = "zone" });
            plain.Assignments[0].layers.Add(new CoordinatorLayerRef { path = "Gameplay/Doors", role = "reads" });

            var codes = CoordinatorValidation.Validate(plain.Save()).Select(diagnostic => diagnostic.Code).ToList();

            Assert.That(codes, Is.EqualTo(new List<string> { "layer_zone_without_architector" }));
        }
    }
}
