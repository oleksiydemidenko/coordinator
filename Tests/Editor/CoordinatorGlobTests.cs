using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorGlobTests
    {
        [TestCase("Assets/Scripts/Doors/**", "Assets/Scripts/Doors/DoorModule.cs", true)]
        [TestCase("Assets/Scripts/Doors/**", "Assets/Scripts/Doors/Editor/DoorTests.cs", true)]
        [TestCase("Assets/Scripts/Doors/**", "Assets/Scripts/Doors.meta", false)]
        [TestCase("Assets/Scripts/Doors.meta", "Assets/Scripts/Doors.meta", true)]
        [TestCase("Assets/Scripts/*.cs", "Assets/Scripts/Door.cs", true)]
        [TestCase("Assets/Scripts/*.cs", "Assets/Scripts/Doors/Door.cs", false)]
        [TestCase("**/*.unity", "Game.unity", true)]
        [TestCase("**/*.unity", "Assets/Scenes/Game.unity", true)]
        [TestCase("**/*.unity", "Assets/Scenes/Game.unity.meta", false)]
        [TestCase("Tests/Door?/**", "Tests/Doors/Cases.cs", true)]
        public void MatchFollowsGitGlobPathspecs(string pattern, string path, bool expected)
        {
            Assert.That(CoordinatorGlob.Match(pattern, path), Is.EqualTo(expected));
        }

        [TestCase("Assets/Scripts/Doors/**", "Assets/Scripts/Lights/**", false)]
        [TestCase("Assets/Scripts/Doors/**", "Assets/Scripts/**", true)]
        [TestCase("Assets/Scripts/Doors/**", "**/*.asmdef", true)]
        [TestCase("Assets/Scripts/Doors.meta", "Assets/Scripts/Doors/**", false)]
        [TestCase("Assets/*/Doors/**", "Assets/Scripts/Doors/Door.cs", true)]
        [TestCase("Assets/Scripts/*.cs", "Assets/Scripts/*.meta", false)]
        [TestCase("Docs/Door*.md", "Docs/*Delivery.md", true)]
        public void OverlapsWhenSomePathCouldMatchBoth(string left, string right, bool expected)
        {
            Assert.That(CoordinatorGlob.Overlaps(left, right), Is.EqualTo(expected));
            Assert.That(CoordinatorGlob.Overlaps(right, left), Is.EqualTo(expected));
        }

        [TestCase("Assets/Scripts/Doors/**", true)]
        [TestCase("/Assets/Doors", false)]
        [TestCase("Assets\\Doors", false)]
        [TestCase("Assets/../Doors", false)]
        [TestCase("Assets/**Doors", false)]
        [TestCase("Assets/[ab]", false)]
        [TestCase("Assets/It's", false)]
        public void OnlyTheSupportedSyntaxIsAccepted(string pattern, bool valid)
        {
            Assert.That(CoordinatorGlob.Problem(pattern).Length == 0, Is.EqualTo(valid));
        }
    }
}
