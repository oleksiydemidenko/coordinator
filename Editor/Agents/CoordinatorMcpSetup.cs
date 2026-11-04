using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Coordinator.Editor
{
    public sealed class CoordinatorMcpClient
    {
        public static readonly CoordinatorMcpClient[] All =
        {
            new("Claude Code", "claude", "mcp add --scope local coordinator -- \"{0}\" --project \"{1}\"", "mcp remove --scope local coordinator"),
            new("Codex", "codex", "mcp add coordinator -- \"{0}\" --project \"{1}\"", "mcp remove coordinator")
        };

        private CoordinatorMcpClient(string displayName, string command, string addArguments, string removeArguments)
        {
            DisplayName = displayName;
            Command = command;
            AddArguments = addArguments;
            RemoveArguments = removeArguments;
        }

        public string DisplayName { get; }
        public string Command { get; }
        public string AddArguments { get; }
        public string RemoveArguments { get; }
    }

    public static class CoordinatorMcpSetup
    {
        private const int CliTimeoutMilliseconds = 30000;
        private const int BuildTimeoutMilliseconds = 300000;

        public static string Version => PackageInfo.FindForAssembly(typeof(CoordinatorMcpSetup).Assembly)?.version ?? "0.0.0";

        public static string PackageRoot => PackageInfo.FindForAssembly(typeof(CoordinatorMcpSetup).Assembly)?.resolvedPath;

        public static string BinaryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coordinator", "mcp", Version, "coordinator-mcp" + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : string.Empty));

        public static bool IsInstalled => File.Exists(BinaryPath);

        public static string ClaudeCommand => $"claude mcp add --scope local coordinator -- \"{BinaryPath}\" --project \"{CoordinatorAgentApi.DefaultRoot}\"";

        public static string CodexCommand => $"codex mcp add coordinator -- \"{BinaryPath}\" --project \"{CoordinatorAgentApi.DefaultRoot}\"";

        public static Task<string> BuildAsync()
        {
            var project = PackageRoot == null ? null : Path.Combine(PackageRoot, "Tools~", "CoordinatorMcp");
            var output = Path.GetDirectoryName(BinaryPath);
            return Task.Run(() =>
            {
                if (project == null || !File.Exists(Path.Combine(project, "CoordinatorMcp.csproj")))
                    return "The server project was not found in the package.";
                var (success, text) = Run("dotnet", $"publish \"{project}\" -c Release -p:Version={Version} -o \"{output}\"", project, BuildTimeoutMilliseconds);
                if (!success)
                    return "dotnet publish failed. The .NET SDK is needed until Coordinator ships prebuilt servers.\n" + text;
                return File.Exists(BinaryPath) ? null : "The build finished but the server binary is missing: " + BinaryPath;
            });
        }

        public static (bool connected, string details) Check(CoordinatorMcpClient client)
        {
            var cli = FindCli(client.Command);
            if (cli == null)
                return (false, $"'{client.Command}' was not found.");
            var (success, output) = Run(cli, "mcp get coordinator", CoordinatorAgentApi.DefaultRoot, CliTimeoutMilliseconds);
            return (success && output.IndexOf(BinaryPath, StringComparison.OrdinalIgnoreCase) >= 0, output);
        }

        public static string Connect(CoordinatorMcpClient client)
        {
            var cli = FindCli(client.Command);
            if (cli == null)
                return $"'{client.Command}' was not found.";
            var root = CoordinatorAgentApi.DefaultRoot;
            Run(cli, client.RemoveArguments, root, CliTimeoutMilliseconds);
            var (success, output) = Run(cli, string.Format(client.AddArguments, BinaryPath, root), root, CliTimeoutMilliseconds);
            return success ? null : output;
        }

        public static string Disconnect(CoordinatorMcpClient client)
        {
            var cli = FindCli(client.Command);
            if (cli == null)
                return $"'{client.Command}' was not found.";
            var (success, output) = Run(cli, client.RemoveArguments, CoordinatorAgentApi.DefaultRoot, CliTimeoutMilliseconds);
            return success ? null : output;
        }

        private static string FindCli(string command)
        {
            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator)
                .Where(folder => folder.Length > 0)
                .Concat(new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                    Path.Combine(home, ".local", "bin"),
                    Path.Combine(home, ".claude", "local"),
                    "/usr/local/bin",
                    "/opt/homebrew/bin"
                });
            var names = windows ? new[] { command + ".exe", command + ".cmd" } : new[] { command };
            foreach (var folder in folders)
            foreach (var name in names)
            {
                var path = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private static (bool success, string output) Run(string file, string arguments, string workingDirectory, int timeout)
        {
            var batch = file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo
            {
                FileName = batch ? "cmd.exe" : file,
                Arguments = batch ? $"/s /c \"\"{file}\" {arguments}\"" : arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                using var process = Process.Start(start);
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    process.Kill();
                    return (false, $"'{file}' did not finish in time.");
                }
                return (process.ExitCode == 0, (output.Result + error.Result).Trim());
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return (false, exception.Message);
            }
        }
    }
}
