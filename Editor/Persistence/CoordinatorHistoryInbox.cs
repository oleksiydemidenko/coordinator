using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Coordinator.Editor
{
    // A change made by the command line without the Editor leaves one entry here, Library/Coordinator/history-inbox: its
    // name and every file before and after. The open Editor takes the entries into its Undo history and reloads the window,
    // so a person can undo an agent's edit as one made through the Editor.
    public static class CoordinatorHistoryInbox
    {
        public const string FolderName = "history-inbox";

        public static string Folder(string projectRoot) => Path.Combine(Path.GetFullPath(projectRoot), "Library", "Coordinator", FolderName);

        public static void Write(string projectRoot, string name, IReadOnlyDictionary<string, string> before)
        {
            var entry = new Entry { name = name ?? string.Empty };
            foreach (var pair in before)
                entry.files.Add(new FileChange { path = pair.Key, before = pair.Value, after = File.Exists(pair.Key) ? File.ReadAllText(pair.Key) : null });
            if (entry.files.All(file => file.before == file.after))
                return;
            var folder = Folder(projectRoot);
            Directory.CreateDirectory(folder);
            var stem = DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N");
            var partial = Path.Combine(folder, stem + ".tmp");
            File.WriteAllText(partial, JsonConvert.SerializeObject(entry), new UTF8Encoding(false));
            File.Move(partial, Path.Combine(folder, stem + ".json"));
        }

        // Every finished entry, oldest first; each is deleted once read. An entry that cannot be read is dropped.
        public static IReadOnlyList<Entry> Take(string projectRoot)
        {
            var folder = Folder(projectRoot);
            if (!Directory.Exists(folder))
                return Array.Empty<Entry>();
            var entries = new List<Entry>();
            foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                try
                {
                    var entry = JsonConvert.DeserializeObject<Entry>(File.ReadAllText(file));
                    if (entry?.files != null)
                        entries.Add(entry);
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ignored) when (ignored is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            return entries;
        }

        public static bool HasEntries(string projectRoot)
        {
            var folder = Folder(projectRoot);
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.json").Any();
        }

        public sealed class Entry
        {
            public string name = string.Empty;
            public List<FileChange> files = new();
        }

        public sealed class FileChange
        {
            public string path = string.Empty;
            public string before;
            public string after;
        }
    }
}
