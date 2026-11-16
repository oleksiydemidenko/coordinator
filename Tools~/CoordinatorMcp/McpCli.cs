using System.Text.Json;
using System.Text.Json.Nodes;

// The coordinator command line: the same binary as the MCP server, the same Editor bridge, the same JSON replies.
// Agents that can run shell commands use it without a registered MCP server; clients without a shell keep MCP.
public static partial class McpServer
{
    // Exit codes: 0 success, 1 internal error, 2 usage error, 4 setup missing (no Unity project),
    // 6 the operation answered with success=false, 7 the Unity Editor is not reachable with --source editor after the wait,
    // or it took an edit and gave no answer (check with a query before you repeat it).
    private const int ExitOk = 0;
    private const int ExitInternal = 1;
    private const int ExitUsage = 2;
    private const int ExitSetup = 4;
    private const int ExitFailed = 6;
    private const int ExitEditor = 7;

    private sealed record CliCommand(string Name, string Usage, string Summary, bool NeedsEditor, string[] Options);

    private static readonly CliCommand[] CliCommands =
    {
        new("query", "query <action> [--<parameter> <value>]...",
            "Any coordinator_query action: capabilities, tasks, board, task, card, validate, scope. "
            + "For example: query board --include_closed false, query board --state Delivered, query task --task SLB-002, query card --task SLB-002 --assignment A1, "
            + "query scope --task SLB-002 --assignment A1 --checkout F:/path/to/worktree, query validate --task SLB-002. "
            + "--ref <branch, tag or commit> reads the Coordinator files of that commit instead of the checkout (query board --ref develop); such a read comes from git, never from the Editor. "
            + "The open Unity Editor answers when its bridge answers now; otherwise the same query is answered at once from the Coordinator files and git "
            + "(no Editor, a recompile, a domain reload). The reply's source is editor or files. "
            + "--source editor waits for the Editor instead, --wait seconds (default " + DefaultWaitSeconds + "), and never reads the files; --source files never asks the Editor. "
            + "In a linked git worktree the project is its main checkout, and scope without --checkout or --revision checks the worktree.",
            false, new[] { "task", "assignment", "ref", "state", "checkout", "revision", "include_closed", "wait", "source" }),
        new("edit", "edit <action> [--<parameter> <value|file.json|JSON>]...",
            "Any coordinator_edit action as the lead: create_task, update_task, set_decision, set_assignment, remove_assignment, "
            + "cancel_assignment, record_review, generate_cards, approve_task. A record parameter (--task for create_task and update_task, --assignment for set_assignment, "
            + "--decision, --round) takes a .json file or JSON text in the format of the coordinator-workflow skill. "
            + "An edit that adds validation errors is rolled back and answers validation_failed with the errors; query validate checks the data without a change. "
            + "approve_task follows the workflow mode a person picks in the Coordinator window: in Lazy mode the lead agent approves a draft whose validate is clean "
            + "(edit approve_task --task <task> --by <agent> --instructed_by <person>); in Safe and Free mode only a person approves, in the window. "
            + "The open Unity Editor runs the edit when its bridge answers now; otherwise the command line runs the same edit on the Coordinator files at once, "
            + "with the same checks and rollback, and the open Editor takes it into its Undo history and reloads the window. "
            + "One writer at a time: a second writer waits up to 10 s for the write lock, then answers busy. "
            + "--source editor waits for the Editor instead (--wait seconds, default " + DefaultWaitSeconds + "), then exit 7; --source files never asks the Editor.",
            false, new[] { "task", "assignment", "decision", "round", "by", "instructed_by", "reason", "wait", "source" }),
        new("status", "status", "The project, its Coordinator data, whether the Unity Editor is reachable, the workflow mode and the path of this binary.", false, Array.Empty<string>()),
        new("commands", "commands", "Every command as JSON, for tools.", false, Array.Empty<string>()),
        new("help", "help [<command>]", "This text, or one command.", false, Array.Empty<string>())
    };

    private static readonly string[] CliWords = { "query", "edit", "status", "commands", "help", "--help", "-h" };

