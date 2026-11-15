using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Coordinator.Editor.Tests
{
    // The command line compiles the package's Editor code without Unity (Tools~/CoordinatorCore) to answer reads from the
    // files. Every file it compiles must keep its Unity references inside #if UNITY_EDITOR, or the server build breaks.
    public sealed class CoordinatorCliSourceTests
    {
        private static readonly Regex UnityReference = new(@"\b(UnityEngine|UnityEditor|EditorApplication|SessionState|EditorPrefs|AssetDatabase)\b|\bApplication\.");

        [Test]
        public void FilesSharedWithTheCommandLineGuardTheirUnityReferences()
        {
            var package = CoordinatorMcpSetup.PackageRoot;
            Assert.That(package, Is.Not.Null);
            var project = File.ReadAllText(Path.Combine(package, "Tools~", "CoordinatorCore", "CoordinatorCore.csproj"));
            var exclude = Regex.Match(project, "Exclude=\"([^\"]*)\"").Groups[1].Value
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Replace("../../", string.Empty).Replace('\\', '/'))
                .ToArray();
            Assert.That(exclude, Does.Contain("Editor/UI/**"));

            var shared = Directory.GetFiles(Path.Combine(package, "Editor"), "*.cs", SearchOption.AllDirectories)
                .Select(path => path.Substring(package.Length + 1).Replace('\\', '/'))
                .Where(path => !exclude.Any(pattern => pattern.EndsWith("/**", StringComparison.Ordinal)
                    ? path.StartsWith(pattern.Substring(0, pattern.Length - 2), StringComparison.Ordinal)
                    : path == pattern))
                .ToArray();
            Assert.That(shared, Does.Contain("Editor/Agents/CoordinatorAgentApi.cs"));

            var unguarded = shared.SelectMany(path => Unguarded(path, File.ReadAllLines(Path.Combine(package, path)))).ToArray();
            Assert.That(unguarded, Is.Empty, "Guard these with #if UNITY_EDITOR or exclude the file in CoordinatorCore.csproj.");
        }

        [Test]
        public void TheScannerSeesOnlyUnguardedReferences()
        {
            var lines = new[]
            {
                "#if UNITY_EDITOR", "using UnityEngine;", "#else", "using System;", "#endif",
                "var root = Application.dataPath;", "// The Unity Editor, in words, is fine."
            };
            Assert.That(Unguarded("File.cs", lines), Is.EqualTo(new[] { "File.cs:6" }));
        }

        private static IEnumerable<string> Unguarded(string path, IReadOnlyList<string> lines)
        {
            // Each open #if records whether its current branch is the UNITY_EDITOR one.
            var blocks = new Stack<(bool editor, bool active)>();
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index].Trim();
                if (line.StartsWith("#if", StringComparison.Ordinal))
                {
                    var editor = Regex.IsMatch(line, @"^#if\s+UNITY_EDITOR\b");
                    blocks.Push((editor, editor));
                    continue;
                }
                if (line.StartsWith("#else", StringComparison.Ordinal) || line.StartsWith("#elif", StringComparison.Ordinal))
                {
                    var block = blocks.Pop();
                    blocks.Push((block.editor, false));
                    continue;
                }
                if (line.StartsWith("#endif", StringComparison.Ordinal))
                {
                    blocks.Pop();
                    continue;
                }
                if (line.StartsWith("//", StringComparison.Ordinal) || blocks.Any(block => block.active))
                    continue;
                if (UnityReference.IsMatch(line))
                    yield return $"{path}:{index + 1}";
            }
        }
    }
}
