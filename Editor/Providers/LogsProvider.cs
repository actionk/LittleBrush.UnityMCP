using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class LogsProvider : IToolProvider
    {
        private static readonly LogRingBuffer s_buffer = new(
            Host.McpBridgeSettings.GetOrLoad()?.LogBufferSize ?? 1000);
        private static readonly string[] s_levels = { "debug", "info", "notice", "warning", "error", "critical", "alert", "emergency" };
        private static string s_minimumLevel = "debug";

        static LogsProvider()
        {
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        private static void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            var plainCondition = StripRichText(condition);
            s_buffer.Add(plainCondition, stackTrace, type);

            // Push to SSE clients if any are connected
            var sink = Host.McpBridgeHost.Notifications;
            if (sink == null) return;

            var levelStr = type switch
            {
                LogType.Warning => "warning",
                LogType.Error => "error",
                LogType.Assert => "error",
                LogType.Exception => "error",
                _ => "info",
            };

            if (Array.IndexOf(s_levels, levelStr) < Array.IndexOf(s_levels, s_minimumLevel)) return;

            sink.Send("notifications/message", new JObject
            {
                ["level"] = levelStr,
                ["logger"] = "Unity",
                ["data"] = new JObject
                {
                    ["message"] = plainCondition,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            });
        }

        private static string StripRichText(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('<') < 0)
                return value;

            var plain = new StringBuilder(value.Length);
            var inTag = false;
            foreach (var character in value)
            {
                if (inTag)
                {
                    if (character == '>')
                        inTag = false;
                    continue;
                }

                if (character == '<')
                {
                    inTag = true;
                    continue;
                }

                plain.Append(character);
            }

            return plain.ToString();
        }

        internal static bool SetMinimumLevel(string level)
        {
            if (Array.IndexOf(s_levels, level) < 0) return false;
            s_minimumLevel = level;
            return true;
        }

        public string Namespace => "editor.logs";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "editor.logs.tail",
                Description = "Return the last N buffered log entries with optional substring and level filters.",
                Availability = ToolAvailability.Always,
                RequiresMainThread = false,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                        ""contains"": { ""type"": ""string"" },
                        ""level"": { ""enum"": [""log"", ""warning"", ""error"", ""exception""] },
                        ""afterSequence"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""sinceTimestamp"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""includeStackTrace"": { ""type"": ""boolean"" },
                        ""maxTextCharacters"": { ""type"": ""integer"", ""minimum"": 256, ""maximum"": 20000, ""description"": ""Per message/stack cap. Default 1000."" }
                    }
                }"),
                Handler = Tail,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "editor.logs.clear",
                Description = "Clear the log ring buffer.",
                Availability = ToolAvailability.Always,
                RequiresMainThread = false,
                InputSchema = new JObject { ["type"] = "object" },
                Handler = (_, _) =>
                {
                    s_buffer.Clear();
                    return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["cleared"] = true }));
                },
            });
        }

        private static ValueTask<ToolResult> Tail(ToolContext ctx, CancellationToken __)
            => new(ToolResult.Ok(CreateTailResponse(s_buffer.Snapshot(), ctx.Arguments)));

        internal static JObject CreateTailResponse(LogRingBuffer.Entry[] snapshot, JObject args)
        {
            var limit = args["limit"]?.Value<int>() ?? 20;
            var contains = (string)args["contains"];
            var level = (string)args["level"];
            var afterSequence = args["afterSequence"]?.Value<long>() ?? 0;
            var sinceTimestamp = args["sinceTimestamp"]?.Value<long>() ?? 0;
            var includeStackTrace = args["includeStackTrace"]?.Value<bool>() == true;
            var maxTextCharacters = args["maxTextCharacters"]?.Value<int>() ?? 1000;

            var matches = new System.Collections.Generic.List<LogRingBuffer.Entry>();
            foreach (var entry in snapshot)
            {
                if (afterSequence > 0 && entry.Sequence <= afterSequence) continue;
                if (sinceTimestamp > 0 && entry.Timestamp <= sinceTimestamp) continue;
                if (!MatchesLevel(entry.Type, level)) continue;
                if (!string.IsNullOrEmpty(contains) && (entry.Message?.IndexOf(contains, StringComparison.OrdinalIgnoreCase) ?? -1) < 0) continue;
                matches.Add(entry);
            }

            var start = Math.Max(0, matches.Count - limit);
            var results = new JArray();
            for (var i = start; i < matches.Count; i++)
            {
                var entry = matches[i];
                var item = new JObject
                {
                    ["sequence"] = entry.Sequence,
                    ["type"] = entry.Type.ToString(),
                    ["timestamp"] = entry.Timestamp,
                };
                AddBoundedText(item, "message", entry.Message, maxTextCharacters);
                if (includeStackTrace && !string.IsNullOrEmpty(entry.StackTrace))
                    AddBoundedText(item, "stackTrace", entry.StackTrace, maxTextCharacters);
                results.Add(item);
            }
            var nextSequence = results.Count > 0
                ? results[results.Count - 1]["sequence"]?.Value<long>() ?? afterSequence
                : afterSequence;
            return new JObject
            {
                ["totalMatching"] = matches.Count,
                ["returned"] = results.Count,
                ["truncated"] = matches.Count > results.Count,
                ["entries"] = results,
                ["nextSequence"] = nextSequence,
            };
        }

        private static void AddBoundedText(JObject target, string name, string value, int maxCharacters)
        {
            value ??= string.Empty;
            target[name] = value.Length <= maxCharacters ? value : value.Substring(0, maxCharacters);
            if (value.Length <= maxCharacters) return;
            target[name + "Truncated"] = true;
            target[name + "Characters"] = value.Length;
        }

        private static bool MatchesLevel(LogType type, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return filter.ToLowerInvariant() switch
            {
                "log" => type == LogType.Log,
                "warning" => type == LogType.Warning,
                "error" => type == LogType.Error || type == LogType.Assert,
                "exception" => type == LogType.Exception,
                _ => true,
            };
        }
    }
}
