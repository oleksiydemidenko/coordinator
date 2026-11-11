using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Coordinator.Editor
{
    public sealed class CoordinatorGitException : Exception
    {
        public CoordinatorGitException(string message) : base(message)
        {
        }
    }

    public sealed class CoordinatorGit
    {
        private static readonly string[] RefPatterns = { "{0}", "refs/{0}", "refs/tags/{0}", "refs/heads/{0}", "refs/remotes/{0}", "refs/remotes/{0}/HEAD" };
        private readonly Dictionary<string, string> resolved = new(StringComparer.Ordinal);
        private Dictionary<string, string> refSnapshot;

        public CoordinatorGit(string repositoryRoot)
        {
            RepositoryRoot = repositoryRoot;
        }

        public string RepositoryRoot { get; }

        public string RefsFingerprint { get; private set; } = string.Empty;

        public void SnapshotRefs()
        {
            var result = Run(new[] { "for-each-ref", "--format=%(objectname) %(*objectname) %(refname)" });
            refSnapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in result.Output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ');
                if (parts.Length < 3)
                    continue;
                refSnapshot[parts[parts.Length - 1]] = parts[1].Length > 0 ? parts[1] : parts[0];
            }
            RefsFingerprint = result.Output;
        }

        public string Resolve(string revision)
        {
            if (string.IsNullOrEmpty(revision))
                return null;
            if (resolved.TryGetValue(revision, out var commit))
                return commit;
            if (refSnapshot != null)
                foreach (var pattern in RefPatterns)
                    if (refSnapshot.TryGetValue(string.Format(pattern, revision), out commit))
                        return commit;
            var fullId = revision.Length == 40 && revision.All(Uri.IsHexDigit);
            if (fullId && CoordinatorGitMemo.TryGet("commit|" + revision, out commit))
            {
                resolved[revision] = commit;
                return commit;
            }
            var result = Run(new[] { "rev-parse", "--verify", "--quiet", revision + "^{commit}" }, allowFailure: true);
            commit = result.ExitCode == 0 ? result.Output.Trim() : null;
            if (commit != null && fullId)
            {
                resolved[revision] = commit;
                CoordinatorGitMemo.Set("commit|" + revision, commit);
            }
            return commit;
        }

        public bool IsAncestor(string ancestor, string descendant)
        {
            var from = Resolve(ancestor);
            var to = Resolve(descendant);
            if (from == null || to == null)
                return false;
            var key = "ancestor|" + from + "|" + to;
            if (CoordinatorGitMemo.TryGet(key, out var known))
                return known == "1";
            var result = Run(new[] { "merge-base", "--is-ancestor", from, to }, allowFailure: true);
            if (result.ExitCode > 1)
                throw new CoordinatorGitException(result.Error);
            CoordinatorGitMemo.Set(key, result.ExitCode == 0 ? "1" : "0");
            return result.ExitCode == 0;
        }

        public string MergeBase(string left, string right)
        {
            var from = Resolve(left);
            var to = Resolve(right);
            if (from == null || to == null)
                return null;
            var key = "merge-base|" + from + "|" + to;
            if (CoordinatorGitMemo.TryGet(key, out var known))
                return known == CoordinatorGitMemo.Missing ? null : known;
            var result = Run(new[] { "merge-base", from, to }, allowFailure: true);
            if (result.ExitCode > 1)
                throw new CoordinatorGitException(result.Error);
            var commit = result.ExitCode == 0 ? result.Output.Trim() : null;
            CoordinatorGitMemo.Set(key, commit ?? CoordinatorGitMemo.Missing);
            return commit;
        }

        public int CountCommits(string from, string to)
        {
            var start = Resolve(from);
            var end = Resolve(to);
            if (start == null || end == null || start == end)
                return 0;
            var key = "count|" + start + "|" + end;
            if (CoordinatorGitMemo.TryGet(key, out var known) && int.TryParse(known, out var count))
                return count;
            count = int.Parse(Run(new[] { "rev-list", "--count", start + ".." + end }).Output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            CoordinatorGitMemo.Set(key, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return count;
        }

        public bool PathExists(string revision, string path)
        {
            var commit = Resolve(revision);
            if (commit == null)
                return false;
            var key = "exists|" + commit + "|" + path;
            if (CoordinatorGitMemo.TryGet(key, out var known))
                return known == "1";
            var exists = Run(new[] { "cat-file", "-e", commit + ":" + path }, allowFailure: true).ExitCode == 0;
            CoordinatorGitMemo.Set(key, exists ? "1" : "0");
            return exists;
        }

        public string Show(string revision, string path)
        {
            var commit = Resolve(revision);
            if (commit == null)
                return null;
            var key = "show|" + commit + "|" + path;
            if (CoordinatorGitMemo.TryGet(key, out var known))
                return known == CoordinatorGitMemo.Missing ? null : known;
            var result = Run(new[] { "show", commit + ":" + path }, allowFailure: true);
            var text = result.ExitCode == 0 ? result.Output : null;
            CoordinatorGitMemo.Set(key, text ?? CoordinatorGitMemo.Missing);
            return text;
        }

        public IReadOnlyList<string> CommitsTouching(string revision, string path)
        {
            var commit = Resolve(revision);
            if (commit == null)
                return Array.Empty<string>();
            var key = "log|" + commit + "|" + path;
            if (!CoordinatorGitMemo.TryGet(key, out var output))
            {
                output = Run(new[] { "log", "--format=%H", commit, "--", path }).Output;
                CoordinatorGitMemo.Set(key, output);
            }
            return output
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .ToArray();
        }

        public GitResult Run(IReadOnlyList<string> arguments, bool allowFailure = false)
        {
            var start = new ProcessStartInfo("git", string.Join(" ", new[] { "-c", "core.quotepath=off" }.Concat(arguments).Select(Quote)))
            {
                WorkingDirectory = RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var process = Process.Start(start) ?? throw new CoordinatorGitException("git could not be started.");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var result = new GitResult(process.ExitCode, output.Replace("\r\n", "\n"), error.Result.Trim());
            if (!allowFailure && result.ExitCode != 0)
                throw new CoordinatorGitException($"git {string.Join(" ", arguments)} failed: {result.Error}");
            return result;
        }

        private static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return argument;
            var quoted = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var symbol in argument)
            {
                if (symbol == '\\')
                {
                    backslashes++;
                    continue;
                }
                quoted.Append('\\', symbol == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                quoted.Append(symbol);
            }
            quoted.Append('\\', backslashes * 2).Append('"');
            return quoted.ToString();
        }

        public readonly struct GitResult
        {
            public GitResult(int exitCode, string output, string error)
            {
                ExitCode = exitCode;
                Output = output;
                Error = error;
            }

            public int ExitCode { get; }
            public string Output { get; }
            public string Error { get; }
        }
    }
}
