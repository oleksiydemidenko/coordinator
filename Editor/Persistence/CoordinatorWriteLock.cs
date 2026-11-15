using System;
using System.IO;
using System.Threading;

namespace Coordinator.Editor
{
    // One writer at a time for the Coordinator files of a project: the open Editor (the window and its bridge) and every
    // command line process take the same lock file, Library/Coordinator/write.lock, for a whole change: read, change,
    // write, validate and roll back. The operating system releases the lock when its process ends, also after a crash.
    public static class CoordinatorWriteLock
    {
        public const string FileName = "write.lock";

        // How long a second writer waits for the first. A Coordinator change takes well under a second.
        public static int WaitMilliseconds = 10000;

        private const int RetryMilliseconds = 50;

        [ThreadStatic] private static int depth;
        [ThreadStatic] private static FileStream held;

        public static string LockPath(string projectRoot) => Path.Combine(Path.GetFullPath(projectRoot), "Library", "Coordinator", FileName);

        public static T Run<T>(string projectRoot, Func<T> change)
        {
            if (depth > 0)
            {
                depth++;
                try
                {
                    return change();
                }
                finally
                {
                    depth--;
                }
            }
            held = Acquire(LockPath(projectRoot));
            depth = 1;
            try
            {
                return change();
            }
            finally
            {
                depth = 0;
                held.Dispose();
                held = null;
            }
        }

        public static void Run(string projectRoot, Action change) => Run(projectRoot, () =>
        {
            change();
            return true;
        });

        private static FileStream Acquire(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var waited = 0;
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (waited < WaitMilliseconds)
                {
                    Thread.Sleep(RetryMilliseconds);
                    waited += RetryMilliseconds;
                }
                catch (IOException)
                {
                    throw new CoordinatorAgentException("busy",
                        $"Another Coordinator writer holds {FileName} for more than {WaitMilliseconds / 1000.0:0.#} s (the Editor or another command line). Nothing was changed; repeat the edit.");
                }
            }
        }
    }
}
