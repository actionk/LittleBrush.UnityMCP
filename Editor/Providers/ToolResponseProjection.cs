using System;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    public static class ToolResponseProjection
    {
        public static JObject BudgetSchema(JObject schema)
        {
            schema["properties"]["maxResponseCharacters"] = JObject.Parse(
                "{'type':'integer','minimum':1000,'maximum':2000000,'default':30000,'description':'JSON character budget, excluding images. Narrow the selection/page or explicitly raise this for large reads.'}");
            return schema;
        }

        public static JObject LayoutSchema(JObject schema)
        {
            var properties = (JObject)schema["properties"];
            properties["layoutDetail"] = JObject.Parse("{'enum':['summary','warnings','full'],'default':'summary'}");
            properties["layoutPath"] = JObject.Parse("{'type':'string','description':'Exact hierarchy path or subtree to inspect.'}");
            foreach (var prefix in new[] { "layoutNode", "layoutWarning" })
            {
                properties[prefix + "Offset"] = JObject.Parse("{'type':'integer','minimum':0}");
                properties[prefix + "Limit"] = JObject.Parse("{'type':'integer','minimum':1,'maximum':50000,'default':25}");
            }
            return BudgetSchema(schema);
        }

        public static int Integer(JObject args, string key, int fallback, int minimum, int maximum)
        {
            var token = args[key];
            if (token == null) return fallback;
            if (token.Type != JTokenType.Integer || token.Value<long>() < minimum || token.Value<long>() > maximum)
                throw new McpToolException(McpErrorCodes.InvalidParams, $"{key} must be an integer from {minimum} to {maximum}.");
            return token.Value<int>();
        }

        public static JObject Page(int total, int offset, int returned)
        {
            var result = new JObject
            {
                ["total"] = total, ["offset"] = offset, ["returned"] = returned,
                ["truncated"] = offset + (long)returned < total,
            };
            if (offset + (long)returned < total) result["nextOffset"] = offset + returned;
            return result;
        }

        public static JObject Layout(JObject report, JObject args)
        {
            var detail = args.Value<string>("layoutDetail") ?? "summary";
            if (detail is not ("summary" or "warnings" or "full"))
                throw new McpToolException(McpErrorCodes.InvalidParams, "layoutDetail must be summary, warnings, or full.");
            var path = args.Value<string>("layoutPath");
            bool Matches(JToken item, string key) => string.IsNullOrEmpty(path)
                || (string)item[key] == path || ((string)item[key])?.StartsWith(path + "/", StringComparison.Ordinal) == true;
            var nodes = ((JArray)report["nodes"]).Where(n => Matches(n, "path")).ToArray();
            var warnings = ((JArray)report["warnings"]).Where(w => Matches(w, "path") || Matches(w, "relatedPath")).ToArray();
            var result = new JObject
            {
                ["nodeCount"] = nodes.Length, ["warningCount"] = warnings.Length,
                ["warningCounts"] = new JObject(warnings.GroupBy(w => (string)w["code"] ?? "unknown")
                    .Select(group => new JProperty(group.Key, group.Count()))),
            };
            if (report["coordinateSpace"] != null) result["coordinateSpace"] = report["coordinateSpace"].DeepClone();
            foreach (var entry in new[] { (key: "nodes", prefix: "layoutNode", values: nodes), (key: "warnings", prefix: "layoutWarning", values: warnings) })
            {
                var offset = Integer(args, entry.prefix + "Offset", 0, 0, int.MaxValue);
                var limit = Integer(args, entry.prefix + "Limit", 25, 1, 50000);
                var include = entry.key == "nodes" ? detail == "full" : detail != "summary";
                var page = new JArray(include ? entry.values.Skip(offset).Take(limit) : Enumerable.Empty<JToken>());
                result[entry.key + "Page"] = Page(entry.values.Length, offset, page.Count);
                if (include) result[entry.key] = page;
            }
            return result;
        }

        public static void CheckBudget(JObject result, JObject args)
        {
            var budget = Integer(args, "maxResponseCharacters", 30000, 1000, 2000000);
            var characters = McpToolCallLogger.CountJsonCharacters(result);
            if (characters > budget)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "Selected response exceeds maxResponseCharacters. Narrow the node/layer selection or page limits, or explicitly raise the budget.",
                    new JObject { ["responseCharacters"] = characters, ["maxResponseCharacters"] = budget });
        }

        public static ToolResult Ok(JObject result, JObject args, params ContentBlock[] content)
        {
            CheckBudget(result, args);
            return ToolResult.Ok(result, content);
        }
    }
}
