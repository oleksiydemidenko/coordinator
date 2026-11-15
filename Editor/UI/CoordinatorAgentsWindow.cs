using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Coordinator.Editor
{
    public sealed class CoordinatorAgentsWindow : EditorWindow
    {
        private readonly Dictionary<string, (bool connected, string details)> clients = new();
        private Task<string> install;
        private Task<CliState> cliCheck;
        private CliState cli;
        private string message = string.Empty;
        private Vector2 scroll;
        private GUIStyle tagStyle;

        public static void Open()
        {
            var window = GetWindow<CoordinatorAgentsWindow>(true, "Coordinator Agents");
            window.minSize = new Vector2(520f, 320f);
            window.RefreshClients();
            window.CheckCli();
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += Poll;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Poll;
        }

        private void Poll()
        {
            if (cliCheck is { IsCompleted: true })
            {
                cli = cliCheck.Result;
                cliCheck = null;
                Repaint();
            }
            if (install is not { IsCompleted: true })
                return;
            message = install.Result ?? "Server installed: " + CoordinatorMcpSetup.BinaryPath;
            install = null;
            RefreshClients();
            CheckCli();
            Repaint();
        }

        // The command line is checked as an agent uses it: the installed binary runs "status" in a background process,
        // so the window opens at once and the answer comes when the binary has looked for the Editor bridge itself.
        private void CheckCli()
        {
            if (cliCheck != null)
                return;
            var binary = CoordinatorMcpSetup.BinaryPath;
            var root = CoordinatorAgentApi.DefaultRoot;
            cliCheck = Task.Run(() => CliState.Read(binary, root));
        }

        private void RefreshClients()
        {
            clients.Clear();
            foreach (var client in CoordinatorMcpClient.All)
                clients[client.DisplayName] = CoordinatorMcpSetup.Check(client);
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            Heading("MCP", McpColor, "Coordinator MCP server");
            EditorGUILayout.LabelField("Version", CoordinatorMcpSetup.Version);
            EditorGUILayout.LabelField("Binary", CoordinatorMcpSetup.IsInstalled ? CoordinatorMcpSetup.BinaryPath : "Not installed");
            EditorGUILayout.LabelField("Editor bridge", CoordinatorAgentBridge.Port > 0 ? "Listening on port " + CoordinatorAgentBridge.Port : "Not running");
            using (new EditorGUI.DisabledScope(install != null))
                if (GUILayout.Button(install != null ? "Installing…" : CoordinatorMcpSetup.IsInstalled ? "Reinstall Server" : "Install Server"))
                {
                    message = string.Empty;
                    install = CoordinatorServerInstall.InstallAsync();
                }
            EditorGUILayout.HelpBox($"The server is downloaded from Coordinator release {CoordinatorServerInstall.Tag} and checked against its SHA-256. When the package version changes, connected agents move to the matching server by themselves. The Coordinator MCP server and the Coordinator CLI are the same binary.", MessageType.None);

            EditorGUILayout.Space();
            DrawCli();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Clients", EditorStyles.boldLabel);
            foreach (var client in CoordinatorMcpClient.All)
            {
                clients.TryGetValue(client.DisplayName, out var state);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(client.DisplayName, state.connected ? "Connected" : "Not connected");
                    using (new EditorGUI.DisabledScope(!CoordinatorMcpSetup.IsInstalled || install != null))
                    {
                        if (GUILayout.Button(state.connected ? "Reconnect" : "Connect", GUILayout.Width(90f)))
                            Report(CoordinatorMcpSetup.Connect(client), client.DisplayName + " connected. Restart it to load the server.");
                        if (state.connected && GUILayout.Button("Disconnect", GUILayout.Width(90f)))
                            Report(CoordinatorMcpSetup.Disconnect(client), client.DisplayName + " disconnected.");
                    }
                }
            }
            if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(70f)))
                RefreshClients();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Manual registration", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(CoordinatorMcpSetup.ClaudeCommand, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.SelectableLabel(CoordinatorMcpSetup.CodexCommand, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.HelpBox("Agents also need the coordinator-workflow skill from the package's Skills~ folder in the project's skills folder.", MessageType.None);

            if (message.Length > 0)
                EditorGUILayout.HelpBox(message, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void DrawCli()
        {
            Heading("CLI", CliColor, "Coordinator CLI");
            var installed = CoordinatorMcpSetup.IsInstalled;
            var binary = CoordinatorMcpSetup.BinaryPath.Replace('\\', '/');
            EditorGUILayout.LabelField("Binary", installed ? binary : "Not installed (Install Server above)");
            EditorGUILayout.LabelField("Version", cliCheck != null ? "Checking…" : cli?.Version ?? (installed ? "No answer" : "-"));
            EditorGUILayout.LabelField("Editor bridge", cliCheck != null ? "Checking…"
                : cli == null ? "-"
                : cli.EditorConnected ? "Answers the command line"
                : "No answer: " + cli.Error);
            EditorGUILayout.LabelField("Without the Editor", cli?.ReadsWithoutEditor == true
                ? "Queries answer from the Coordinator files and git; edits wait for the Editor."
                : "Every call needs this Editor.");
            using (new EditorGUI.DisabledScope(!installed))
            {
                Example("Shell", $"\"{binary}\" query board");
                Example("PowerShell", $"& \"{binary}\" query board");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!installed || cliCheck != null))
                    if (GUILayout.Button("Check", EditorStyles.miniButton, GUILayout.Width(70f)))
                        CheckCli();
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.HelpBox("Agents with a shell run the binary as a command line; it needs no MCP registration. \"help\" lists the commands. Run it in the project or in a git worktree of it, or pass --project. While this Editor compiles or reloads, a call waits for it (--wait, default 60 s).", MessageType.None);
        }

        private void Example(string label, string command)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                EditorGUILayout.SelectableLabel(command, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    EditorGUIUtility.systemCopyBuffer = command;
                    message = "Copied: " + command;
                }
            }
        }

        // The MCP and CLI tags have the colours of the Architector integrations, so both packages show them the same way.
        private static Color McpColor => EditorGUIUtility.isProSkin ? new Color(0.40f, 0.74f, 1.00f) : new Color(0.00f, 0.36f, 0.68f);

        private static Color CliColor => EditorGUIUtility.isProSkin ? new Color(1.00f, 0.84f, 0.34f) : new Color(0.58f, 0.42f, 0.00f);

        private void Heading(string tag, Color color, string title)
        {
            tagStyle ??= new GUIStyle(EditorStyles.boldLabel) { richText = true };
            EditorGUILayout.LabelField($"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{tag}</color>  {title}", tagStyle);
        }

        private sealed class CliState
        {
            private const int TimeoutMilliseconds = 15000;

            public string Version;
            public bool EditorConnected;
            public bool ReadsWithoutEditor;
            public string Error = string.Empty;

            public static CliState Read(string binary, string root)
            {
                if (!File.Exists(binary))
                    return null;
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(binary, $"status --project \"{root}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    var output = process.StandardOutput.ReadToEndAsync();
                    process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(TimeoutMilliseconds))
                    {
                        process.Kill();
                        return new CliState { Error = "the binary did not finish in time." };
                    }
                    var status = JObject.Parse(output.Result);
                    var connected = (bool?)status["editor_connected"] ?? false;
                    return new CliState
                    {
                        Version = (string)status["version"],
                        EditorConnected = connected,
                        ReadsWithoutEditor = (bool?)status["reads_without_editor"] ?? false,
                        Error = connected ? string.Empty : "the binary found no bridge for this project."
                    };
                }
                catch (Exception exception)
                {
                    return new CliState { Error = exception.Message };
                }
            }
        }

        private void Report(string error, string success)
        {
            message = error ?? success;
            RefreshClients();
        }
    }
}
