#if HAS_UNITY_TIMELINE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp;
using LittleBrushGames.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Modules.Timeline
{
    [McpToolProvider]
    public sealed class AnimationClipCurveProvider : IToolProvider
    {
        private const int MaxOperations = 128;
        private const int MaxKeys = 4096;

        public string Namespace => "asset.animation_clip.curves";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "asset.animation_clip.curves.read",
                Description = "Read paged float and object-reference curves from a selected AnimationClip, including imported clip sub-assets. Key detail defaults to 25 keys for one binding.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""clipName"": { ""type"": ""string"" },
                        ""clipLocalId"": { ""type"": [""integer"", ""string""] },
                        ""detail"": { ""type"": ""string"", ""enum"": [""bindings"", ""keys""] },
                        ""query"": { ""type"": ""string"" },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200 },
                        ""keyOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""keyLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200 }
                    }
                }"),
                Handler = Read,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "asset.animation_clip.curves.write",
                Description = "Atomically set or remove float curves on a standalone .anim asset. Requires expectedHash and supports dryRun.",
                Availability = ToolAvailability.EditMode,
                ExclusiveGroup = "animation-curve-write",
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path"", ""expectedHash"", ""operations""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"" },
                        ""dryRun"": { ""type"": ""boolean"" },
                        ""operations"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 128, ""items"": { ""type"": ""object"" } }
                    }
                }"),
                Handler = Write,
            });
        }

        private static ValueTask<ToolResult> Read(ToolContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = NormalizePath((string)ctx.Arguments["path"]);
            var clip = SelectClip(path, ctx.Arguments);
            return new ValueTask<ToolResult>(ToolResult.Ok(ReadDocument(clip, path, ctx.Arguments)));
        }

        private static ValueTask<ToolResult> Write(ToolContext ctx, CancellationToken ct)
        {
            var path = NormalizePath((string)ctx.Arguments["path"]);
            var clip = SelectClip(path, ctx.Arguments);
            if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) || AssetDatabase.IsSubAsset(clip) || AssetDatabase.LoadMainAssetAtPath(path) != clip)
                throw TimelineProvider.Validation("Only standalone, project-owned .anim assets are writable. Extract imported clips with asset.animation_clip.extract first.");
            TimelineProvider.RequireHash(path, (string)ctx.Arguments["expectedHash"]);

            var operations = ctx.Arguments["operations"] as JArray ?? throw TimelineProvider.Validation("operations must be an array.");
            if (operations.Count is < 1 or > MaxOperations)
                throw TimelineProvider.Validation($"operations must contain 1 to {MaxOperations} items.");

            var actions = new List<Action>();
            var changes = new JArray();
            var targets = new HashSet<string>(StringComparer.Ordinal);
            var keyCount = 0;
            foreach (var token in operations)
            {
                ct.ThrowIfCancellationRequested();
                var op = token as JObject ?? throw TimelineProvider.Validation("Each operation must be an object.");
                var kind = RequiredString(op, "op");
                if (kind is not ("set_curve" or "remove_curve"))
                    throw TimelineProvider.Validation($"Unsupported animation curve operation: '{kind}'.");
                var binding = ParseBinding(op["binding"] as JObject);
                var target = binding.path + "\n" + binding.type.AssemblyQualifiedName + "\n" + binding.propertyName;
                if (!targets.Add(target)) throw TimelineProvider.Validation("Each curve binding may only be edited once per request.");

                if (kind == "remove_curve")
                {
                    actions.Add(() => AnimationUtility.SetEditorCurve(clip, binding, null));
                }
                else
                {
                    var curve = ParseCurve(op["curve"] as JObject, ref keyCount);
                    actions.Add(() => AnimationUtility.SetEditorCurve(clip, binding, curve));
                }
                changes.Add(new JObject { ["op"] = kind, ["binding"] = DescribeBinding(binding) });
            }
            if (keyCount > MaxKeys) throw TimelineProvider.Validation($"Request exceeds the {MaxKeys} keyframe limit.");

            if (ctx.Arguments["dryRun"]?.Value<bool>() == true)
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["dryRun"] = true,
                    ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                    ["changes"] = changes,
                }));

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP Animation Curve Write");
            Undo.RegisterCompleteObjectUndo(clip, "MCP Animation Curve Write");
            try
            {
                foreach (var action in actions) action();
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssetIfDirty(clip);
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
            Undo.CollapseUndoOperations(undoGroup);

            var document = ReadDocument(clip, path, new JObject());
            document["dryRun"] = false;
            document["changes"] = changes;
            return new ValueTask<ToolResult>(ToolResult.Ok(document));
        }

        private static JObject ReadDocument(AnimationClip clip, string path, JObject args)
        {
            var detail = (string)args["detail"] ?? "bindings";
            var query = ((string)args["query"] ?? string.Empty).Trim();
            var offset = args["offset"]?.Value<int>() ?? 0;
            var limit = args["limit"]?.Value<int>() ?? (detail == "keys" ? 1 : 25);
            var keyOffset = args["keyOffset"]?.Value<int>() ?? 0;
            var keyLimit = args["keyLimit"]?.Value<int>() ?? 25;
            if (detail is not ("bindings" or "keys")) throw TimelineProvider.Validation("detail must be bindings or keys.");
            if (offset < 0 || limit is < 1 or > 200) throw TimelineProvider.Validation("offset must be >= 0 and limit must be between 1 and 200.");
            if (keyOffset < 0 || keyLimit is < 1 or > 200) throw TimelineProvider.Validation("keyOffset must be >= 0 and keyLimit must be between 1 and 200.");
            if (detail == "keys" && limit > 5) throw TimelineProvider.Validation("detail=keys supports at most 5 bindings per response.");

            var entries = new List<(string Key, string Kind, EditorCurveBinding Binding, AnimationCurve Curve, ObjectReferenceKeyframe[] ObjectKeys)>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip).OrderBy(BindingKey, StringComparer.Ordinal))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                entries.Add((BindingKey(binding), "float", binding, curve, null));
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip).OrderBy(BindingKey, StringComparer.Ordinal))
                entries.Add((BindingKey(binding), "objectReference", binding, null,
                    AnimationUtility.GetObjectReferenceCurve(clip, binding) ?? Array.Empty<ObjectReferenceKeyframe>()));

            var filtered = entries.Where(entry => string.IsNullOrEmpty(query)
                    || entry.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(entry => entry.Key, StringComparer.Ordinal).ThenBy(entry => entry.Kind, StringComparer.Ordinal).ToList();
            var curves = new JArray();
            foreach (var entry in filtered.Skip(offset).Take(limit))
            {
                var keyCount = entry.Curve?.length ?? entry.ObjectKeys.Length;
                var item = new JObject
                {
                    ["kind"] = entry.Kind,
                    ["binding"] = DescribeBinding(entry.Binding),
                    ["keyCount"] = keyCount,
                };
                if (entry.Curve != null)
                {
                    item["preWrapMode"] = entry.Curve.preWrapMode.ToString();
                    item["postWrapMode"] = entry.Curve.postWrapMode.ToString();
                }
                if (detail == "keys")
                {
                    var keys = new JArray();
                    var end = Math.Min(keyCount, keyOffset + keyLimit);
                    for (var index = keyOffset; index < end; index++)
                    {
                        if (entry.Curve == null)
                        {
                            var key = entry.ObjectKeys[index];
                            keys.Add(new JObject { ["time"] = key.time, ["value"] = TimelineProvider.DescribeObject(key.value) });
                            continue;
                        }
                        var keyframe = entry.Curve[index];
                        keys.Add(new JObject
                        {
                            ["time"] = keyframe.time,
                            ["value"] = keyframe.value,
                            ["inTangent"] = keyframe.inTangent,
                            ["outTangent"] = keyframe.outTangent,
                            ["inWeight"] = keyframe.inWeight,
                            ["outWeight"] = keyframe.outWeight,
                            ["weightedMode"] = keyframe.weightedMode.ToString(),
                            ["leftTangentMode"] = AnimationUtility.GetKeyLeftTangentMode(entry.Curve, index).ToString(),
                            ["rightTangentMode"] = AnimationUtility.GetKeyRightTangentMode(entry.Curve, index).ToString(),
                        });
                    }
                    item["keyOffset"] = keyOffset;
                    item["keyReturned"] = keys.Count;
                    item["keysTruncated"] = keyOffset + keys.Count < keyCount;
                    if (keyOffset + keys.Count < keyCount) item["nextKeyOffset"] = keyOffset + keys.Count;
                    item["keys"] = keys;
                }
                curves.Add(item);
            }

            var result = new JObject
            {
                ["schemaVersion"] = 1,
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                ["clip"] = TimelineProvider.DescribeObject(clip),
                ["writable"] = path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) && !AssetDatabase.IsSubAsset(clip) && AssetDatabase.LoadMainAssetAtPath(path) == clip,
                ["frameRate"] = clip.frameRate,
                ["length"] = clip.length,
                ["detail"] = detail,
                ["total"] = entries.Count,
                ["filtered"] = filtered.Count,
                ["offset"] = offset,
                ["returned"] = curves.Count,
                ["truncated"] = offset + curves.Count < filtered.Count,
                ["curves"] = curves,
            };
            if (offset + curves.Count < filtered.Count) result["nextOffset"] = offset + curves.Count;
            return result;
        }

        private static AnimationCurve ParseCurve(JObject value, ref int totalKeys)
        {
            if (value == null) throw TimelineProvider.Validation("curve is required for set_curve.");
            var tokens = value["keys"] as JArray ?? throw TimelineProvider.Validation("curve.keys must be an array.");
            totalKeys += tokens.Count;
            var curve = new AnimationCurve();
            var tangentModes = new List<(AnimationUtility.TangentMode left, AnimationUtility.TangentMode right)>();
            var lastTime = float.NegativeInfinity;
            foreach (var token in tokens)
            {
                var item = token as JObject ?? throw TimelineProvider.Validation("Each curve key must be an object.");
                var time = FiniteFloat(item, "time");
                if (time < 0 || time <= lastTime) throw TimelineProvider.Validation("Curve key times must be >= 0 and strictly increasing.");
                lastTime = time;
                var key = new Keyframe(time, FiniteFloat(item, "value"), OptionalFiniteFloat(item, "inTangent"), OptionalFiniteFloat(item, "outTangent"),
                    OptionalFiniteFloat(item, "inWeight"), OptionalFiniteFloat(item, "outWeight"));
                if (item["weightedMode"] != null) key.weightedMode = ParseEnum<WeightedMode>(item, "weightedMode");
                curve.AddKey(key);
                tangentModes.Add((OptionalEnum(item, "leftTangentMode", AnimationUtility.TangentMode.Free), OptionalEnum(item, "rightTangentMode", AnimationUtility.TangentMode.Free)));
            }
            curve.preWrapMode = OptionalEnum(value, "preWrapMode", WrapMode.Default);
            curve.postWrapMode = OptionalEnum(value, "postWrapMode", WrapMode.Default);
            for (var index = 0; index < tangentModes.Count; index++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, index, tangentModes[index].left);
                AnimationUtility.SetKeyRightTangentMode(curve, index, tangentModes[index].right);
            }
            return curve;
        }

        private static EditorCurveBinding ParseBinding(JObject value)
        {
            if (value == null) throw TimelineProvider.Validation("binding is required.");
            var typeName = RequiredString(value, "componentType");
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(typeName, false))
                .FirstOrDefault(candidate => candidate != null)
                ?? Type.GetType(typeName, false);
            if (type == null) throw TimelineProvider.Validation($"Component type was not found: '{typeName}'.");
            return EditorCurveBinding.FloatCurve((string)value["path"] ?? string.Empty, type, RequiredString(value, "propertyName"));
        }

        private static JObject DescribeBinding(EditorCurveBinding binding) => new()
        {
            ["path"] = binding.path,
            ["componentType"] = binding.type.FullName,
            ["propertyName"] = binding.propertyName,
        };

        private static string BindingKey(EditorCurveBinding binding)
            => binding.path + "\n" + binding.type.FullName + "\n" + binding.propertyName;

        private static AnimationClip SelectClip(string path, JObject args)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Asset not found: '{path}'.");
            var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
            var name = ((string)args["clipName"])?.Trim();
            var hasLocalId = args["clipLocalId"] != null && args["clipLocalId"].Type != JTokenType.Null;
            long localId = 0;
            if (hasLocalId && !long.TryParse(args["clipLocalId"].ToString(), out localId))
                throw TimelineProvider.Validation("clipLocalId must be a 64-bit integer.");
            var matches = hasLocalId
                ? clips.Where(clip => TimelineProvider.LocalId(clip) == localId).ToArray()
                : clips.Where(clip => string.IsNullOrEmpty(name) || clip.name == name).ToArray();
            if (matches.Length == 0) throw new McpToolException(McpErrorCodes.NotFound, $"AnimationClip not found in '{path}'.");
            if (matches.Length > 1) throw new McpToolException(McpErrorCodes.Conflict, $"More than one AnimationClip matched in '{path}'. Pass clipName or clipLocalId.");
            return matches[0];
        }

        private static string NormalizePath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || System.IO.Path.IsPathRooted(raw)) throw TimelineProvider.Validation("path must be project-relative.");
            var path = raw.Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) throw TimelineProvider.Validation("path must be under Assets/.");
            return path;
        }

        private static float FiniteFloat(JObject obj, string name)
        {
            if (obj[name] == null) throw TimelineProvider.Validation($"{name} is required.");
            return OptionalFiniteFloat(obj, name);
        }

        private static float OptionalFiniteFloat(JObject obj, string name)
        {
            float value;
            try { value = obj[name]?.Value<float>() ?? 0; }
            catch { throw TimelineProvider.Validation($"{name} must be a number."); }
            if (float.IsNaN(value) || float.IsInfinity(value)) throw TimelineProvider.Validation($"{name} must be finite.");
            return value;
        }

        private static T ParseEnum<T>(JObject obj, string name) where T : struct
        {
            if (obj[name]?.Type != JTokenType.String || !Enum.TryParse((string)obj[name], true, out T value) || !Enum.IsDefined(typeof(T), value))
                throw TimelineProvider.Validation($"Invalid {name}: '{obj[name]}'.");
            return value;
        }

        private static T OptionalEnum<T>(JObject obj, string name, T fallback) where T : struct
            => obj[name] == null ? fallback : ParseEnum<T>(obj, name);

        private static string RequiredString(JObject obj, string name)
            => obj[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)obj[name])
                ? ((string)obj[name]).Trim()
                : throw TimelineProvider.Validation($"{name} is required.");
    }
}
#endif
