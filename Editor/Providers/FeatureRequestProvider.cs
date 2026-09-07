using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class FeatureRequestProvider : IToolProvider
    {
        internal const int MaxRequests = 256;
        private const int MaxFileBytes = 8 * 1024 * 1024;
        private static readonly object s_gate = new();
        public string Namespace => "mcp";

        public void RegisterTools(IToolRegistration reg)
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "McpUsage", "feature-requests.json"));
            reg.Register(new ToolDescriptor
            {
                Name = "mcp.feature_request",
                Description = "Submit local capability feedback after checking the live tool registry. Reuse the capability key to merge reports; counts are submissions, not distinct agents. Include the task, missing operation, checked tools and why they do not fit, workaround, and expected benefit (label unmeasured savings as estimates). This records a proposal, not approval to implement. Saved in Logs/McpUsage/feature-requests.json; no external service is contacted.",
                Availability = ToolAvailability.Either,
                RequiresMainThread = false,
                Execution = ToolExecution.Async,
                TrustCategory = ToolTrustCategory.ProjectWrite,
                Annotations = new JObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = false },
                InputSchema = JObject.Parse(@"{
                    'type': 'object', 'additionalProperties': false,
                    'required': ['capability', 'task', 'missing', 'checkedTools', 'workaround', 'benefit'],
                    'properties': {
                        'capability': {'type':'string', 'minLength':3, 'maxLength':80, 'description':'Stable operation key, e.g. scene.preview.camera-pose. Reuse an existing key when applicable.'},
                        'task': {'type':'string', 'minLength':1, 'maxLength':1000},
                        'missing': {'type':'string', 'minLength':1, 'maxLength':1000},
                        'checkedTools': {'type':'string', 'minLength':1, 'maxLength':1000, 'description':'Live registry search/tools checked and the specific mismatch.'},
                        'workaround': {'type':'string', 'minLength':1, 'maxLength':1000, 'description':'Actual fallback or none available; do not paste credentials or full payloads.'},
                        'benefit': {'type':'string', 'minLength':1, 'maxLength':1000, 'description':'Expected fewer calls, tokens, response bytes, or code. Distinguish measurements from estimates.'}
                    }
                }"),
                Handler = (ctx, ct) => new ValueTask<ToolResult>(Task.Run(() => ToolResult.Ok(Submit(path, ctx.Arguments, ct)), ct)),
            });
        }

        internal static JObject Submit(string path, JObject arguments, CancellationToken ct = default)
        {
            var key = Text(arguments, "capability", 80);
            if (!Regex.IsMatch(key, "^[a-z][a-z0-9_.-]{2,79}$"))
                throw new McpToolException(McpErrorCodes.InvalidParams, "capability must be a lowercase operation key (3-80 characters).");
            var example = new JObject();
            foreach (var field in new[] { "task", "missing", "checkedTools", "workaround", "benefit" })
                example[field] = Text(arguments, field, 1000);
            var now = DateTime.UtcNow.ToString("O");
            example["utc"] = now;
            lock (s_gate)
            {
                ct.ThrowIfCancellationRequested();
                var document = new JObject { ["schemaVersion"] = 1, ["requests"] = new JObject() };
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > MaxFileBytes)
                        throw new McpToolException(McpErrorCodes.ValidationFailed, "Feature request file exceeds 8 MiB. Archive it before submitting more feedback.");
                    try { document = JObject.Parse(File.ReadAllText(path)); }
                    catch (JsonException)
                    {
                        throw new McpToolException(McpErrorCodes.ValidationFailed, "Feature request file is unreadable JSON. Repair or archive it; it has not been overwritten.");
                    }
                }
                if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 1
                    || document["requests"] is not JObject requests)
                    throw new McpToolException(McpErrorCodes.ValidationFailed, "Unsupported feature request file structure; existing data was preserved.");
                var existing = requests[key];
                var merged = existing != null;
                if (!merged && requests.Count >= MaxRequests)
                    throw new McpToolException(McpErrorCodes.ValidationFailed, "Feature request limit (256) reached. Review/archive the file before adding new capabilities; existing keys can still be updated.");
                var entry = existing as JObject ?? new JObject
                {
                    ["firstSeenUtc"] = now, ["submissions"] = 0L, ["examples"] = new JArray(),
                };
                if ((merged && existing is not JObject) || entry["submissions"]?.Type != JTokenType.Integer
                    || entry["examples"] is not JArray examples)
                    throw new McpToolException(McpErrorCodes.ValidationFailed, "Invalid feature request entry; existing data was preserved.");
                entry["submissions"] = checked((long)entry["submissions"] + 1);
                entry["lastSeenUtc"] = now;
                examples.Add(example);
                while (examples.Count > 3) examples.RemoveAt(0);
                requests[key] = entry;
                var serialized = document.ToString(Formatting.Indented);
                if (System.Text.Encoding.UTF8.GetByteCount(serialized) > MaxFileBytes)
                    throw new McpToolException(McpErrorCodes.ValidationFailed, "Feature request storage is full (8 MiB). Archive it before submitting more feedback.");
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, serialized);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return new JObject
                {
                    ["capability"] = key, ["merged"] = merged, ["submissions"] = entry["submissions"].DeepClone(),
                    ["path"] = path, ["status"] = "recorded",
                };
            }
        }

        private static string Text(JObject arguments, string field, int max)
        {
            var token = arguments?[field];
            var value = token?.Type == JTokenType.String ? ((string)token).Trim() : null;
            if (string.IsNullOrWhiteSpace(value) || value.Length > max)
                throw new McpToolException(McpErrorCodes.InvalidParams, $"{field} must contain 1-{max} characters.");
            return value;
        }
    }
}
