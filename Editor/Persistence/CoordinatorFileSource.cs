using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Coordinator.Editor
{
    // Where the Coordinator and Architector files are read from: the checkout on disk, or a git commit (a read with ref).
    public class CoordinatorFileSource
    {
        public static readonly CoordinatorFileSource Disk = new();

        public virtual bool FileExists(string path) => File.Exists(path);

        public virtual bool DirectoryExists(string path) => Directory.Exists(path);

        public virtual IEnumerable<string> Directories(string path) => Directory.GetDirectories(path);

        // pattern is "*" followed by a suffix, such as "*.json" or "*.layer.json"; only files directly in the folder.
        public virtual IEnumerable<string> Files(string path, string pattern) => Directory.GetFiles(path, pattern);

        public virtual string ReadText(string path) => File.ReadAllText(path);
    }

    // The files of one commit, by repository path, served under the project root as if they were checked out.
    public sealed class CoordinatorTreeSource : CoordinatorFileSource
    {
        private readonly string root;
        private readonly IReadOnlyDictionary<string, string> files;
        private readonly HashSet<string> folders = new(StringComparer.Ordinal);

        public CoordinatorTreeSource(string projectRoot, IReadOnlyDictionary<string, string> repositoryFiles)
        {
            root = Path.GetFullPath(projectRoot);
            files = repositoryFiles;
            foreach (var path in files.Keys)
                for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                    folders.Add(path.Substring(0, slash));
        }

        private string Relative(string path)
        {
            var relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
            return relative == "." ? string.Empty : relative;
        }

        private string Full(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        public override bool FileExists(string path) => files.ContainsKey(Relative(path));

        public override bool DirectoryExists(string path)
        {
            var relative = Relative(path);
            return relative.Length == 0 || folders.Contains(relative);
        }

        public override IEnumerable<string> Directories(string path)
        {
            var prefix = Prefix(path);
            return folders.Where(folder => folder.StartsWith(prefix, StringComparison.Ordinal) && folder.IndexOf('/', prefix.Length) < 0)
                .Select(Full).ToArray();
        }

        public override IEnumerable<string> Files(string path, string pattern)
        {
            var prefix = Prefix(path);
            var suffix = pattern.StartsWith("*", StringComparison.Ordinal) ? pattern.Substring(1) : pattern;
            return files.Keys.Where(file => file.StartsWith(prefix, StringComparison.Ordinal) && file.IndexOf('/', prefix.Length) < 0
                                            && file.EndsWith(suffix, StringComparison.Ordinal))
                .Select(Full).ToArray();
        }

        public override string ReadText(string path) =>
            files.TryGetValue(Relative(path), out var text) && text != null ? text : throw new FileNotFoundException("Not in the commit: " + Relative(path));

        private string Prefix(string path)
        {
            var relative = Relative(path);
            return relative.Length == 0 ? string.Empty : relative + "/";
        }
    }
}
