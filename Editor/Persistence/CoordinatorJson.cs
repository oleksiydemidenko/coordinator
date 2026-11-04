using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Coordinator.Editor
{
    public static class CoordinatorJson
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MissingMemberHandling = MissingMemberHandling.Error
        };

        public static T Parse<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        public static string Serialize(object document)
        {
            var builder = new StringBuilder();
            using (var writer = new StringWriter(builder))
            using (var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented, Indentation = 4 })
                JsonSerializer.Create(Settings).Serialize(json, document);
            return builder.Replace("\r\n", "\n").Append('\n').ToString();
        }

        public static void Write(string path, object document)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Serialize(document), new UTF8Encoding(false));
        }
    }
}
