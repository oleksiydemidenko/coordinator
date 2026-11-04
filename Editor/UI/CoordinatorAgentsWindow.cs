using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Coordinator.Editor
{
    public sealed class CoordinatorAgentsWindow : EditorWindow
    {
        private readonly Dictionary<string, (bool connected, string details)> clients = new();
        private Task<string> build;
        private string message = string.Empty;
        private Vector2 scroll;

        public static void Open()
        {
            var window = GetWindow<CoordinatorAgentsWindow>(true, "Coordinator Agents");
            window.minSize = new Vector2(520f, 320f);
            window.RefreshClients();
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
            if (build is not { IsCompleted: true })
                return;
            message = build.Result ?? "Server built: " + CoordinatorMcpSetup.BinaryPath;
            build = null;
            RefreshClients();
            Repaint();
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
            EditorGUILayout.LabelField("MCP Server", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Version", CoordinatorMcpSetup.Version);
            EditorGUILayout.LabelField("Binary", CoordinatorMcpSetup.IsInstalled ? CoordinatorMcpSetup.BinaryPath : "Not built");
            EditorGUILayout.LabelField("Editor bridge", CoordinatorAgentBridge.Port > 0 ? "Listening on port " + CoordinatorAgentBridge.Port : "Not running");
            using (new EditorGUI.DisabledScope(build != null))
                if (GUILayout.Button(build != null ? "Building…" : CoordinatorMcpSetup.IsInstalled ? "Rebuild Server" : "Build Server"))
                {
                    message = string.Empty;
                    build = CoordinatorMcpSetup.BuildAsync();
                }
            EditorGUILayout.HelpBox("Until Coordinator has releases, the server is built from the package with the .NET SDK. The server only forwards to this open Editor.", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Clients", EditorStyles.boldLabel);
            foreach (var client in CoordinatorMcpClient.All)
            {
                clients.TryGetValue(client.DisplayName, out var state);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(client.DisplayName, state.connected ? "Connected" : "Not connected");
                    using (new EditorGUI.DisabledScope(!CoordinatorMcpSetup.IsInstalled || build != null))
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

        private void Report(string error, string success)
        {
            message = error ?? success;
            RefreshClients();
        }
    }
}
