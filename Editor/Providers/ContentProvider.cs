using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class ContentProvider : IToolProvider
    {
        private const string Root = "Assets/Resources/Content";

        public string Namespace => "content";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "content.status",
                Description = "Report JsonContentManager availability, load state, and source JSON file count.",
                Availability = ToolAvailability.Either,
                Handler = Status,
                InputSchema = new JObject { ["type"] = "object" },
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "content.read",
                Description = "Read one source JSON file through a compact, paged projection with a concurrency hash. Defaults to child summaries; use pointer plus detail=full for targeted values.",
                Availability = ToolAvailability.Either,
                Handler = Read,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path""], ""additionalProperties"": false,
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""pointer"": { ""type"": ""string"", ""description"": ""RFC 6901 JSON Pointer. Empty/omitted selects the document root."" },
                        ""detail"": { ""type"": ""string"", ""enum"": [""summary"", ""full""] },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 },
                        ""maxContentCharacters"": { ""type"": ""integer"", ""minimum"": 1000, ""maximum"": 200000 }
                    }
                }"),
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "content.write",
                Description = "Create or replace one source content JSON file with expectedHash concurrency, dryRun, generated-file protection, and automatic reindex scheduling.",
                Availability = ToolAvailability.EditMode,
                Handler = Write,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path"", ""content""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": [""string"", ""null""] },
                        ""content"": { ""type"": [""object"", ""array""] },
                        ""dryRun"": { ""type"": ""boolean"" }
                    }
                }"),
                Annotations = new JObject { ["destructiveHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "content.reindex",
                Description = "Rebuild JsonContentManager's index and wait until the Editor reloads it.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Async,
                Timeout = TimeSpan.FromMinutes(2),
                Handler = Reindex,
                InputSchema = new JObject { ["type"] = "object" },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "content.reload",
                Description = "Reload JsonContentManager's current index and content in the Editor.",
                Availability = ToolAvailability.EditMode,
                Handler = Reload,
                InputSchema = new JObject { ["type"] = "object" },
            });
        }

        private static ValueTask<ToolResult> Status(ToolContext ctx, CancellationToken ct)
        {
            var manager = FindType("LittleBrushGames.JsonContentManager.Managers.ContentManager");
            var loaded = manager?.GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as bool?;
            var fileCount = Directory.Exists(Root)
                ? Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories).Count(path =>
                {
                    var normalized = path.Replace('\\', '/');
                    return !normalized.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase)
                        && !normalized.Contains("/_", StringComparison.Ordinal)
                        && !normalized.Contains("/^", StringComparison.Ordinal)
                        && !normalized.Contains("/.", StringComparison.Ordinal);
                })
                : 0;
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["installed"] = manager != null,
                ["loaded"] = loaded,
                ["root"] = Root,
                ["fileCount"] = fileCount,
            }));
        }

        private static ValueTask<ToolResult> Read(ToolContext ctx, CancellationToken ct)
        {
            var path = ValidatePath((string)ctx.Arguments["path"], allowGenerated: false);
            if (!File.Exists(path))
                throw new McpToolException(McpErrorCodes.NotFound, $"Content file not found: '{path}'.");
            var bytes = File.ReadAllBytes(path);
            return new ValueTask<ToolResult>(ToolResult.Ok(CreateReadResponse(
                path,
                Hash(bytes),
                JToken.Parse(Encoding.UTF8.GetString(bytes)),
                ctx.Arguments)));
        }

        internal static JObject CreateReadResponse(string path, string hash, JToken document, JObject args)
        {
            var pointer = (string)args["pointer"] ?? string.Empty;
            var detail = (string)args["detail"] ?? "summary";
            var offset = args["offset"]?.Value<int>() ?? 0;
            var limit = args["limit"]?.Value<int>() ?? 25;
            var maxCharacters = args["maxContentCharacters"]?.Value<int>() ?? 20000;
            if (offset < 0 || limit is < 1 or > 500)
                throw new McpToolException(McpErrorCodes.InvalidParams, "offset must be >= 0 and limit must be between 1 and 500.");
            if (maxCharacters is < 1000 or > 200000)
                throw new McpToolException(McpErrorCodes.InvalidParams, "maxContentCharacters must be between 1000 and 200000.");
            var selected = ResolvePointer(document, pointer);
            var children = Children(selected).ToList();
            var total = children.Count;
            var page = children.Skip(offset).Take(limit).ToList();
            var result = new JObject
            {
                ["path"] = path,
                ["hash"] = hash,
                ["pointer"] = pointer,
                ["type"] = TokenType(selected),
                ["detail"] = detail,
                ["total"] = total,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["truncated"] = offset + page.Count < total,
            };
            if (offset + page.Count < total)
                result["nextOffset"] = offset + page.Count;

            if (detail == "summary")
            {
                result["items"] = new JArray(page.Select(item => Summarize(item.Name, item.Index, item.Value)));
                return result;
            }
            if (detail != "full")
                throw new McpToolException(McpErrorCodes.InvalidParams, "detail must be summary or full.");

            JToken content;
            if (selected is JObject)
                content = new JObject(page.Select(item => new JProperty(item.Name, item.Value.DeepClone())));
            else if (selected is JArray)
                content = new JArray(page.Select(item => item.Value.DeepClone()));
            else
                content = selected.DeepClone();
            var characters = content.ToString(Newtonsoft.Json.Formatting.None).Length;
            if (characters > maxCharacters)
                throw new McpToolException(
                    McpErrorCodes.ValidationFailed,
                    $"Selected content page is {characters} characters; narrow pointer/limit or raise maxContentCharacters (current {maxCharacters}).",
                    new JObject { ["contentCharacters"] = characters, ["maxContentCharacters"] = maxCharacters, ["pointer"] = pointer });
            result["contentCharacters"] = characters;
            result["content"] = content;
            return result;
        }

        private static JToken ResolvePointer(JToken root, string pointer)
        {
            if (string.IsNullOrEmpty(pointer)) return root;
            if (!pointer.StartsWith("/", StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.InvalidParams, "pointer must be empty or start with '/'.");
            var current = root;
            foreach (var encoded in pointer.Substring(1).Split('/'))
            {
                var segment = encoded.Replace("~1", "/").Replace("~0", "~");
                current = current switch
                {
                    JObject obj => obj[segment],
                    JArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                    _ => null,
                };
                if (current == null)
                    throw new McpToolException(McpErrorCodes.NotFound, $"JSON pointer not found: '{pointer}'.");
            }
            return current;
        }

        private static System.Collections.Generic.IEnumerable<(string Name, int? Index, JToken Value)> Children(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                    yield return (property.Name, null, property.Value);
                yield break;
            }
            if (token is JArray array)
            {
                for (var index = 0; index < array.Count; index++)
                    yield return (null, index, array[index]);
                yield break;
            }
            yield return (null, null, token);
        }

        private static JObject Summarize(string name, int? index, JToken value)
        {
            var result = new JObject { ["type"] = TokenType(value) };
            if (name != null) result["key"] = name;
            if (index.HasValue) result["index"] = index.Value;
            if (value is JObject obj) result["count"] = obj.Count;
            else if (value is JArray array) result["count"] = array.Count;
            else if (value.Type == JTokenType.String)
            {
                var text = value.Value<string>() ?? string.Empty;
                result["value"] = text.Length <= 200 ? text : text.Substring(0, 200);
                if (text.Length > 200)
                {
                    result["valueTruncated"] = true;
                    result["valueCharacters"] = text.Length;
                }
            }
            else result["value"] = value.DeepClone();
            return result;
        }

        private static string TokenType(JToken token) => token.Type switch
        {
            JTokenType.Object => "object",
            JTokenType.Array => "array",
            JTokenType.String => "string",
            JTokenType.Integer => "integer",
            JTokenType.Float => "number",
            JTokenType.Boolean => "boolean",
            JTokenType.Null => "null",
            _ => token.Type.ToString().ToLowerInvariant(),
        };

        private static ValueTask<ToolResult> Write(ToolContext ctx, CancellationToken ct)
        {
            var path = ValidatePath((string)ctx.Arguments["path"], allowGenerated: false);
            var content = ctx.Arguments["content"];
            var existed = File.Exists(path);
            var previous = existed ? File.ReadAllBytes(path) : null;
            var currentHash = existed ? Hash(previous) : null;
            var expectedHash = (string)ctx.Arguments["expectedHash"];
            if (existed && string.IsNullOrEmpty(expectedHash))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "expectedHash is required when replacing an existing content file.");
            if (!string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict, $"Content file changed since read: '{path}'.", new JObject { ["expectedHash"] = expectedHash, ["currentHash"] = currentHash });

            var text = content.ToString(Newtonsoft.Json.Formatting.Indented) + Environment.NewLine;
            var bytes = new UTF8Encoding(false).GetBytes(text);
            if (ctx.Arguments["dryRun"]?.Value<bool>() == true)
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["path"] = path, ["hash"] = Hash(bytes), ["dryRun"] = true }));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                ScheduleReindex();
            }
            catch
            {
                if (existed) File.WriteAllBytes(path, previous);
                else if (File.Exists(path)) File.Delete(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                throw;
            }

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["path"] = path, ["hash"] = Hash(bytes), ["created"] = !existed, ["reindexScheduled"] = true }));
        }

        private static async ValueTask<ToolResult> Reindex(ToolContext ctx, CancellationToken ct)
        {
            await ScheduleReindexAsync();
            return ToolResult.Ok(new JObject { ["reindexed"] = true });
        }

        private static ValueTask<ToolResult> Reload(ToolContext ctx, CancellationToken ct)
        {
            var manager = FindType("LittleBrushGames.JsonContentManager.Managers.ContentManager")
                ?? throw new McpToolException(McpErrorCodes.ToolUnavailable, "JsonContentManager is not installed.");
            var instance = manager.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            manager.GetMethod("ReloadIndex", BindingFlags.Public | BindingFlags.Instance)?.Invoke(instance, null);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["reloaded"] = true }));
        }

        private static void ScheduleReindex()
        {
            var collector = FindType("LittleBrushGames.JsonContentManager.Editor.ContentCollector")
                ?? throw new McpToolException(McpErrorCodes.ToolUnavailable, "JsonContentManager Editor collector is not installed.");
            collector.GetMethod("RecalculateIndexAndContent", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }

        private static Task ScheduleReindexAsync()
        {
            var collector = FindType("LittleBrushGames.JsonContentManager.Editor.ContentCollector")
                ?? throw new McpToolException(McpErrorCodes.ToolUnavailable, "JsonContentManager Editor collector is not installed.");
            return collector.GetMethod("RecalculateIndexAndContentAsync", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as Task
                ?? throw new McpToolException(McpErrorCodes.ToolUnavailable, "JsonContentManager does not support waiting for content reindexing.");
        }

        public static string ValidatePath(string raw, bool allowGenerated)
        {
            var path = AssetProvider.NormalizeAssetPath(raw);
            if (!path.StartsWith(Root + "/", StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"Content path must be a .json file under {Root}/.");
            if (!allowGenerated && (path.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase) || path.Contains("/_", StringComparison.Ordinal)))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "Generated or system content files (index.json and /_ paths) cannot be edited directly.");
            return path;
        }

        private static Type FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(type => type != null);

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
