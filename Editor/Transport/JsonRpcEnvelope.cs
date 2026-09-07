using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Transport
{
    public static class JsonRpcEnvelope
    {
        public static JObject Request(JToken id, string method, JObject parameters)
            => new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["method"] = method,
                ["params"] = parameters ?? new JObject(),
            };

        public static JObject Success(JToken id, JToken result)
            => new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result ?? new JObject(),
            };

        public static JObject Error(JToken id, int code, string message, JObject data = null)
        {
            var error = new JObject
            {
                ["code"] = code,
                ["message"] = message,
            };
            if (data != null) error["data"] = data;
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = error,
            };
        }
    }
}
