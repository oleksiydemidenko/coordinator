using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coordinator.Editor
{
    public sealed class CoordinatorWorkflowMode
    {
        public CoordinatorWorkflowMode(string id, string displayName, string description, string agentRule, bool agentMayApprove)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            AgentRule = agentRule;
            AgentMayApprove = agentMayApprove;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string AgentRule { get; }
        public bool AgentMayApprove { get; }
    }

    // How much the lead agent does on its own, as in Architector. The person picks the mode in the Coordinator window; it is
    // kept per user and project in the Editor, and the Editor mirrors it to Library/Coordinator/agent-mode.json (per checkout,
    // not committed), which the API reads in the Editor and in the command line alike. No file, or an unknown mode, is Safe.
    public static class CoordinatorWorkflowModes
    {
        public const string Safe = "safe";
        public const string Free = "free";
        public const string Lazy = "lazy";
        public const string FileName = "agent-mode.json";

        public static readonly IReadOnlyList<CoordinatorWorkflowMode> All = new[]
        {
            new CoordinatorWorkflowMode(Safe, "Safe",
                "The agent shows you every split, decision and review verdict and waits for your go-ahead before it records it. Only you approve a task, in this window.",
                "Show the user every new or changed split, decision and review verdict and wait for the go-ahead before you record it. Only a person approves a task, in the Coordinator window.",
                false),
            new CoordinatorWorkflowMode(Free, "Free",
                "The agent splits tasks, records decisions and returns cards on its own. It accepts a card only on your word. Only you approve a task, in this window.",
                "Split tasks, record decisions and return cards on your own. Accept a card only when the user told you to (instructedBy). Only a person approves a task, in the Coordinator window.",
                false),
            new CoordinatorWorkflowMode(Lazy, "Lazy",
                "As Free, and the agent also approves tasks on its own after it checks them again: validate must be clean for the task, and the approval names the agent and you as the person who instructed. Coordinator is less effective without your control.",
                "Work as in Free. You may approve a task yourself with approve_task: first check the split again against its sources and decisions, and run validate with the task until it shows nothing. Pass by (your agent name) and instructed_by (the person who gave you the work). Tell the user which tasks you approved.",
                true)
        };

        public static CoordinatorWorkflowMode Find(string id) =>
            All.FirstOrDefault(mode => string.Equals(mode.Id, id, StringComparison.Ordinal)) ?? All[0];

        public static string PathFor(string projectRoot) => Path.Combine(projectRoot, "Library", "Coordinator", FileName);

        public static string Text(CoordinatorWorkflowMode mode) =>
            new JObject
            {
                ["workflowMode"] = mode.Id,
                ["displayName"] = mode.DisplayName,
                ["description"] = mode.Description,
                ["agentRule"] = mode.AgentRule,
                ["agentMayApprove"] = mode.AgentMayApprove
            }.ToString(Formatting.Indented);

        // Writes the file only when its text changes; returns false when it could not be written.
        public static bool Write(string projectRoot, CoordinatorWorkflowMode mode)
        {
            var path = PathFor(projectRoot);
            var text = Text(mode);
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == text)
                    return true;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The mode of this checkout. Only the id counts: the rest of the file is for people and agents that read it.
        public static CoordinatorWorkflowMode Read(string projectRoot)
        {
            try
            {
                var path = PathFor(projectRoot);
                return File.Exists(path) ? Find((string)JObject.Parse(File.ReadAllText(path))["workflowMode"]) : All[0];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidCastException or ArgumentException)
            {
                return All[0];
            }
        }

        public static object Json(CoordinatorWorkflowMode mode) => new
        {
            id = mode.Id,
            name = mode.DisplayName,
            agent_rule = mode.AgentRule,
            agent_may_approve = mode.AgentMayApprove,
            modes = All.Select(candidate => candidate.Id).ToArray(),
            set_by = "A person picks the mode in the Coordinator window (Mode in the toolbar). Agents never write Library/Coordinator/agent-mode.json."
        };
    }
}
