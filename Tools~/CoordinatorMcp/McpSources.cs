using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Coordinator.Editor;

// Where an answer comes from. Every call tries the open Unity Editor first. While Unity runs for the project but its bridge
// does not answer (a recompile, a domain reload), the call waits for it. A read that still has no Editor is answered
// from the Coordinator files and git by the package's own model code; an edit always needs the Editor.
public static partial class McpServer
{
    // A domain reload of a large project keeps the bridge away for about 30 s (28 s measured on christmas-smile-friend), so the
    // default leaves room for a slower one.
    private const int DefaultWaitSeconds = 60;
    private const int RetryMilliseconds = 500;

    private enum BridgeResult
    {
        Answered,
        Unreachable,
        Lost
    }

    private sealed record Policy(int WaitSeconds, string Source);

    private static readonly Policy DefaultPolicy = new(DefaultWaitSeconds, "auto");

    // The linked git worktree the call was made in, when the project was moved to its main checkout.
    private static string? checkout;

    // An executor's worktree has no Library and no Editor: Coordinator lives in the main checkout, where the lead's Editor
    // runs and the Coordinator files are current. The worktree stays the checkout that scope checks by default.
    private static string? ResolveProject(string? found)
    {
        checkout = null;
        if (found == null)
            return null;
        var main = MainWorktree(found);
        if (main == null || !Directory.Exists(Path.Combine(main, "Coordinator"))
            || !Directory.Exists(Path.Combine(main, "Assets")) || !Directory.Exists(Path.Combine(main, "ProjectSettings")))
            return found;
        checkout = found;
        return main;
    }

    // A linked worktree has a .git file "gitdir: <common>/worktrees/<name>" whose folder names the common git folder in
    // "commondir"; the main checkout is the folder that holds that common .git folder. No git process is started.
    private static string? MainWorktree(string folder)
    {
        try
        {
            var dotGit = Path.Combine(folder, ".git");
            if (!File.Exists(dotGit))
                return null;
            var line = File.ReadAllText(dotGit).Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                return null;
            var gitDir = Path.GetFullPath(Path.Combine(folder, line["gitdir:".Length..].Trim()));
            var commonFile = Path.Combine(gitDir, "commondir");
            if (!File.Exists(commonFile))
                return null;
            var common = new DirectoryInfo(Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim())));
            if (common.Name != ".git" || common.Parent == null)
                return null;
            var main = common.Parent.FullName;
            return string.Equals(Path.GetFullPath(main).TrimEnd('\\', '/'), Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? null
                : main;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // Unity writes Library/EditorInstance.json while an Editor has the project open.
    private static bool EditorRunning()
    {
        try
        {
            var file = Path.Combine(root!, "Library", "EditorInstance.json");
            if (!File.Exists(file))
                return false;
            var id = (int?)JsonNode.Parse(File.ReadAllText(file))?["process_id"];
            if (id == null)
                return false;
            using var process = Process.GetProcessById(id.Value);
            return !process.HasExited && process.ProcessName.Contains("Unity", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // The answer as JSON with "source" (editor or files), or null with the reason when there is none.
    private static (JsonObject? Reply, string Reason) Answer(string tool, JsonObject arguments, Policy policy)
    {
        if (checkout != null && tool == "query" && (string?)arguments["action"] == "scope"
            && arguments["checkout"] == null && arguments["revision"] == null)
            arguments["checkout"] = checkout.Replace('\\', '/');
        if (policy.Source == "files")
            return tool == "query"
                ? (Files(arguments, "files were asked for"), string.Empty)
                : (null, "An edit goes through the Unity Editor; --source files is for queries.");

        var watch = Stopwatch.StartNew();
        var result = TryBridge(tool, arguments, out var answer);
        var waited = false;
        while (result != BridgeResult.Answered && !(result == BridgeResult.Lost && tool == "edit")
               && watch.Elapsed.TotalSeconds < policy.WaitSeconds && EditorRunning())
        {
            waited = true;
            Thread.Sleep(RetryMilliseconds);
            result = TryBridge(tool, arguments, out answer);
        }
        if (result == BridgeResult.Answered)
        {
            var reply = JsonNode.Parse(answer!)!.AsObject();
            reply["source"] = "editor";
            if (waited)
                reply["waited_seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1);
            return (reply, string.Empty);
        }
        if (result == BridgeResult.Lost && tool == "edit")
            return (null, "The Unity Editor took the edit but gave no answer; it may have started a reload. Check the result with a query before you repeat the edit.");
        var running = EditorRunning();
        var reason = running
            ? $"The Unity Editor runs for this project but its bridge gave no answer in {policy.WaitSeconds} s (compiling, reloading or paused)."
            : NotRunning;
        if (tool == "query" && policy.Source == "auto")
            return (Files(arguments, running ? $"the Editor did not answer in {policy.WaitSeconds} s" : "no Unity Editor runs for this project"), string.Empty);
        return (null, reason);
    }

    // A read answered without Unity: the same CoordinatorAgentApi.Query the Editor runs, on the files of this project.
    private static JsonObject Files(JsonObject arguments, string why)
    {
        CoordinatorGitMemo.FilePath = Path.Combine(root!, "Library", "Coordinator", "git-memo.json");
        var parameters = Newtonsoft.Json.Linq.JObject.Parse(arguments.ToJsonString());
        var reply = JsonNode.Parse(CoordinatorAgentApi.Query(parameters, root).ToJson())!.AsObject();
        reply["source"] = "files";
        reply["source_reason"] = why;
        return reply;
    }

    private static BridgeResult TryBridge(string tool, JsonNode? arguments, out string? answer)
    {
        answer = null;
        JsonNode bridge;
        try
        {
            if (!File.Exists(BridgeState))
                return BridgeResult.Unreachable;
            bridge = JsonNode.Parse(File.ReadAllText(BridgeState))!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The Editor rewrites the file after a reload; a half-written file is the same as none.
            return BridgeResult.Unreachable;
        }
        TcpClient client;
        try
        {
            client = new TcpClient("127.0.0.1", (int)bridge["port"]!);
        }
        catch (Exception exception) when (exception is SocketException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return BridgeResult.Unreachable;
        }
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var writer = new StreamWriter(stream, Utf8) { AutoFlush = true, NewLine = "\n" })
            using (var reader = new StreamReader(stream, Utf8))
            {
                writer.WriteLine(new JsonObject { ["token"] = (string?)bridge["token"], ["tool"] = tool, ["parameters"] = arguments?.DeepClone() ?? new JsonObject() }.ToJsonString());
                answer = reader.ReadLine();
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            answer = null;
        }
        return answer == null ? BridgeResult.Lost : BridgeResult.Answered;
    }
}
