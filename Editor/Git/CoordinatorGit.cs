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
        private readonly Dictionary<string, string> resolved = new(StringComparer.Ordinal);

        public CoordinatorGit(string repositoryRoot)
        {
            RepositoryRoot = repositoryRoot;
        }

        public string RepositoryRoot { get; }

        public string Resolve(string revision)
        {
            if (string.IsNullOrEmpty(revision))
                return null;
            if (resolved.TryGetValue(revision, out var commit))
                return commit;
            var result = Run(new[] { "rev-parse", "--verify", "--quiet", revision + "^{commit}" }, allowFailure: true);
            commit = result.ExitCode == 0 ? result.Output.Trim() : null;
            if (commit != null && revision.Length == 40 && revision.All(Uri.IsHexDigit))
                resolved[revision] = commit;
            return commit;
        }

        public bool IsAncestor(string ancestor, string descendant)
        {
            var from = Resolve(ancestor);
            var to = Resolve(descendant);
            if (from == null || to == null)
                return false;
            var result = Run(new[] { "merge-base", "--is-ancestor", from, to }, allowFailure: true);
            if (result.ExitCode > 1)
                throw new CoordinatorGitException(result.Error);
            return result.ExitCode == 0;
        }

        public bool PathExists(string revision, string path)
        {
            var commit = Resolve(revision);
            return commit != null && Run(new[] { "cat-file", "-e", commit + ":" + path }, allowFailure: true).ExitCode == 0;
        }

        public string Show(string revision, string path)
        {
            var commit = Resolve(revision);
            if (commit == null)
                return null;
            var result = Run(new[] { "show", commit + ":" + path }, allowFailure: true);
            return result.ExitCode == 0 ? result.Output : null;
        }

        public IReadOnlyList<string> CommitsTouching(string revision, string path)
        {
            var commit = Resolve(revision);
            if (commit == null)
                return Array.Empty<string>();
            return Run(new[] { "log", "--format=%H", commit, "--", path }).Output
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
