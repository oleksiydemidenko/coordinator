using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace Coordinator.Editor
{
    internal static class CoordinatorGitMemo
    {
        public const string Missing = "\u0000missing";
        private const int MaximumEntries = 20000;
        private static Dictionary<string, string> entries;
        private static bool dirty;

        private static string FilePath =>
            Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath, "Library", "Coordinator", "git-memo.json");

        public static bool TryGet(string key, out string value)
        {
            Load();
            return entries.TryGetValue(key, out value);
        }

        public static void Set(string key, string value)
        {
            Load();
            if (entries.Count >= MaximumEntries)
                entries.Clear();
            entries[key] = value;
            dirty = true;
        }

        public static void Save()
        {
            if (!dirty || entries == null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(entries));
                dirty = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static void Load()
        {
            if (entries != null)
                return;
            entries = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(FilePath))
                    return;
                var stored = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (stored != null)
                    foreach (var pair in stored)
                        entries[pair.Key] = pair.Value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                entries.Clear();
            }
        }
    }
}
