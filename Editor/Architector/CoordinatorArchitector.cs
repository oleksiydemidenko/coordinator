using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coordinator.Editor
{
    public sealed class CoordinatorArchitectorLayer
    {
        public string Id { get; internal set; } = string.Empty;
        public string Name { get; internal set; } = string.Empty;
        public string GroupId { get; internal set; } = string.Empty;
        public string Path { get; internal set; } = string.Empty;
        public IReadOnlyList<string> ScopeFolders { get; internal set; } = Array.Empty<string>();
    }

    public sealed class CoordinatorArchitector
    {
        public const string RootFolder = "Architector";
        private readonly Dictionary<string, CoordinatorArchitectorLayer> byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CoordinatorArchitectorLayer> byPath = new(StringComparer.Ordinal);
        private readonly HashSet<string> connectedPairs = new(StringComparer.Ordinal);

        private CoordinatorArchitector()
        {
        }

        public IReadOnlyCollection<CoordinatorArchitectorLayer> Layers => byId.Values;
        public List<string> Problems { get; } = new();

        public static CoordinatorArchitector Load(string projectRoot)
        {
            var root = System.IO.Path.Combine(projectRoot, RootFolder);
            if (!Directory.Exists(System.IO.Path.Combine(root, "Layers")))
                return null;
            var architector = new CoordinatorArchitector();
            var groups = new Dictionary<string, (string name, string parent)>(StringComparer.Ordinal);
            foreach (var json in architector.Read(root, "Groups", "*.group.json"))
                groups[(string)json["id"] ?? string.Empty] = ((string)json["name"] ?? string.Empty, (string)json["parentGroupId"] ?? string.Empty);
            foreach (var json in architector.Read(root, "Layers", "*.layer.json"))
            {
                var layer = new CoordinatorArchitectorLayer
                {
                    Id = (string)json["layerId"] ?? string.Empty,
                    Name = (string)json["name"] ?? string.Empty,
                    GroupId = (string)json["parentGroupId"] ?? string.Empty,
                    ScopeFolders = json["scope"]?["folders"]?.Values<string>().Where(folder => !string.IsNullOrEmpty(folder)).ToArray() ?? Array.Empty<string>()
                };
                layer.Path = GroupPath(groups, layer.GroupId) + layer.Name;
                architector.byId[layer.Id] = layer;
                architector.byPath[layer.Path] = layer;
            }
            foreach (var json in architector.Read(root, "Links", "*.link.json"))
            {
                var layers = json["endpoints"]?.Select(endpoint => (string)endpoint["layerId"]).Where(id => id != null).Distinct().ToArray();
                if (layers?.Length == 2)
                    architector.connectedPairs.Add(PairKey(layers[0], layers[1]));
            }
            return architector;
        }

        public CoordinatorArchitectorLayer FindByPath(string path) => byPath.TryGetValue(path ?? string.Empty, out var layer) ? layer : null;

        public bool Connected(CoordinatorArchitectorLayer left, CoordinatorArchitectorLayer right) =>
            connectedPairs.Contains(PairKey(left.Id, right.Id));

        public static IEnumerable<string> ZonePatterns(CoordinatorArchitectorLayer layer)
        {
            return layer.ScopeFolders.Select(folder => folder.Replace('\\', '/').Trim('/'))
                .Select(folder => System.IO.Path.HasExtension(folder.Split('/').Last()) ? folder : folder + "/**");
        }

        private IEnumerable<JObject> Read(string root, string folder, string pattern)
        {
            var directory = System.IO.Path.Combine(root, folder);
            if (!Directory.Exists(directory))
                yield break;
            foreach (var file in Directory.GetFiles(directory, pattern).OrderBy(path => path, StringComparer.Ordinal))
            {
                JObject json;
                try
                {
                    json = JObject.Parse(File.ReadAllText(file));
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    Problems.Add($"{RootFolder}/{folder}/{System.IO.Path.GetFileName(file)}: {exception.Message}");
                    continue;
                }
                yield return json;
            }
        }

        private static string GroupPath(IReadOnlyDictionary<string, (string name, string parent)> groups, string groupId)
        {
            var names = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(groupId) && visited.Add(groupId) && groups.TryGetValue(groupId, out var group))
            {
                names.Insert(0, group.name);
                groupId = group.parent;
            }
            return names.Count == 0 ? string.Empty : string.Join("/", names) + "/";
        }

        private static string PairKey(string left, string right) =>
            string.CompareOrdinal(left, right) < 0 ? left + "|" + right : right + "|" + left;
    }
}
