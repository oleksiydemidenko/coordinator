using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorValidationTests
    {
        private CoordinatorTestProject project;

        [SetUp]
        public void SetUp() => project = new CoordinatorTestProject();

        [TearDown]
        public void TearDown() => project.Dispose();

        private string[] Codes() => CoordinatorValidation.Validate(project.Save()).Select(diagnostic => diagnostic.Code).ToArray();

        [Test]
        public void AWellFormedTaskHasNoDiagnostics()
        {
            Assert.That(CoordinatorValidation.Validate(project.Save()), Is.Empty);
        }

        [Test]
        public void AnExecutorDeclaresWhereItWorks()
        {
            project.Assignments[0].executor.environment = string.Empty;
            project.Assignments[1].executor.environment = "cloud";

            Assert.That(Codes().Count(code => code == "executor_environment"), Is.EqualTo(2));
        }

        [Test]
        public void ATaskBranchIsRequiredAndDiffersFromTheTarget()
        {
            project.Task.integrationBranch = "develop";
            Assert.That(Codes(), Does.Contain("integration_is_target"));

            project.Task.integrationBranch = null;
            Assert.That(Codes(), Does.Contain("missing_field"));
        }

        [Test]
        public void ACheckForAnotherEnvironmentCannotBeAnExecutorsAcceptance()
        {
            project.Project.checks[0].environments = new List<string> { CoordinatorEnvironments.Worktree };
            project.Project.rules.Add(new CoordinatorRuleRecord { id = "no-unity", text = "Do not open Unity.", environments = new List<string>() });
            project.Assignments[1].executor.environment = CoordinatorEnvironments.Editor;

            var codes = Codes();

            Assert.That(codes.Count(code => code == "check_environment"), Is.EqualTo(1));
            Assert.That(codes, Does.Contain("environments_empty"));
        }

        [Test]
        public void FilesRoundTripByteForByte()
        {
            project.Save();
            var path = Path.Combine(project.Paths.TaskFolder("doors"), "door-rules" + CoordinatorPaths.AssignmentSuffix);
            var first = File.ReadAllText(path);
            var loaded = CoordinatorWorkspace.Load(project.Root).FindTask("doors").Assignments.Single(assignment => assignment.id == "door-rules");

            Assert.That(CoordinatorJson.Serialize(loaded), Is.EqualTo(first));
            Assert.That(first, Does.Not.Contain("\r"));
            Assert.That(first, Does.Not.Contain("null"));
        }

        [Test]
        public void OverlappingZonesAreReportedWithBothAssignments()
        {
            project.Assignments[1].writeZone.Add("Assets/Scripts/**");
            project.Assignments[1].outputs[0].paths.Add("Assets/Scripts/**");

            var overlap = CoordinatorValidation.Validate(project.Save()).Where(diagnostic => diagnostic.Code == "zone_overlap").ToArray();

            Assert.That(overlap, Is.Not.Empty);
            Assert.That(overlap[0].Message, Does.Contain("Assets/Scripts/**").And.Contain("doors/light-rules"));
        }

        [Test]
        public void AZoneInsideReservedPathsIsRejectedButReservedFilesInsideAZoneAreNot()
        {
            project.Assignments[0].writeZone.Add("Assets/Core/Doors/**");
            var codes = Codes();

            Assert.That(codes.Count(code => code == "zone_reserved"), Is.EqualTo(1));
        }

        [Test]
        public void OutputsMustLieInsideTheZone()
        {
            project.Assignments[0].outputs[0].paths.Add("Assets/Scripts/Lights/Lamp.cs");

            Assert.That(Codes(), Does.Contain("output_outside_zone"));
        }

        [Test]
        public void ReferencesToUnknownDecisionsOutputsChecksAndVariablesAreErrors()
        {
            var assignment = project.Assignments[0];
            assignment.dependsOn.decisions.Add("missing-decision");
            assignment.inputs.Add(new CoordinatorInputRecord { kind = "output", assignment = "light-rules", output = "missing" });
            assignment.route.Add(new CoordinatorRouteStepRecord { text = "Test.", check = "missing-check" });
            assignment.acceptance.Add(new CoordinatorAcceptanceRecord { id = "run", text = "Runs.", command = "run ${nowhere}" });

            Assert.That(Codes(), Is.SupersetOf(new[] { "unknown_decision", "unknown_output", "unknown_check", "unknown_variable" }));
        }

        [Test]
        public void ADecidedDecisionNeedsItsShortAnswer()
        {
            project.Task.decisions[1].state = "decided";

            Assert.That(Codes(), Does.Contain("decision_without_answer"));
        }

        [Test]
        public void AssignmentsWaitingForEachOtherFormACycle()
        {
            project.Assignments[0].dependsOn.outputs.Add(new CoordinatorOutputRef { assignment = "light-rules", output = "light-rules-code" });
            project.Assignments[1].inputs.Add(new CoordinatorInputRecord { kind = "output", assignment = "door-rules", output = "door-rules-code" });

            var cycles = CoordinatorValidation.Validate(project.Save()).Where(diagnostic => diagnostic.Code == "dependency_cycle").ToArray();

            Assert.That(cycles.Length, Is.EqualTo(1));
            Assert.That(cycles[0].Message, Does.Contain("door-rules -> light-rules -> door-rules"));
        }

        [Test]
        public void MissingSourceInputsAreReportedWhenPathsCanBeChecked()
        {
            project.Assignments[0].inputs.Add(new CoordinatorInputRecord { kind = "source", path = "Assets/Scripts/Identity.cs" });
            var workspace = project.Save();

            Assert.That(CoordinatorValidation.Validate(workspace, path => false).Select(diagnostic => diagnostic.Code), Does.Contain("input_missing"));
            Assert.That(CoordinatorValidation.Validate(workspace, path => true), Is.Empty);
        }

        [Test]
        public void UnknownFieldsBrokenJsonAndMisnamedFilesAreLoadErrors()
        {
            project.Save();
            project.WriteFile("Coordinator/Tasks/doors/door-rules.delivery.json", "{ \"schemaVersion\": 1, \"assignment\": \"door-rules\", \"sate\": \"delivered\" }");
            project.WriteFile("Coordinator/Tasks/doors/light-rules.review.json", "{ \"schemaVersion\": 1, \"assignment\": \"door-rules\" }");
            project.WriteFile("Coordinator/Tasks/doors/notes.json", "{}");

            var codes = CoordinatorValidation.Validate(CoordinatorWorkspace.Load(project.Root)).Select(diagnostic => diagnostic.Code).ToArray();

            Assert.That(codes, Is.SupersetOf(new[] { "invalid_json", "file_name_mismatch", "unknown_file" }));
        }

        [Test]
        public void DeliveriesAndReviewsAreCheckedAgainstTheirAssignment()
        {
            project.Save();
            CoordinatorJson.Write(Path.Combine(project.Paths.TaskFolder("doors"), "door-rules.delivery.json"), new CoordinatorDeliveryRecord
            {
                assignment = "door-rules",
                state = "done",
                checks = new List<CoordinatorCheckResultRecord> { new() { acceptance = "missing", exitCode = 0 } },
                judgments = new List<CoordinatorJudgmentRecord> { new() { text = "Kept the old timer.", step = 4 }, new() { text = "Used ticks.", reason = "The card names no unit.", step = 1 } }
            });
            CoordinatorJson.Write(Path.Combine(project.Paths.TaskFolder("doors"), "door-rules.review.json"), new CoordinatorReviewRecord
            {
                assignment = "door-rules",
                rounds = new List<CoordinatorReviewRoundRecord> { new() { revision = "def5678", verdict = "accepted" } }
            });

            var codes = CoordinatorValidation.Validate(CoordinatorWorkspace.Load(project.Root)).Select(diagnostic => diagnostic.Code).ToArray();

            Assert.That(codes, Is.SupersetOf(new[] { "delivery_state", "unknown_acceptance", "acceptance_without_person", "judgment_incomplete", "judgment_step" }));
            Assert.That(codes.Count(code => code.StartsWith("judgment_")), Is.EqualTo(2));
        }
    }
}
