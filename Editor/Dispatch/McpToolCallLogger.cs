using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Host;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public static class McpToolCallLogger
    {
        internal static int CountJsonCharacters(JToken value)
        {
            if (value == null) return 0;
            using var counter = new CharacterCounter();
            using var writer = new JsonTextWriter(counter) { Formatting = Formatting.None };
            value.WriteTo(writer);
            writer.Flush();
            return counter.Count;
        }

        private sealed class CharacterCounter : System.IO.TextWriter
        {
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
            public int Count { get; private set; }
            public override void Write(char value) => Count++;
            public override void Write(string value) => Count += value?.Length ?? 0;
            public override void Write(char[] buffer, int index, int count) => Count += count;
        }

        public const int DefaultMaxCompactResponseCharacters = 2000;
        private const int MaxArgumentLogCharacters = 512;
        private const int MaxArgumentStringCharacters = 160;
        private const int MaxArgumentDepth = 2;
        private static readonly HashSet<string> s_payloadArgumentNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "base64", "body", "bytes", "code", "content", "data", "edits", "graph", "image",
            "operations", "properties", "script", "source",
        };
        private static readonly HashSet<string> s_secretArgumentNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "authorization", "password", "playSessionToken", "secret", "token",
        };
        private static readonly HashSet<string> s_resultSummaryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "changed", "count", "created", "deleted", "dryRun", "errorCount", "failed", "missing",
            "removed", "returned", "saved", "skipped", "succeeded", "total", "truncated", "updated",
            "warningCount",
        };
        private static readonly HashSet<string> s_resultCollectionNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "assets", "entries", "errors", "items", "matches", "results", "scenes", "tools", "warnings",
        };

        public static void Log(
            ILogSink sink,
            string name,
            JToken id,
            long correlationId,
            JObject response,
            McpToolCallLogMode mode,
            int maxCompactResponseCharacters = DefaultMaxCompactResponseCharacters,
            Exception exception = null,
            bool expectedFailure = false,
            JObject arguments = null)
        {
            if (sink == null || mode == McpToolCallLogMode.Disabled)
                return;

            var errorCodeToken = response?["error"]?["code"];
            var errorCode = errorCodeToken?.Type == JTokenType.Integer
                ? errorCodeToken.Value<int>()
                : (int?)null;
            var isErrorToken = response?["result"]?["isError"];
            var isError = response?["error"] != null
                          || isErrorToken?.Type == JTokenType.Boolean && isErrorToken.Value<bool>();
            var structuredFailure = !isError && HasStructuredFailure(response);
            var level = isError
                ? expectedFailure ? ExpectedFailureLevel(errorCode) : LogLevel.Error
                : structuredFailure ? LogLevel.Warn
                : IsRoslynTool(name) ? LogLevel.Warn : LogLevel.Info;
            var status = isError
                ? expectedFailure ? "rejected" : "error"
                : structuredFailure ? "warning" : "ok";
            var header = FormatHeader(name, id, correlationId, status, mode, FormatArguments(arguments));

            if (mode == McpToolCallLogMode.Summary)
            {
                var error = status == "ok" && exception == null
                    ? null
                    : GetErrorSummary(response, exception);
                var detailLabel = status == "error" ? "error" : status == "warning" ? "diagnostic" : "reason";
                if (!string.IsNullOrEmpty(error))
                    sink.Log(level, $"{header} ({detailLabel}: {error})");
                else
                {
                    var resultSummary = GetResultSummary(response);
                    sink.Log(level, string.IsNullOrEmpty(resultSummary)
                        ? header
                        : $"{header} ({resultSummary})");
                }
                return;
            }

            var body = mode == McpToolCallLogMode.Full
                ? response?.ToString(Formatting.Indented) ?? "{}"
                : CompactResponse(response, maxCompactResponseCharacters);
            if (exception != null)
            {
                var exceptionText = mode == McpToolCallLogMode.Full
                    ? exception.ToString()
                    : $"{exception.GetType().Name}: {exception.Message}";
                body += $"\nException: {exceptionText}";
            }
            sink.Log(level, $"{header}\nResponse: {body}");
        }

        private static LogLevel ExpectedFailureLevel(int? errorCode)
        {
            return errorCode == McpErrorCodes.Compiling
                   || errorCode == McpErrorCodes.ToolUnavailable
                   || errorCode == McpErrorCodes.MainThreadBusy
                   || errorCode == McpErrorCodes.ExclusiveBusy
                   || errorCode == McpErrorCodes.Cancelled
                   || errorCode == McpErrorCodes.ServiceUnavailable
                ? LogLevel.Info
                : LogLevel.Warn;
        }

        private static bool HasStructuredFailure(JObject response)
        {
            var structured = response?["result"]?["structuredContent"] as JObject;
            if (structured == null)
                return false;

            if (structured["success"]?.Type == JTokenType.Boolean && !structured["success"].Value<bool>())
                return true;
            if (structured["timeout"]?.Type == JTokenType.Boolean && structured["timeout"].Value<bool>())
                return true;

            var failed = structured["failed"];
            if (failed?.Type == JTokenType.Integer && failed.Value<int>() > 0)
                return true;

            var status = structured["status"]?.Type == JTokenType.String
                ? structured["status"].Value<string>()
                : null;
            return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsRoslynTool(string name)
        {
            return !string.IsNullOrEmpty(name)
                   && (name.IndexOf("roslyn", StringComparison.OrdinalIgnoreCase) >= 0
                       || string.Equals(name, "editor.execute_code", StringComparison.OrdinalIgnoreCase));
        }

        private static string CompactResponse(JObject response, int maxCharacters)
        {
            var body = response?["error"] ?? response?["result"];
            if (body == null)
                return "{}";

            var compact = body.DeepClone();
            if (compact is JObject objectBody)
            {
                if (objectBody["isError"]?.Type == JTokenType.Boolean && !objectBody["isError"].Value<bool>())
                    objectBody.Remove("isError");

                CompactStructuredContent(objectBody);
                CompactContent(objectBody["content"] as JArray);
            }

            return Truncate(compact.ToString(Formatting.None), maxCharacters);
        }

        private static string GetErrorSummary(JObject response, Exception exception)
        {
            var message = exception != null
                ? $"Exception: {exception.GetType().Name}: {exception.Message}"
                : StringValue(response?["error"]?["message"])
                  ?? GetStructuredFailureSummary(response?["result"]?["structuredContent"] as JObject)
                  ?? StringValue(response?["result"]?["content"]?[0]?["text"]);

            if (LooksLikeJson(message))
                message = null;

            return string.IsNullOrWhiteSpace(message)
                ? null
                : Truncate(message.Replace('\r', ' ').Replace('\n', ' '), 256);
        }

        private static string FormatArguments(JObject arguments)
        {
            if (arguments == null || arguments.Count == 0)
                return null;

            var compact = new JObject();
            foreach (var property in arguments.Properties())
                compact[property.Name] = SummarizeArgument(property.Name, property.Value, 0);

            return Truncate(compact.ToString(Formatting.None), MaxArgumentLogCharacters);
        }

        private static JToken SummarizeArgument(string name, JToken value, int depth)
        {
            if (value == null || value.Type == JTokenType.Null)
                return JValue.CreateNull();

            if (s_secretArgumentNames.Contains(name))
                return new JValue("<redacted>");

            switch (value.Type)
            {
                case JTokenType.String:
                    var text = value.Value<string>() ?? string.Empty;
                    if (s_payloadArgumentNames.Contains(name))
                        return new JObject
                        {
                            ["length"] = text.Length,
                            ["preview"] = Truncate(Clean(text), 80),
                        };
                    return new JValue(Truncate(Clean(text), MaxArgumentStringCharacters));

                case JTokenType.Object:
                    var objectValue = (JObject)value;
                    if (s_payloadArgumentNames.Contains(name) || depth >= MaxArgumentDepth || objectValue.Count > 12)
                        return SummarizeObject(objectValue);

                    var compactObject = new JObject();
                    foreach (var property in objectValue.Properties())
                        compactObject[property.Name] = SummarizeArgument(property.Name, property.Value, depth + 1);
                    return compactObject;

                case JTokenType.Array:
                    var arrayValue = (JArray)value;
                    if (s_payloadArgumentNames.Contains(name) || arrayValue.Count > 8)
                        return SummarizeArray(arrayValue);

                    var compactArray = new JArray();
                    foreach (var item in arrayValue)
                        compactArray.Add(SummarizeArgument(name, item, depth + 1));
                    return compactArray;

                default:
                    return value.DeepClone();
            }
        }

        private static JObject SummarizeObject(JObject value)
        {
            var keys = new JArray();
            var count = 0;
            foreach (var property in value.Properties())
            {
                if (count++ >= 8)
                    break;
                keys.Add(property.Name);
            }

            return new JObject
            {
                ["keys"] = keys,
                ["count"] = value.Count,
            };
        }

        private static JObject SummarizeArray(JArray value)
        {
            var summary = new JObject { ["count"] = value.Count };
            var preview = new JArray();
            for (var i = 0; i < value.Count && preview.Count < 8; i++)
            {
                if (!(value[i] is JObject item))
                    continue;

                var type = StringValue(item["type"]);
                var path = StringValue(item["path"]);
                if (!string.IsNullOrEmpty(type))
                    preview.Add(type);
                else if (!string.IsNullOrEmpty(path))
                    preview.Add(path);
            }

            if (preview.Count > 0)
                summary["preview"] = preview;
            return summary;
        }

        private static string GetResultSummary(JObject response)
        {
            var structured = response?["result"]?["structuredContent"] as JObject;
            if (structured == null)
                return null;

            var parts = new List<string>();
            foreach (var property in structured.Properties())
            {
                if (!s_resultSummaryNames.Contains(property.Name)
                    || (property.Value.Type != JTokenType.Boolean
                        && property.Value.Type != JTokenType.Integer
                        && property.Value.Type != JTokenType.Float
                        && property.Value.Type != JTokenType.String))
                    continue;

                parts.Add($"{property.Name}={property.Value.ToString(Formatting.None)}");
                if (parts.Count >= 4)
                    break;
            }

            if (parts.Count == 0)
            {
                foreach (var property in structured.Properties())
                {
                    if (!(property.Value is JArray array) || !s_resultCollectionNames.Contains(property.Name))
                        continue;
                    parts.Add($"{property.Name}={array.Count}");
                    if (parts.Count >= 2)
                        break;
                }
            }

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static string GetStructuredFailureSummary(JObject structured)
        {
            if (structured == null)
                return null;

            var message = StringValue(structured["runtimeError"])
                          ?? StringValue(structured["error"])
                          ?? StringValue(structured["message"]);
            if (!string.IsNullOrWhiteSpace(message))
                return message;

            var errorCount = structured["errorCount"]?.Type == JTokenType.Integer
                ? structured["errorCount"].Value<int>()
                : 0;
            if (errorCount > 0)
                return $"{errorCount} error{(errorCount == 1 ? string.Empty : "s")}";

            var failed = structured["failed"]?.Type == JTokenType.Integer
                ? structured["failed"].Value<int>()
                : 0;
            return failed > 0
                ? $"{failed} failed"
                : null;
        }

        private static bool LooksLikeJson(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var trimmed = value.TrimStart();
            return (trimmed.StartsWith("{", StringComparison.Ordinal)
                    && trimmed.EndsWith("}", StringComparison.Ordinal))
                   || (trimmed.StartsWith("[", StringComparison.Ordinal)
                       && trimmed.EndsWith("]", StringComparison.Ordinal));
        }

        private static string StringValue(JToken token)
        {
            return token?.Type == JTokenType.String
                ? token.Value<string>()
                : null;
        }

        private static void CompactContent(JArray content)
        {
            if (content == null)
                return;

            foreach (var token in content)
            {
                if (!(token is JObject item))
                    continue;

                var text = item["text"];
                if (text?.Type == JTokenType.String)
                    item["text"] = Truncate(((string)text).Replace('\r', ' ').Replace('\n', ' '), 512);

                var data = item["data"];
                if (data?.Type == JTokenType.String)
                {
                    item.Remove("data");
                    item["dataBytes"] = EstimateDecodedBytes((string)data);
                }
            }
        }

        private static void CompactStructuredContent(JObject result)
        {
            var structured = result["structuredContent"];
            if (structured == null)
                return;

            result.Remove("structuredContent");
            if (structured is JObject structuredObject)
            {
                var keys = new JArray();
                var count = 0;
                foreach (var property in structuredObject.Properties())
                {
                    if (count++ >= 12)
                        break;
                    keys.Add(property.Name);
                }

                result["structuredKeys"] = keys;
                result["structuredKeyCount"] = structuredObject.Count;
            }
            else
            {
                result["structuredType"] = structured.Type.ToString();
            }
        }

        private static string FormatHeader(
            string name,
            JToken id,
            long correlationId,
            string status,
            McpToolCallLogMode mode,
            string arguments)
        {
            var details = mode == McpToolCallLogMode.Summary
                ? string.Empty
                : $" [cid={correlationId}, id={FormatId(id)}]";
            var request = string.IsNullOrEmpty(arguments) ? string.Empty : $" args={arguments}";
            return $"'{name ?? "<unknown>"}'{details}{request} - {status}";
        }

        private static string Clean(string value)
        {
            return value?.Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
        }

        private static int EstimateDecodedBytes(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return 0;

            var padding = encoded.EndsWith("==", StringComparison.Ordinal)
                ? 2
                : encoded.EndsWith("=", StringComparison.Ordinal) ? 1 : 0;
            return Math.Max(0, (int)Math.Min(int.MaxValue, encoded.Length * 3L / 4L) - padding);
        }

        private static string FormatId(JToken id)
        {
            if (id == null || id.Type == JTokenType.Null)
                return "none";

            return Truncate(id.ToString(Formatting.None).Replace('\r', ' ').Replace('\n', ' '), 64);
        }

        private static string Truncate(string value, int maxCharacters)
        {
            var limit = maxCharacters > 0 ? maxCharacters : DefaultMaxCompactResponseCharacters;
            if (string.IsNullOrEmpty(value) || value.Length <= limit)
                return value ?? string.Empty;

            return value.Substring(0, limit) + "... (truncated)";
        }
    }

    internal static class McpToolMetrics
    {
        public const int LargeResponseWarningCharacters = 32 * 1024;
        private static readonly ConcurrentDictionary<string, Entry> s_entries = new(StringComparer.Ordinal);

        public static bool Record(string name, long durationMs, int responseCharacters, bool failed, bool warnCandidate)
        {
            var entry = s_entries.GetOrAdd(name ?? "<unknown>", _ => new Entry());
            lock (entry)
            {
                entry.Calls++;
                if (failed) entry.Failures++;
                entry.LastDurationMs = durationMs;
                entry.TotalDurationMs += durationMs;
                entry.MaxDurationMs = Math.Max(entry.MaxDurationMs, durationMs);
                entry.LastResponseCharacters = responseCharacters;
                entry.TotalResponseCharacters += responseCharacters;
                entry.MaxResponseCharacters = Math.Max(entry.MaxResponseCharacters, responseCharacters);
                if (!warnCandidate || entry.LargeResponseWarned) return false;
                entry.LargeResponseWarned = true;
                return true;
            }
        }

        public static JObject CreateResponse(JObject arguments)
        {
            var query = arguments?["query"]?.Value<string>() ?? string.Empty;
            var offset = arguments?["offset"]?.Value<int>() ?? 0;
            var limit = arguments?["limit"]?.Value<int>() ?? 25;
            if (offset < 0 || limit is < 1 or > 100)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "offset must be >= 0 and limit must be between 1 and 100.");

            var matches = s_entries
                .Where(pair => query.Length == 0
                               || pair.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();
            var page = new JArray(matches.Skip(offset).Take(limit).Select(pair => Snapshot(pair.Key, pair.Value)));
            var result = new JObject
            {
                ["filtered"] = matches.Count,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["truncated"] = offset + page.Count < matches.Count,
                ["tools"] = page,
            };
            if (offset + page.Count < matches.Count) result["nextOffset"] = offset + page.Count;
            return result;
        }

        private static JObject Snapshot(string name, Entry entry)
        {
            lock (entry)
            {
                return new JObject
                {
                    ["name"] = name,
                    ["calls"] = entry.Calls,
                    ["failures"] = entry.Failures,
                    ["lastDurationMs"] = entry.LastDurationMs,
                    ["averageDurationMs"] = entry.Calls == 0 ? 0 : entry.TotalDurationMs / entry.Calls,
                    ["maxDurationMs"] = entry.MaxDurationMs,
                    ["lastResponseCharacters"] = entry.LastResponseCharacters,
                    ["averageResponseCharacters"] = entry.Calls == 0 ? 0 : entry.TotalResponseCharacters / entry.Calls,
                    ["maxResponseCharacters"] = entry.MaxResponseCharacters,
                };
            }
        }

        private sealed class Entry
        {
            public long Calls;
            public long Failures;
            public long LastDurationMs;
            public long TotalDurationMs;
            public long MaxDurationMs;
            public long LastResponseCharacters;
            public long TotalResponseCharacters;
            public long MaxResponseCharacters;
            public bool LargeResponseWarned;
        }
    }
}
