using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Coordinator.Editor
{
    // The person's workflow mode, per user and project (EditorPrefs), as in Architector. The Editor mirrors it to
    // Library/Coordinator/agent-mode.json, which CoordinatorAgentApi reads in the Editor and in the command line. The mirror
    // is written again when the Editor loads and on every window refresh, so a hand-edited file does not last.
    [InitializeOnLoad]
    internal static class CoordinatorWorkflowModePreference
    {
        static CoordinatorWorkflowModePreference()
        {
            EditorApplication.delayCall += Mirror;
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        private static string Key
        {
            get
            {
                var root = Path.GetFullPath(ProjectRoot).Replace('\\', '/').TrimEnd('/');
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(root));
                return $"Coordinator.{BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty)}.WorkflowMode";
            }
        }

        public static CoordinatorWorkflowMode Current => CoordinatorWorkflowModes.Find(EditorPrefs.GetString(Key, CoordinatorWorkflowModes.Safe));

        public static void Set(CoordinatorWorkflowMode mode)
        {
            EditorPrefs.SetString(Key, mode.Id);
            Mirror();
        }

        public static void Mirror()
        {
            if (!CoordinatorWorkflowModes.Write(ProjectRoot, Current))
                Debug.LogWarning($"Coordinator could not write {CoordinatorWorkflowModes.PathFor(ProjectRoot)}; agents see the Safe mode.");
        }
    }
}
