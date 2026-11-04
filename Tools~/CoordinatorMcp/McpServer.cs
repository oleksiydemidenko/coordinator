using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

public static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private const string Instructions = "Coordinator holds this Unity project's parallel tasks: decisions, assignments with exclusive write zones, deliveries and reviews. Everything goes through the open Unity Editor. Start with coordinator_query action capabilities, then board or task. Only a person approves a task; acceptance needs the person who decided it. Executors without Unity read their card with git show and write their delivery file by hand.";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static string? root;

    public static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine(Version);
            return 0;
        }
        root = FindProject(args);
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true, NewLine = "\n" };
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0 && Answer(line) is { } answer)
                output.WriteLine(answer.ToJsonString());
        }
        return 0;
    }

    private static string Version => typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static string? FindProject(string[] args)
    {
        var explicitPath = args.SkipWhile(arg => arg != "--project").Skip(1).FirstOrDefault()
            ?? args.FirstOrDefault(arg => !arg.StartsWith("--"))
            ?? Environment.GetEnvironmentVariable("COORDINATOR_PROJECT");
        for (var directory = new DirectoryInfo(explicitPath ?? Directory.GetCurrentDirectory()); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Assets")) && Directory.Exists(Path.Combine(directory.FullName, "ProjectSettings")))
                return directory.FullName;
        return null;
    }

    private static JsonObject? Answer(string line)
    {
        JsonNode message;
        try
        {
            message = JsonNode.Parse(line)!;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return Failure(null, -32700, "Parse error");
        }
        var id = message["id"]?.DeepClone();
        if (id == null)
            return null;
        try
        {
            var result = Handle((string?)message["method"] ?? "", message["params"]);
            return result == null
                ? Failure(id, -32601, "Method not found")
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (Exception exception)
        {
            return Failure(id, -32603, exception.Message);
        }
    }

    private static JsonObject Failure(JsonNode? id, int code, string text)
    {
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };
    }

    private static JsonNode? Handle(string method, JsonNode? parameters)
    {
        return method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = (string?)parameters?["protocolVersion"] ?? ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "coordinator", ["version"] = Version },
                ["instructions"] = Instructions
            },
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = Tools() },
            "tools/call" => Call((string?)parameters?["name"] ?? "", parameters?["arguments"]),
            _ => null
        };
    }

    private static JsonArray Tools()
    {
        return new JsonArray(
            Tool("coordinator_status", "Report the Unity project this server serves, whether it has Coordinator data and whether the Unity Editor is reachable.", Schema(new JsonObject())),
            Tool("coordinator_query", "Read tasks, the board, one task, one assignment card, validation or scope from the open Unity Editor. Call action capabilities first.", ActionSchema(
                new[] { "capabilities", "tasks", "board", "task", "card", "validate", "scope" },
                new JsonObject
                {
                    ["task"] = TextParameter("Task id."),
                    ["assignment"] = TextParameter("Assignment id."),
                    ["checkout"] = TextParameter("For scope: absolute path of an executor's worktree to check against the assignment's zone."),
                    ["include_closed"] = new JsonObject { ["type"] = "boolean", ["description"] = "For board; default true." }
                })),
            Tool("coordinator_edit", "Change tasks in the open Unity Editor as the lead. Records use the JSON format of the coordinator-workflow skill; an edit that adds validation errors is rolled back.", ActionSchema(
                new[] { "create_task", "update_task", "set_decision", "set_assignment", "remove_assignment", "cancel_assignment", "record_review", "generate_cards", "approve_task" },
                new JsonObject
                {
                    ["task"] = new JsonObject { ["description"] = "Task id, or for create_task and update_task the task record." },
                    ["assignment"] = new JsonObject { ["description"] = "Assignment id, or for set_assignment the assignment record." },
                    ["decision"] = new JsonObject { ["type"] = "object", ["description"] = "Decision record for set_decision." },
                    ["round"] = new JsonObject { ["type"] = "object", ["description"] = "Review round for record_review." },
                    ["by"] = TextParameter("Who cancels, for cancel_assignment."),
                    ["reason"] = TextParameter("Why, for cancel_assignment.")
                })));
    }

    private static JsonObject Tool(string name, string description, JsonObject schema)
    {
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
            schema["required"] = new JsonArray(required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        return schema;
    }

    private static JsonObject ActionSchema(string[] actions, JsonObject properties)
    {
        properties["action"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(actions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray())
        };
        var schema = Schema(properties, "action");
        schema["additionalProperties"] = true;
        return schema;
    }

    private static JsonObject TextParameter(string description)
    {
        return new JsonObject { ["type"] = "string", ["description"] = description };
    }

    private static JsonNode Call(string name, JsonNode? arguments)
    {
        if (name == "coordinator_status")
            return Text(Status(), false);
        if (root == null)
            return Text("No Unity project found. Start the server inside a Unity project or pass --project <path>.", true);
        return name switch
        {
            "coordinator_query" => Bridge("query", arguments),
            "coordinator_edit" => Bridge("edit", arguments),
            _ => Text("Unknown tool: " + name, true)
        };
    }

    private static string BridgeState => Path.Combine(root!, "Library", "Coordinator", "agent-bridge.json");

    private static string Status()
    {
        return new JsonObject
        {
            ["version"] = Version,
            ["project"] = root,
            ["has_coordinator"] = root != null && Directory.Exists(Path.Combine(root, "Coordinator")),
            ["editor_connected"] = root != null && EditorReachable()
        }.ToJsonString();
    }

    private static bool EditorReachable()
    {
        if (!File.Exists(BridgeState))
            return false;
        try
        {
            using var client = new TcpClient("127.0.0.1", (int)JsonNode.Parse(File.ReadAllText(BridgeState))!["port"]!);
            return client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static JsonNode Bridge(string tool, JsonNode? arguments)
    {
        const string notRunning = "The Unity Editor is not running for this project, or it is compiling. Open the project and try again. Coordinator works only through the open Editor.";
        if (!File.Exists(BridgeState))
            return Text(notRunning, true);
        try
        {
            var bridge = JsonNode.Parse(File.ReadAllText(BridgeState))!;
            using var client = new TcpClient("127.0.0.1", (int)bridge["port"]!);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Utf8) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(stream, Utf8);
            writer.WriteLine(new JsonObject { ["token"] = (string?)bridge["token"], ["tool"] = tool, ["parameters"] = arguments?.DeepClone() ?? new JsonObject() }.ToJsonString());
            var answer = reader.ReadLine();
            if (answer == null)
                return Text(notRunning, true);
            return Text(answer, !((bool?)JsonNode.Parse(answer)!["success"] ?? false));
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return Text(notRunning, true);
        }
    }

    private static JsonObject Text(string text, bool isError)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };
    }
}
