using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Coordinator.Editor
{
    public static class CoordinatorServerInstall
    {
        private const string Repository = "oleksiydemidenko/coordinator";
        private const string ChecksumsFile = "checksums.txt";

        public static bool IsInstalling { get; private set; }

        public static string Tag => "v" + CoordinatorMcpSetup.Version;

        public static string ServerFileName => FileName(Application.platform, RuntimeInformation.ProcessArchitecture == Architecture.Arm64);

        public static string FileName(RuntimePlatform platform, bool arm)
        {
            var runtime = platform switch
            {
                RuntimePlatform.WindowsEditor => "win-x64.exe",
                RuntimePlatform.OSXEditor => arm ? "osx-arm64" : "osx-x64",
                RuntimePlatform.LinuxEditor => arm ? "linux-arm64" : "linux-x64",
                _ => null
            };
            return runtime == null ? null : "coordinator-mcp-" + runtime;
        }

        public static string Url(string tag, string fileName) =>
            $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";

        public static async Task<string> InstallAsync()
        {
            if (IsInstalling)
                return "A server installation is already in progress.";
            var fileName = ServerFileName;
            if (fileName == null)
                return "This Editor platform has no Coordinator MCP server.";
            IsInstalling = true;
            var target = CoordinatorMcpSetup.BinaryPath;
            var partial = target + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var (checksums, checksumsError) = await GetText(Url(Tag, ChecksumsFile));
                if (checksumsError != null)
                    return $"Release {Tag} of Coordinator has no server to download ({checksumsError}). Servers come with Coordinator releases.";
                var checksum = ReadChecksum(checksums, fileName, out var parseError);
                if (checksum == null)
                    return parseError;
                var downloadError = await Download(Url(Tag, fileName), partial);
                if (downloadError != null)
                    return $"The server download failed: {downloadError}";
                if (!Matches(partial, checksum))
                    return "The downloaded server failed its SHA-256 check. It was not installed.";
                var permissionError = MarkExecutable(partial);
                if (permissionError != null)
                    return permissionError;
                if (File.Exists(target))
                    File.Replace(partial, target, null);
                else
                    File.Move(partial, target);
                File.WriteAllText(target + ".sha256", checksum);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "The server could not be written: " + exception.Message;
            }
            finally
            {
                IsInstalling = false;
                if (File.Exists(partial))
                    File.Delete(partial);
            }
        }

        public static string ReadChecksum(string text, string fileName, out string error)
        {
            error = null;
            string checksum = null;
            foreach (var line in (text ?? string.Empty).Split('\n'))
            {
                var match = Regex.Match(line.TrimEnd('\r'), @"^([a-fA-F0-9]{64})[ \t]+\*?(.+)$");
                if (!match.Success || !string.Equals(match.Groups[2].Value, fileName, StringComparison.Ordinal))
                    continue;
                if (checksum != null)
                {
                    error = $"{ChecksumsFile} lists {fileName} more than once.";
                    return null;
                }
                checksum = match.Groups[1].Value;
            }
            if (checksum == null)
                error = $"{ChecksumsFile} has no SHA-256 checksum for {fileName}.";
            return checksum;
        }

        public static bool Matches(string path, string checksum)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            return string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase);
        }

        public static string InstalledVersion(string binary)
        {
            if (string.IsNullOrEmpty(binary))
                return null;
            var normalized = binary.Replace('\\', '/');
            var root = CoordinatorMcpSetup.ServerRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return null;
            var parts = normalized.Substring(root.Length).Split('/');
            return parts.Length == 2 && parts[1].StartsWith("coordinator-mcp", StringComparison.Ordinal) ? parts[0] : null;
        }

        private static string MarkExecutable(string path)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
                return null;
            using var chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{path}\"") { UseShellExecute = false });
            chmod.WaitForExit();
            return chmod.ExitCode == 0 ? null : "The downloaded server could not be marked executable.";
        }

        private static async Task<(string text, string error)> GetText(string url)
        {
            using var request = UnityWebRequest.Get(url);
            var error = await Send(request);
            return error == null ? (request.downloadHandler.text, null) : (null, error);
        }

        private static async Task<string> Download(string url, string path)
        {
            using var request = UnityWebRequest.Get(url);
            request.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
            return await Send(request);
        }

        private static async Task<string> Send(UnityWebRequest request)
        {
            request.timeout = 120;
            request.SetRequestHeader("User-Agent", "Coordinator-Unity-Editor");
            var completed = new TaskCompletionSource<bool>();
            request.SendWebRequest().completed += _ => completed.TrySetResult(true);
            await completed.Task;
            return request.result == UnityWebRequest.Result.Success ? null : $"HTTP {request.responseCode}: {request.error}";
        }
    }
}
