using System.IO;
using UnityEditor;
using UnityEngine;

namespace Coordinator.Editor
{
    [InitializeOnLoad]
    public static class CoordinatorServerMaintenance
    {
        static CoordinatorServerMaintenance()
        {
            if (!Application.isBatchMode)
                EditorApplication.delayCall += Run;
        }

        private static string MarkerPath => Path.Combine(CoordinatorAgentApi.DefaultRoot, "Library", "Coordinator", "server-version.txt");

        public static async void Run()
        {
            var version = CoordinatorMcpSetup.Version;
            if (File.Exists(MarkerPath) && File.ReadAllText(MarkerPath).Trim() == version)
                return;
            foreach (var client in CoordinatorMcpClient.All)
            {
                var registered = CoordinatorServerInstall.InstalledVersion(CoordinatorMcpSetup.RegisteredBinary(client));
                if (registered == null || registered == version)
                    continue;
                if (!CoordinatorMcpSetup.IsInstalled)
                {
                    var error = await CoordinatorServerInstall.InstallAsync();
                    if (error != null)
                    {
                        Debug.LogWarning($"Coordinator: {client.DisplayName} still uses server {registered}; the {version} server could not be installed. {error}");
                        return;
                    }
                }
                var connectError = CoordinatorMcpSetup.Connect(client);
                if (connectError != null)
                {
                    Debug.LogWarning($"Coordinator: {client.DisplayName} could not be moved to server {version}. {connectError}");
                    return;
                }
                Debug.Log($"Coordinator: {client.DisplayName} now uses server {version}. Restart it to load the server.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
            File.WriteAllText(MarkerPath, version);
        }
    }
}
