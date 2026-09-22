using System;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Host;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace LittleBrushGames.Mcp.Modules.Pipeline
{
    public static class McpPipelineCommands
    {
        [CliCommand("lbg_mcp_tools", "Discover LittleBrush MCP tools and schemas.", MainThreadRequired = false)]
        public static Task<JObject> Tools([CliArg("query", "unity.tools arguments as a JSON object.")] JObject query = null)
            => Invoke("unity.tools", query ?? new JObject());

        [CliCommand("lbg_mcp_call", "Invoke a LittleBrush tool through its permissions and writer queue.", MainThreadRequired = false)]
        public static Task<JObject> Call(
            [CliArg("tool", "Exact tool name from lbg_mcp_tools.")] string tool,
            [CliArg("arguments", "Tool arguments as a JSON object.")] JObject arguments = null)
            => Invoke("unity.call", new JObject { ["tool"] = tool, ["arguments"] = arguments ?? new JObject() });

        private static async Task<JObject> Invoke(string gateway, JObject arguments)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var response = await McpBridgeHost.InvokeGatewayAsync(gateway, arguments, timeout.Token,
                "pipeline:" + Guid.NewGuid().ToString("N"));
            if (response["error"] != null)
                throw new InvalidOperationException(response["error"].ToString(Formatting.None));
            var result = (JObject)response["result"];
            if (result.Value<bool?>("isError") == true)
                throw new InvalidOperationException(result.ToString(Formatting.None));
            return result;
        }
    }
}
