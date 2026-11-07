using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Coordinator.Editor.Tests
{
    public sealed class CoordinatorServerInstallTests
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void EachEditorPlatformMapsToItsReleaseAsset()
        {
            Assert.That(CoordinatorServerInstall.FileName(RuntimePlatform.WindowsEditor, false), Is.EqualTo("coordinator-mcp-win-x64.exe"));
            Assert.That(CoordinatorServerInstall.FileName(RuntimePlatform.OSXEditor, true), Is.EqualTo("coordinator-mcp-osx-arm64"));
            Assert.That(CoordinatorServerInstall.FileName(RuntimePlatform.LinuxEditor, false), Is.EqualTo("coordinator-mcp-linux-x64"));
            Assert.That(CoordinatorServerInstall.FileName(RuntimePlatform.Android, false), Is.Null);
            Assert.That(CoordinatorServerInstall.Url("v0.1.0", "checksums.txt"),
                Is.EqualTo("https://github.com/oleksiydemidenko/coordinator/releases/download/v0.1.0/checksums.txt"));
        }

        [Test]
        public void TheChecksumIsReadForExactlyOneAsset()
        {
            var text = $"{Hash}  coordinator-mcp-linux-x64\r\n{Hash.ToUpperInvariant()} *coordinator-mcp-win-x64.exe\n";

            Assert.That(CoordinatorServerInstall.ReadChecksum(text, "coordinator-mcp-win-x64.exe", out var error), Is.EqualTo(Hash.ToUpperInvariant()));
            Assert.That(error, Is.Null);
            Assert.That(CoordinatorServerInstall.ReadChecksum(text, "coordinator-mcp-osx-x64", out error), Is.Null);
            Assert.That(error, Does.Contain("no SHA-256"));
            Assert.That(CoordinatorServerInstall.ReadChecksum(text + $"{Hash}  coordinator-mcp-linux-x64\n", "coordinator-mcp-linux-x64", out error), Is.Null);
            Assert.That(error, Does.Contain("more than once"));
        }

        [Test]
        public void ADownloadMatchesOnlyItsOwnChecksum()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "abc");
                Assert.That(CoordinatorServerInstall.Matches(path, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"), Is.True);
                Assert.That(CoordinatorServerInstall.Matches(path, Hash), Is.False);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void OnlyServersInTheVersionFoldersCountAsInstalledVersions()
        {
            var root = CoordinatorMcpSetup.ServerRoot;

            Assert.That(CoordinatorServerInstall.InstalledVersion(Path.Combine(root, "0.1.0", "coordinator-mcp.exe")), Is.EqualTo("0.1.0"));
            Assert.That(CoordinatorServerInstall.InstalledVersion(Path.Combine(root, "0.1.0", "other.exe")), Is.Null);
            Assert.That(CoordinatorServerInstall.InstalledVersion("C:/Tools/coordinator-mcp.exe"), Is.Null);
            Assert.That(CoordinatorServerInstall.InstalledVersion(null), Is.Null);
        }
    }
}
