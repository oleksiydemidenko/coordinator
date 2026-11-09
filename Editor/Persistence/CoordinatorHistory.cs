using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;

namespace Coordinator.Editor
{
    public static class CoordinatorHistory
    {
        private const string SessionKey = "Coordinator.History";
        private const int MaximumEntries = 50;
        private static State state;

        public static event Action Changed;

        public static bool CanUndo => Current.undo.Count > 0;
        public static bool CanRedo => Current.redo.Count > 0;
        public static string NextUndoName => CanUndo ? Current.undo[Current.undo.Count - 1].name : string.Empty;
        public static string NextRedoName => CanRedo ? Current.redo[Current.redo.Count - 1].name : string.Empty;

        private static State Current
        {
            get
            {
                if (state != null)
                    return state;
                try
                {
                    state = JsonConvert.DeserializeObject<State>(SessionState.GetString(SessionKey, string.Empty));
                }
                catch (JsonException)
                {
                    state = null;
                }
                state ??= new State();
                return state;
            }
        }

        public static void Record(string name, IReadOnlyDictionary<string, string> before)
        {
            var entry = new Entry { name = ShortName(name) };
            foreach (var pair in before)
                entry.files.Add(new FileChange { path = pair.Key, before = pair.Value, after = File.Exists(pair.Key) ? File.ReadAllText(pair.Key) : null });
            if (entry.files.All(file => file.before == file.after))
                return;
            Current.undo.Add(entry);
            if (Current.undo.Count > MaximumEntries)
                Current.undo.RemoveAt(0);
            Current.redo.Clear();
            Save();
        }

        public static string Undo() => Move(Current.undo, Current.redo, true);

        public static string Redo() => Move(Current.redo, Current.undo, false);

        public static void Clear()
        {
            Current.undo.Clear();
            Current.redo.Clear();
            Save();
        }

        private static string Move(List<Entry> from, List<Entry> to, bool undo)
        {
            if (from.Count == 0)
                return undo ? "Nothing to undo." : "Nothing to redo.";
            var entry = from[from.Count - 1];
            var changed = entry.files
                .Where(file => !string.Equals(File.Exists(file.path) ? File.ReadAllText(file.path) : null, undo ? file.after : file.before, StringComparison.Ordinal))
                .Select(file => file.path)
                .ToArray();
            if (changed.Length > 0)
                return $"Cannot {(undo ? "undo" : "redo")} '{entry.name}': changed since then outside this history: {string.Join(", ", changed.Select(Path.GetFileName))}. The history keeps it; the files stay as they are.";
            foreach (var file in entry.files)
            {
                var content = undo ? file.before : file.after;
                if (content == null)
                {
                    if (File.Exists(file.path))
                        File.Delete(file.path);
                    var folder = Path.GetDirectoryName(file.path);
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.path) ?? ".");
                    File.WriteAllText(file.path, content, new System.Text.UTF8Encoding(false));
                }
            }
            from.RemoveAt(from.Count - 1);
            to.Add(entry);
            Save();
            return $"{(undo ? "Undone" : "Redone")}: {entry.name}";
        }

        private static string ShortName(string message)
        {
            var text = (message ?? string.Empty).Trim();
            var end = text.IndexOf(". ", StringComparison.Ordinal);
            text = end > 0 ? text.Substring(0, end) : text.TrimEnd('.');
            return text.Length == 0 ? "Coordinator change" : text;
        }

        private static void Save()
        {
            SessionState.SetString(SessionKey, JsonConvert.SerializeObject(Current));
            Changed?.Invoke();
        }

        private sealed class State
        {
            public List<Entry> undo = new();
            public List<Entry> redo = new();
        }

        private sealed class Entry
        {
            public string name = string.Empty;
            public List<FileChange> files = new();
        }

        private sealed class FileChange
        {
            public string path = string.Empty;
            public string before;
            public string after;
        }
    }
}
