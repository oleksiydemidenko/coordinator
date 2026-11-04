using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coordinator.Editor
{
    public sealed class CoordinatorAgentResult
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        private CoordinatorAgentResult(bool success, string message, string code, object data)
        {
            Success = success;
            Message = message;
            Code = code;
            Data = data;
        }

        [JsonProperty("success")] public bool Success { get; }
        [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)] public string Message { get; }
        [JsonProperty("code", NullValueHandling = NullValueHandling.Ignore)] public string Code { get; }
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public object Data { get; }

        public static CoordinatorAgentResult Ok(string message, object data = null) => new(true, message, null, data);

        public static CoordinatorAgentResult Fail(string code, string message, object data = null) => new(false, message, code, data);

        public string ToJson() => JObject.FromObject(this, Serializer).ToString(Formatting.None);
    }
}