    private static bool IsCliCommand(string word) => CliWords.Contains(word);

    // A command line call has a command word among its positional arguments, wherever the options stand
    // ("--project X status" as well as "status --project X"). The MCP registration passes the project as
    // "--project <path>" or as a bare folder, so a positional word that is neither a command nor an existing folder is a
    // mistyped command, answered as one: the binary never starts the silent server for it.
    private static bool IsCliInvocation(string[] args)
    {
        if (args.Length > 0 && IsCliCommand(args[0]))
            return true;
        var (positional, _) = ParseArguments(args);
        if (positional.Count == 0)
            return false;
        return IsCliCommand(positional[0]) || !Directory.Exists(positional[0]);
    }

    private static bool pretty;

    private static int RunCli(string[] args)
    {
        // Replies are UTF-8 on every platform: the Windows console code page would turn names and dashes into question marks.
        try
        {
            Console.OutputEncoding = Utf8;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
        }
        var (positional, options) = ParseArguments(args);
        pretty = options.ContainsKey("pretty");
        if (positional.Count == 0 || positional[0] is "help" or "--help" or "-h" || options.ContainsKey("help"))
            return Help(positional.Where(word => word is not ("help" or "--help" or "-h")).ToList());
        var project = options.TryGetValue("project", out var projectValues) ? projectValues.Last() : null;
        root = ResolveProject(FindProject(project == null ? Array.Empty<string>() : new[] { "--project", project }));
        var code = RunCliCommand(positional, options);
        Console.Out.Flush();
        return code;
    }

    private static int RunCliCommand(List<string> positional, Dictionary<string, List<string>> options)
    {
        try
        {
            if (positional[0] == "commands")
                return Emit(new JsonObject
                {
                    ["success"] = true,
                    ["version"] = Version,
                    ["commands"] = Nodes(CliCommands.Select(command => (JsonNode?)new JsonObject
                    {
                        ["command"] = command.Name,
                        ["usage"] = "coordinator-mcp " + command.Usage,
                        ["summary"] = command.Summary,
                        ["needs_editor"] = command.NeedsEditor,
                        ["options"] = Nodes(command.Options.Select(option => (JsonNode?)option))
                    })),
                    ["exit_codes"] = new JsonObject
                    {
                        ["0"] = "success", ["1"] = "internal error", ["2"] = "usage", ["4"] = "setup missing",
                        ["6"] = "success=false", ["7"] = "Unity Editor not reachable with --source editor, or an edit sent without an answer"
                    }
                }, ExitOk);
            if (root == null)
                return Fail("no_project", "No Unity project here. Run inside a Unity project or pass --project <path>.", ExitSetup);
            switch (positional[0])
            {
                case "status":
                    return Emit(JsonNode.Parse(Status())!, ExitOk);
                case "query":
                case "edit":
                    return CliBridge(positional[0], positional, options);
            }
            return Fail("unknown_command", $"Unknown command '{positional[0]}'. Commands: {string.Join(", ", CliCommands.Select(command => command.Name))}.", ExitUsage);
        }
        catch (Exception exception)
        {
            // Every failure is reported as JSON with exit code 1; the command line never ends without output.
            return Fail("internal_error", $"{exception.GetType().Name}: {exception.Message}", ExitInternal);
        }
    }

    private static int CliBridge(string tool, List<string> positional, Dictionary<string, List<string>> options)
    {
        if (positional.Count < 2)
            return Fail("usage", $"Usage: coordinator-mcp {tool} <action> [--<parameter> <value>]...", ExitUsage);
        if (positional.Count > 2)
            return Fail("usage", $"Unexpected '{positional[2]}'. Parameters are named: coordinator-mcp {tool} {positional[1]} --<parameter> <value>.", ExitUsage);
        var policy = DefaultPolicy;
        if (options.TryGetValue("wait", out var wait))
        {
            if (wait.Count > 1 || !int.TryParse(wait[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return Fail("usage", "--wait takes the number of seconds once, for example --wait 0.", ExitUsage);
            policy = policy with { WaitSeconds = seconds };
        }
        if (options.TryGetValue("source", out var source))
        {
            if (source.Count > 1 || source[0] is not ("auto" or "editor" or "files"))
                return Fail("usage", "--source is auto, editor or files.", ExitUsage);
            policy = policy with { Source = source[0] };
        }
        var arguments = new JsonObject { ["action"] = positional[1] };
        foreach (var (name, values) in options)
        {
            if (name is "project" or "pretty" or "wait" or "source")
                continue;
            if (values.Count > 1)
                return Fail("usage", $"--{name} is given {values.Count} times; give it once.", ExitUsage);
            JsonNode? value;
            try
            {
                value = ParseValue(values[0]);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                return Fail("usage", $"--{name}: {exception.Message}", ExitUsage);
            }
            arguments[name.Replace('-', '_')] = value;
        }
        var (reply, reason) = Answer(tool, arguments, policy);
        if (reply == null)
            return Fail("editor_unreachable", reason, ExitEditor);
        return Emit(reply, (bool?)reply["success"] == true ? ExitOk : ExitFailed);
    }

    // true and false are booleans; a .json file that exists, or text that starts with { or [, is JSON; everything else,
    // numbers included, stays text, because ids such as 001 must not lose their zeros.
    private static JsonNode? ParseValue(string value)
    {
        if (value is "true" or "false")
            return value == "true";
        if (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(value))
            return JsonNode.Parse(File.ReadAllText(value, Utf8));
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return JsonNode.Parse(value);
        return value;
    }

    private static int Help(List<string> topic)
    {
        var selected = topic.Count > 0 && CliCommands.FirstOrDefault(command => command.Name == topic[0]) is { } one
            ? new[] { one }
            : CliCommands;
        var text = new System.Text.StringBuilder();
        if (selected.Length > 1)
        {
            text.AppendLine($"coordinator-mcp {Version}: Coordinator for agents and scripts. Without a command it is the MCP server.");
            text.AppendLine();
        }
        foreach (var command in selected)
        {
            text.AppendLine("coordinator-mcp " + command.Usage);
            text.AppendLine("  " + command.Summary + (command.NeedsEditor ? " Needs the open Unity Editor." : string.Empty));
            if (command.Options.Length > 0)
                text.AppendLine("  Options: " + string.Join(", ", command.Options.Select(option => "--" + option)) + ".");
        }
        text.AppendLine();
        text.AppendLine("Global: --project <path> (default: the Unity project containing the current folder; a linked git worktree means its main checkout), --pretty (indented JSON).");
        text.AppendLine("The Editor: a call goes to the open Unity Editor when its bridge answers now, otherwise to the Coordinator files at once; the reply's source says which. "
                        + $"With --source editor a call waits up to --wait <seconds> (default {DefaultWaitSeconds}) while Unity runs for the project but its bridge does not answer.");
        text.AppendLine("A value true or false is a boolean, a .json file or text that starts with { or [ is JSON, anything else is text. --name=value works too.");
        text.AppendLine("Every reply is one line of JSON with success. Exit codes: 0 success, 1 internal error, 2 usage, 4 setup missing, 6 success=false, 7 Unity Editor not reachable with --source editor, or an edit sent to it without an answer.");
        Console.Out.Write(text.ToString());
        return ExitOk;
    }

    private static (List<string> Positional, Dictionary<string, List<string>> Options) ParseArguments(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg == "--help" && index == 0)
            {
                positional.Add(arg);
                continue;
            }
            var name = arg[2..];
            string value;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) && name is not ("pretty" or "help"))
                value = args[++index];
            else
                value = "true";
            if (!options.TryGetValue(name, out var values))
                options[name] = values = new List<string>();
            values.Add(value);
        }
        return (positional, options);
    }

    private static JsonArray Nodes(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add(item);
        return array;
    }

    private static int Emit(JsonNode reply, int exitCode)
    {
        Console.Out.WriteLine(reply.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = pretty,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        return exitCode;
    }

    private static int Fail(string code, string message, int exitCode) =>
        Emit(new JsonObject { ["success"] = false, ["code"] = code, ["message"] = message }, exitCode);
}
