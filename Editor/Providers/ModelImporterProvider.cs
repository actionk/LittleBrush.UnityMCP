using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Host;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    /// <summary>
    /// Typed ModelImporter property tool. Closed whitelist of supported properties — never
    /// reflects arbitrary fields. Batch variant uses one StartAssetEditing/StopAssetEditing
    /// envelope so N imports cost a single pass.
    /// </summary>
    [McpToolProvider]
    public sealed class ModelImporterProvider : IToolProvider
    {
        public string Namespace => "model_importer";

        private static readonly HashSet<string> SupportedKeys = new(StringComparer.Ordinal)
        {
            "sourceAvatar",
            "animationType",
            "avatarSetup",
            "importAnimation",
            "importBlendShapes",
            "importVisibility",
            "importCameras",
            "importLights",
            "optimizeGameObjects",
            "useFileScale",
            "globalScale",
        };

        // Keep this list intentionally small and explicit. Events, curves, and masks
        // need separate schemas because they contain asset references and nested data.
        private static readonly string[] ClipKeys =
        {
            "name",
            "takeName",
            "firstFrame",
            "lastFrame",
            "wrapMode",
            "loopTime",
            "loopPose",
            "cycleOffset",
            "lockRootRotation",
            "keepOriginalOrientation",
            "rotationOffset",
            "lockRootHeightY",
            "keepOriginalPositionY",
            "heightFromFeet",
            "heightOffset",
            "lockRootPositionXZ",
            "keepOriginalPositionXZ",
            "mirror",
            "hasAdditiveReferencePose",
            "additiveReferencePoseFrame",
        };

        private static readonly HashSet<string> SupportedClipKeys = new(ClipKeys, StringComparer.Ordinal);
        private static readonly HashSet<string> MetaFallbackClipKeys = new(StringComparer.Ordinal)
        {
            "firstFrame",
            "lastFrame",
            "wrapMode",
            "loopTime",
            "loopPose",
            "cycleOffset",
            "lockRootRotation",
            "keepOriginalOrientation",
            "rotationOffset",
            "lockRootHeightY",
            "keepOriginalPositionY",
            "heightFromFeet",
            "heightOffset",
            "lockRootPositionXZ",
            "keepOriginalPositionXZ",
            "mirror",
            "hasAdditiveReferencePose",
            "additiveReferencePoseFrame",
        };

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "model_importer.get",
                Description = "Read whitelisted ModelImporter properties for a single asset.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path""],
                    ""properties"": { ""path"": { ""type"": ""string"" } }
                }"),
                Handler = Get,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "model_importer.set",
                Description = "Set one or more whitelisted ModelImporter properties. Reimports if any value changed. Whitelist: sourceAvatar, animationType, avatarSetup, importAnimation, importBlendShapes, importVisibility, importCameras, importLights, optimizeGameObjects, useFileScale, globalScale.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""properties""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""properties"": { ""type"": ""object"" }
                    }
                }"),
                Handler = Set,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "model_importer.set_many",
                Description = "Batch set whitelisted ModelImporter properties across many assets in a single StartAssetEditing/StopAssetEditing envelope. Replaces loops of GetAtPath+SaveAndReimport.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""paths"", ""properties""],
                    ""properties"": {
                        ""paths"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"" } },
                        ""properties"": { ""type"": ""object"" }
                    }
                }"),
                Handler = SetMany,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "model_importer.clips.get",
                Description = "Read stable primitive ModelImporterClipAnimation settings and an optimistic asset hash.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path""],
                    ""additionalProperties"": false,
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 256, ""description"": ""Page size. Default 25."" }
                    }
                }"),
                Handler = GetClips,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });

            reg.Register(new ToolDescriptor
            {
                Name = "model_importer.clips.set",
                Description = "Validate and set stable primitive ModelImporterClipAnimation settings by clip name. Requires expectedHash, supports dryRun and exact readback, and can opt into a scoped serialized .meta fallback when Unity rejects API persistence.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""expectedHash"", ""clipName"", ""properties""],
                    ""additionalProperties"": false,
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"", ""minLength"": 1 },
                        ""clipName"": { ""type"": ""string"", ""minLength"": 1 },
                        ""takeName"": { ""type"": ""string"" },
                        ""properties"": { ""type"": ""object"" },
                        ""dryRun"": { ""type"": ""boolean"" },
                        ""allowMetaFallback"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = SetClips,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "model_importer.clips.set_many",
                Description = "Batch validate and set stable primitive ModelImporterClipAnimation settings with per-asset expectedHash values, durable sequential reimports, and an optional scoped serialized .meta fallback.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""items""],
                    ""additionalProperties"": false,
                    ""properties"": {
                        ""dryRun"": { ""type"": ""boolean"" },
                        ""allowMetaFallback"": { ""type"": ""boolean"" },
                        ""items"": {
                            ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256,
                            ""items"": {
                                ""type"": ""object"",
                                ""required"": [""path"", ""expectedHash"", ""clipName"", ""properties""],
                                ""additionalProperties"": false,
                                ""properties"": {
                                    ""path"": { ""type"": ""string"" },
                                    ""expectedHash"": { ""type"": ""string"", ""minLength"": 1 },
                                    ""clipName"": { ""type"": ""string"", ""minLength"": 1 },
                                    ""takeName"": { ""type"": ""string"" },
                                    ""properties"": { ""type"": ""object"" }
                                }
                            }
                        }
                    }
                }"),
                Handler = SetManyClips,
            });
        }

        // ---- Handlers --------------------------------------------------------

        private static ValueTask<ToolResult> Get(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            var importer = RequireModelImporter(path);

            var props = new JObject();
            foreach (var key in SupportedKeys)
                props[key] = ReadProperty(importer, key);

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["path"] = path,
                ["properties"] = props,
            }));
        }

        private static ValueTask<ToolResult> Set(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            var props = ctx.Arguments["properties"] as JObject
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "properties required.");

            ValidateKeys(props);
            var result = ApplyOne(path, props);
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static ValueTask<ToolResult> SetMany(ToolContext ctx, CancellationToken ct)
        {
            var arr = ctx.Arguments["paths"] as JArray
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "paths required.");
            var props = ctx.Arguments["properties"] as JObject
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "properties required.");

            ValidateKeys(props);

            var results = new JArray();
            int reimportedCount = 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var t in arr)
                {
                    var raw = (string)t;
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        results.Add(new JObject { ["path"] = raw, ["error"] = "empty path" });
                        continue;
                    }
                    string path;
                    try { path = AssetProvider.NormalizeAssetPath(raw); }
                    catch (McpToolException ex) { results.Add(new JObject { ["path"] = raw, ["error"] = ex.Message }); continue; }

                    try
                    {
                        var one = ApplyOne(path, props);
                        if (one["reimported"]?.Value<bool>() == true) reimportedCount++;
                        results.Add(one);
                    }
                    catch (McpToolException ex)
                    {
                        results.Add(new JObject { ["path"] = path, ["error"] = ex.Message });
                    }
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["results"] = results,
                ["reimportedCount"] = reimportedCount,
            }));
        }

        private static ValueTask<ToolResult> GetClips(ToolContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            var importer = RequireModelImporter(path);
            var clips = GetClipAnimations(importer);
            var offset = ctx.Arguments["offset"]?.Value<int>() ?? 0;
            var limit = ctx.Arguments["limit"]?.Value<int>() ?? 25;
            if (offset < 0 || limit is < 1 or > 256)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "offset must be >= 0 and limit must be between 1 and 256.");
            var page = clips.Skip(offset).Take(limit).ToArray();
            var result = new JObject
            {
                ["path"] = path,
                ["hash"] = CurrentHash(path),
                ["openForEdit"] = AssetDatabase.IsOpenForEdit(path),
                ["clips"] = new JArray(),
                ["clipCount"] = clips.Length,
                ["offset"] = offset,
                ["returned"] = page.Length,
                ["truncated"] = offset + page.Length < clips.Length,
            };
            var output = (JArray)result["clips"];
            foreach (var clip in page)
                output.Add(DescribeClip(clip));
            if (offset + page.Length < clips.Length)
                result["nextOffset"] = offset + page.Length;
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static ValueTask<ToolResult> SetClips(ToolContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            var properties = ctx.Arguments["properties"] as JObject
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "properties required.");
            ValidateClipKeys(properties);
            var expectedHash = RequiredExpectedHash(ctx.Arguments);
            RequireHash(path, expectedHash);
            var clipName = RequiredClipName(ctx.Arguments);
            var takeName = OptionalString(ctx.Arguments, "takeName");
            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            var allowMetaFallback = ctx.Arguments["allowMetaFallback"]?.Value<bool>() == true;
            if (allowMetaFallback)
                ValidateMetaFallbackKeys(properties);
            return new ValueTask<ToolResult>(ToolResult.Ok(ApplyClipWrite(
                path, expectedHash, clipName, takeName, properties, dryRun, allowMetaFallback)));
        }

        private static ValueTask<ToolResult> SetManyClips(ToolContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var items = ctx.Arguments["items"] as JArray
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "items required.");
            if (items.Count is < 1 or > 256)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    "items must contain between 1 and 256 entries.");

            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            var allowMetaFallback = ctx.Arguments["allowMetaFallback"]?.Value<bool>() == true;
            var plans = new List<ClipWriteRequest>(items.Count);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in items)
            {
                ct.ThrowIfCancellationRequested();
                var item = token as JObject
                    ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "each item must be an object.");
                var path = AssetProvider.NormalizeAssetPath((string)item["path"]
                    ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "item.path required."));
                if (!paths.Add(path))
                    throw new McpToolException(McpErrorCodes.ValidationFailed,
                        $"items must not contain duplicate paths: '{path}'.");
                var properties = item["properties"] as JObject
                    ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "item.properties required.");
                ValidateClipKeys(properties);
                if (allowMetaFallback)
                    ValidateMetaFallbackKeys(properties);
                var expectedHash = RequiredExpectedHash(item);
                var clipName = RequiredClipName(item);
                plans.Add(new ClipWriteRequest(
                    path, expectedHash, clipName, OptionalString(item, "takeName"), properties,
                    allowMetaFallback));
            }

            // Validate every target and hash before changing any importer. This keeps a
            // malformed batch from partially applying earlier entries.
            var prepared = new List<PreparedClipWrite>(plans.Count);
            foreach (var plan in plans)
            {
                RequireHash(plan.Path, plan.ExpectedHash);
                prepared.Add(PrepareClipWrite(plan));
            }

            var results = new JArray();
            var reimportedCount = 0;
            foreach (var plan in prepared)
            {
                var result = ApplyPreparedClipWrite(plan, dryRun);
                if (result["reimported"]?.Value<bool>() == true) reimportedCount++;
                results.Add(result);
            }

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["results"] = results,
                ["reimportedCount"] = reimportedCount,
                ["dryRun"] = dryRun,
            }));
        }

        // ---- Core apply ------------------------------------------------------

        private static JObject ApplyOne(string path, JObject props)
        {
            var importer = RequireModelImporter(path);
            bool changed = false;
            foreach (var kv in props)
                changed |= WriteProperty(importer, kv.Key, kv.Value);

            bool reimported = false;
            if (changed)
            {
                importer.SaveAndReimport();
                reimported = true;
            }
            return new JObject
            {
                ["path"] = path,
                ["changed"] = changed,
                ["reimported"] = reimported,
            };
        }

        private static JObject ApplyClipWrite(string path, string expectedHash, string clipName,
            string takeName, JObject properties, bool dryRun, bool allowMetaFallback)
        {
            var plan = PrepareClipWrite(new ClipWriteRequest(
                path, expectedHash, clipName, takeName, properties, allowMetaFallback));
            return ApplyPreparedClipWrite(plan, dryRun);
        }

        private static PreparedClipWrite PrepareClipWrite(ClipWriteRequest request)
        {
            var importer = RequireModelImporter(request.Path);
            var clips = CreateWritableClipAnimations(importer);
            var targetIndex = FindClipIndex(clips, request.ClipName, request.TakeName);
            var target = clips[targetIndex];
            ValidateClipProperties(target, request.Properties);

            var proposedName = request.Properties["name"]?.Value<string>() ?? target.name;
            var proposedTakeName = request.Properties["takeName"]?.Value<string>() ?? target.takeName;
            for (var i = 0; i < clips.Length; i++)
            {
                if (i == targetIndex) continue;
                if (string.Equals(clips[i].name, proposedName, StringComparison.Ordinal)
                    && string.Equals(clips[i].takeName, proposedTakeName, StringComparison.Ordinal))
                    throw new McpToolException(McpErrorCodes.Conflict,
                        $"clip identity would collide with '{proposedName}' (take '{proposedTakeName}').");
            }

            return new PreparedClipWrite(request, targetIndex, target.name, target.takeName, clips,
                HasClipChanges(target, request.Properties));
        }

        private static JObject ApplyPreparedClipWrite(PreparedClipWrite plan, bool dryRun)
        {
            var currentHash = CurrentHash(plan.Request.Path);
            var result = new JObject
            {
                ["path"] = plan.Request.Path,
                ["clipName"] = plan.Request.ClipName,
                ["changed"] = plan.Changed,
                ["reimported"] = false,
                ["dryRun"] = dryRun,
                ["hash"] = currentHash,
                ["fallbackUsed"] = false,
                ["writeMethod"] = "none",
            };

            if (dryRun || !plan.Changed)
            {
                result["clip"] = DescribeClip(plan.Clips[plan.TargetIndex]);
                return result;
            }

            var target = plan.Clips[plan.TargetIndex];
            ApplyClipProperties(target, plan.Request.Properties);
            var originalMeta = plan.Request.AllowMetaFallback
                ? File.ReadAllBytes(GetMetaFilePath(plan.Request.Path))
                : null;
            var importer = RequireModelImporter(plan.Request.Path);
            importer.clipAnimations = plan.Clips;
            try
            {
                importer.SaveAndReimport();
            }
            catch
            {
                if (originalMeta != null)
                    RestoreMetaAndReimport(plan.Request.Path, originalMeta);
                throw;
            }

            importer = RequireModelImporter(plan.Request.Path);
            var clips = GetClipAnimations(importer);
            var targetIndex = FindClipIndexAfterWrite(clips, plan.TargetIndex,
                target.name, target.takeName);
            var mismatches = GetClipReadbackMismatches(clips[targetIndex], plan.Request.Properties);
            if (mismatches.Count > 0)
            {
                if (!plan.Request.AllowMetaFallback)
                    ThrowClipReadbackMismatch(plan.Request.Path, mismatches);

                try
                {
                    ApplySerializedMetaFallback(plan);
                    importer = RequireModelImporter(plan.Request.Path);
                    clips = GetClipAnimations(importer);
                    targetIndex = FindClipIndex(clips, plan.OriginalName, plan.OriginalTakeName);
                    VerifyClipReadback(plan.Request.Path, clips[targetIndex], plan.Request.Properties);
                    result["fallbackUsed"] = true;
                    result["writeMethod"] = "serializedMetaFallback";
                }
                catch (Exception fallbackError)
                {
                    try
                    {
                        RestoreMetaAndReimport(plan.Request.Path, originalMeta);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new McpToolException(
                            McpErrorCodes.ToolError,
                            $"Serialized .meta fallback and rollback both failed for '{plan.Request.Path}'.",
                            new JObject
                            {
                                ["fallbackError"] = fallbackError.Message,
                                ["rollbackError"] = rollbackError.Message,
                            });
                    }

                    throw new McpToolException(
                        McpErrorCodes.ToolError,
                        $"Serialized .meta fallback failed for '{plan.Request.Path}' and the original .meta was restored.",
                        new JObject { ["error"] = fallbackError.Message });
                }
            }
            else
            {
                result["writeMethod"] = "modelImporterApi";
            }

            result["clip"] = DescribeClip(clips[targetIndex]);
            result["clipName"] = clips[targetIndex].name;
            result["hash"] = CurrentHash(plan.Request.Path);
            result["reimported"] = true;
            result["verified"] = true;
            return result;
        }

        private static JArray GetClipReadbackMismatches(
            ModelImporterClipAnimation clip,
            JObject properties)
        {
            var mismatches = new JArray();
            foreach (var property in properties)
            {
                var actual = ReadClipProperty(clip, property.Key);
                if (ClipPropertyEquals(actual, property.Value))
                    continue;

                mismatches.Add(new JObject
                {
                    ["property"] = property.Key,
                    ["expected"] = property.Value.DeepClone(),
                    ["actual"] = actual,
                });
            }
            return mismatches;
        }

        private static void VerifyClipReadback(
            string path,
            ModelImporterClipAnimation clip,
            JObject properties)
        {
            var mismatches = GetClipReadbackMismatches(clip, properties);
            if (mismatches.Count > 0)
                ThrowClipReadbackMismatch(path, mismatches);
        }

        private static void ThrowClipReadbackMismatch(string path, JArray mismatches)
        {
            throw new McpToolException(
                McpErrorCodes.ToolError,
                $"Unity reimported '{path}' but did not persist the requested clip settings.",
                new JObject { ["mismatches"] = mismatches });
        }

        private static void ApplySerializedMetaFallback(PreparedClipWrite plan)
        {
            var metaPath = GetMetaFilePath(plan.Request.Path);
            var source = File.ReadAllText(metaPath);
            var patched = PatchClipMetaText(
                source,
                plan.OriginalName,
                plan.OriginalTakeName,
                plan.Request.Properties);
            AtomicWriteAllText(metaPath, patched);
            AssetDatabase.ImportAsset(plan.Request.Path, ImportAssetOptions.ForceUpdate);
        }

        private static void RestoreMetaAndReimport(string assetPath, byte[] originalMeta)
        {
            if (originalMeta == null)
                return;
            AtomicWriteAllBytes(GetMetaFilePath(assetPath), originalMeta);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        private static string GetMetaFilePath(string assetPath)
            => Path.GetFullPath(assetPath.Replace('/', Path.DirectorySeparatorChar) + ".meta");

        private static void AtomicWriteAllText(string path, string contents)
            => AtomicWriteAllBytes(path, new UTF8Encoding(false).GetBytes(contents));

        private static void AtomicWriteAllBytes(string path, byte[] contents)
        {
            var temporaryPath = path + ".mcp-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, contents);
                File.Replace(temporaryPath, path, null);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        internal static string PatchClipMetaText(
            string source,
            string clipName,
            string takeName,
            JObject properties)
        {
            ValidateMetaFallbackKeys(properties);
            var lines = SplitLinesWithEndings(source);
            var sectionStart = FindLine(lines, 0, lines.Count, "    clipAnimations:");
            if (sectionStart < 0)
                throw Validation("serialized .meta contains no clipAnimations section.");

            var sectionEnd = FindClipSectionEnd(lines, sectionStart + 1);
            var matches = new List<(int Start, int End)>();
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                if (!LineContent(lines[i]).StartsWith("    - serializedVersion:", StringComparison.Ordinal))
                    continue;
                var end = i + 1;
                while (end < sectionEnd
                    && !LineContent(lines[end]).StartsWith("    - serializedVersion:", StringComparison.Ordinal))
                    end++;
                if (ClipBlockMatches(lines, i, end, clipName, takeName))
                    matches.Add((i, end));
                i = end - 1;
            }

            if (matches.Count == 0)
                throw Validation($"serialized .meta clip not found: '{clipName}' (take '{takeName}').");
            if (matches.Count > 1)
                throw Validation($"serialized .meta contains more than one matching clip: '{clipName}' (take '{takeName}').");

            var block = matches[0];
            foreach (var property in properties)
            {
                var yamlKey = MetaYamlKey(property.Key);
                var lineIndex = FindLine(lines, block.Start, block.End, "      " + yamlKey + ":");
                if (lineIndex < 0)
                    throw Validation($"serialized .meta clip field '{yamlKey}' is missing.");
                var line = lines[lineIndex];
                var ending = line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
                    : line.EndsWith("\n", StringComparison.Ordinal) ? "\n"
                    : string.Empty;
                lines[lineIndex] = "      " + yamlKey + ": " + MetaYamlValue(property.Key, property.Value) + ending;
            }
            return string.Concat(lines);
        }

        private static List<string> SplitLinesWithEndings(string source)
        {
            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] != '\n') continue;
                lines.Add(source.Substring(start, i - start + 1));
                start = i + 1;
            }
            if (start < source.Length)
                lines.Add(source.Substring(start));
            return lines;
        }

        private static int FindClipSectionEnd(List<string> lines, int start)
        {
            for (var i = start; i < lines.Count; i++)
            {
                var line = LineContent(lines[i]);
                if (line.Length > 4 && line.StartsWith("    ", StringComparison.Ordinal)
                    && line[4] != ' ' && line[4] != '-')
                    return i;
            }
            return lines.Count;
        }

        private static bool ClipBlockMatches(
            List<string> lines,
            int start,
            int end,
            string clipName,
            string takeName)
        {
            var nameLine = FindLine(lines, start, end, "      name:");
            var takeLine = FindLine(lines, start, end, "      takeName:");
            if (nameLine < 0 || takeLine < 0)
                return false;
            return string.Equals(YamlScalar(lines[nameLine]), clipName, StringComparison.Ordinal)
                && string.Equals(YamlScalar(lines[takeLine]), takeName, StringComparison.Ordinal);
        }

        private static int FindLine(List<string> lines, int start, int end, string prefix)
        {
            for (var i = start; i < end; i++)
                if (LineContent(lines[i]).StartsWith(prefix, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private static string LineContent(string line)
            => line.TrimEnd('\r', '\n');

        private static string YamlScalar(string line)
        {
            var content = LineContent(line);
            var colon = content.IndexOf(':');
            return colon < 0 ? string.Empty : content.Substring(colon + 1).Trim();
        }

        private static string MetaYamlKey(string property) => property switch
        {
            "loopPose" => "loopBlend",
            "lockRootRotation" => "loopBlendOrientation",
            "rotationOffset" => "orientationOffsetY",
            "lockRootHeightY" => "loopBlendPositionY",
            "heightOffset" => "level",
            "lockRootPositionXZ" => "loopBlendPositionXZ",
            _ => property,
        };

        private static string MetaYamlValue(string property, JToken value)
        {
            if (property == "wrapMode")
                return ((int)ParseWrapMode(value)).ToString(CultureInfo.InvariantCulture);
            if (value.Type == JTokenType.Boolean)
                return value.Value<bool>() ? "1" : "0";
            return value.Value<float>().ToString("R", CultureInfo.InvariantCulture);
        }

        private static void ValidateMetaFallbackKeys(JObject properties)
        {
            foreach (var property in properties)
                if (!MetaFallbackClipKeys.Contains(property.Key))
                    throw Validation(
                        $"clip property '{property.Key}' cannot use serialized .meta fallback. " +
                        $"Supported fallback properties: {string.Join(", ", MetaFallbackClipKeys)}.");
        }

        private static int FindClipIndexAfterWrite(ModelImporterClipAnimation[] clips, int originalIndex,
            string name, string takeName)
        {
            if (originalIndex >= 0 && originalIndex < clips.Length
                && string.Equals(clips[originalIndex].name, name, StringComparison.Ordinal)
                && string.Equals(clips[originalIndex].takeName, takeName, StringComparison.Ordinal))
                return originalIndex;
            return FindClipIndex(clips, name, takeName);
        }

        private static ModelImporterClipAnimation[] GetClipAnimations(ModelImporter importer)
        {
            var clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0)
                clips = importer.defaultClipAnimations;
            if (clips == null || clips.Length == 0)
                throw new McpToolException(McpErrorCodes.NotFound,
                    "ModelImporter contains no animation clips.");
            return clips;
        }

        private static ModelImporterClipAnimation[] CloneClipAnimations(
            ModelImporterClipAnimation[] source)
        {
            var clones = new ModelImporterClipAnimation[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                clones[i] = new ModelImporterClipAnimation();
                CopyClipAnimation(source[i], clones[i]);
            }

            return clones;
        }

        private static ModelImporterClipAnimation[] CreateWritableClipAnimations(ModelImporter importer)
        {
            var current = GetClipAnimations(importer);
            var defaults = importer.defaultClipAnimations;
            if (defaults == null || defaults.Length != current.Length)
                return CloneClipAnimations(current);

            for (var i = 0; i < current.Length; i++)
                CopyClipAnimation(current[i], defaults[i]);
            return defaults;
        }

        private static void CopyClipAnimation(
            ModelImporterClipAnimation source,
            ModelImporterClipAnimation destination)
        {
            destination.name = source.name;
            destination.takeName = source.takeName;
            destination.firstFrame = source.firstFrame;
            destination.lastFrame = source.lastFrame;
            destination.wrapMode = source.wrapMode;
            destination.loop = source.loop;
            destination.loopTime = source.loopTime;
            destination.loopPose = source.loopPose;
            destination.cycleOffset = source.cycleOffset;
            destination.lockRootRotation = source.lockRootRotation;
            destination.keepOriginalOrientation = source.keepOriginalOrientation;
            destination.rotationOffset = source.rotationOffset;
            destination.lockRootHeightY = source.lockRootHeightY;
            destination.keepOriginalPositionY = source.keepOriginalPositionY;
            destination.heightFromFeet = source.heightFromFeet;
            destination.heightOffset = source.heightOffset;
            destination.lockRootPositionXZ = source.lockRootPositionXZ;
            destination.keepOriginalPositionXZ = source.keepOriginalPositionXZ;
            destination.mirror = source.mirror;
            destination.maskType = source.maskType;
            destination.maskSource = source.maskSource;
            destination.curves = source.curves;
            destination.events = source.events;
            destination.hasAdditiveReferencePose = source.hasAdditiveReferencePose;
            destination.additiveReferencePoseFrame = source.additiveReferencePoseFrame;
        }

        private static int FindClipIndex(ModelImporterClipAnimation[] clips, string clipName, string takeName)
        {
            var matches = new List<int>();
            for (var i = 0; i < clips.Length; i++)
            {
                if (!string.Equals(clips[i].name, clipName, StringComparison.Ordinal)) continue;
                if (takeName != null && !string.Equals(clips[i].takeName, takeName, StringComparison.Ordinal)) continue;
                matches.Add(i);
            }

            if (matches.Count == 0)
                throw new McpToolException(McpErrorCodes.NotFound,
                    $"ModelImporter clip not found: '{clipName}'" +
                    (takeName == null ? string.Empty : $" (take '{takeName}')") + ".");
            if (matches.Count > 1)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"More than one ModelImporter clip matched '{clipName}'. Pass takeName.");
            return matches[0];
        }

        private static JObject DescribeClip(ModelImporterClipAnimation clip)
        {
            var result = new JObject();
            foreach (var key in ClipKeys)
                result[key] = ReadClipProperty(clip, key);
            return result;
        }

        private static JToken ReadClipProperty(ModelImporterClipAnimation clip, string key) => key switch
        {
            "name" => clip.name,
            "takeName" => clip.takeName,
            "firstFrame" => clip.firstFrame,
            "lastFrame" => clip.lastFrame,
            "wrapMode" => clip.wrapMode.ToString(),
            "loopTime" => clip.loopTime,
            "loopPose" => clip.loopPose,
            "cycleOffset" => clip.cycleOffset,
            "lockRootRotation" => clip.lockRootRotation,
            "keepOriginalOrientation" => clip.keepOriginalOrientation,
            "rotationOffset" => clip.rotationOffset,
            "lockRootHeightY" => clip.lockRootHeightY,
            "keepOriginalPositionY" => clip.keepOriginalPositionY,
            "heightFromFeet" => clip.heightFromFeet,
            "heightOffset" => clip.heightOffset,
            "lockRootPositionXZ" => clip.lockRootPositionXZ,
            "keepOriginalPositionXZ" => clip.keepOriginalPositionXZ,
            "mirror" => clip.mirror,
            "hasAdditiveReferencePose" => clip.hasAdditiveReferencePose,
            "additiveReferencePoseFrame" => clip.additiveReferencePoseFrame,
            _ => null,
        };

        private static bool HasClipChanges(ModelImporterClipAnimation clip, JObject properties)
        {
            foreach (var property in properties)
            {
                if (!ClipPropertyEquals(ReadClipProperty(clip, property.Key), property.Value))
                    return true;
            }
            return false;
        }

        private static bool ClipPropertyEquals(JToken current, JToken expected)
        {
            if (JToken.DeepEquals(current, expected))
                return true;
            if (expected.Type != JTokenType.Float && expected.Type != JTokenType.Integer)
                return false;
            return Math.Abs(current.Value<float>() - expected.Value<float>()) <= 0.000001f;
        }

        private static void ApplyClipProperties(ModelImporterClipAnimation clip, JObject properties)
        {
            foreach (var property in properties)
            {
                switch (property.Key)
                {
                    case "name": clip.name = property.Value.Value<string>(); break;
                    case "takeName": clip.takeName = property.Value.Value<string>(); break;
                    case "firstFrame": clip.firstFrame = property.Value.Value<float>(); break;
                    case "lastFrame": clip.lastFrame = property.Value.Value<float>(); break;
                    case "wrapMode": clip.wrapMode = ParseWrapMode(property.Value); break;
                    case "loopTime": clip.loopTime = property.Value.Value<bool>(); break;
                    case "loopPose": clip.loopPose = property.Value.Value<bool>(); break;
                    case "cycleOffset": clip.cycleOffset = property.Value.Value<float>(); break;
                    case "lockRootRotation": clip.lockRootRotation = property.Value.Value<bool>(); break;
                    case "keepOriginalOrientation": clip.keepOriginalOrientation = property.Value.Value<bool>(); break;
                    case "rotationOffset": clip.rotationOffset = property.Value.Value<float>(); break;
                    case "lockRootHeightY": clip.lockRootHeightY = property.Value.Value<bool>(); break;
                    case "keepOriginalPositionY": clip.keepOriginalPositionY = property.Value.Value<bool>(); break;
                    case "heightFromFeet": clip.heightFromFeet = property.Value.Value<bool>(); break;
                    case "heightOffset": clip.heightOffset = property.Value.Value<float>(); break;
                    case "lockRootPositionXZ": clip.lockRootPositionXZ = property.Value.Value<bool>(); break;
                    case "keepOriginalPositionXZ": clip.keepOriginalPositionXZ = property.Value.Value<bool>(); break;
                    case "mirror": clip.mirror = property.Value.Value<bool>(); break;
                    case "hasAdditiveReferencePose": clip.hasAdditiveReferencePose = property.Value.Value<bool>(); break;
                    case "additiveReferencePoseFrame": clip.additiveReferencePoseFrame = property.Value.Value<float>(); break;
                }
            }
        }

        private static void ValidateClipKeys(JObject properties)
        {
            foreach (var property in properties)
            {
                if (!SupportedClipKeys.Contains(property.Key))
                    throw new McpToolException(McpErrorCodes.ValidationFailed,
                        $"clip property '{property.Key}' is not in the whitelist. Supported: {string.Join(", ", ClipKeys)}.");
            }
        }

        private static void ValidateClipProperties(ModelImporterClipAnimation current, JObject properties)
        {
            foreach (var property in properties)
            {
                var value = property.Value;
                switch (property.Key)
                {
                    case "name":
                        RequireType(property.Key, value, JTokenType.String);
                        if (string.IsNullOrWhiteSpace(value.Value<string>()))
                            throw Validation($"{property.Key}: non-empty string required.");
                        break;
                    case "takeName":
                        RequireType(property.Key, value, JTokenType.String);
                        break;
                    case "wrapMode":
                        ParseWrapMode(value);
                        break;
                    case "loopTime": case "loopPose": case "lockRootRotation":
                    case "keepOriginalOrientation": case "lockRootHeightY":
                    case "keepOriginalPositionY": case "heightFromFeet":
                    case "lockRootPositionXZ": case "keepOriginalPositionXZ":
                    case "mirror": case "hasAdditiveReferencePose":
                        RequireType(property.Key, value, JTokenType.Boolean);
                        break;
                    case "firstFrame": case "lastFrame": case "cycleOffset":
                    case "rotationOffset": case "heightOffset": case "additiveReferencePoseFrame":
                        RequireFiniteFloat(property.Key, value);
                        break;
                }
            }

            var first = FloatValue(properties, "firstFrame", current.firstFrame);
            var last = FloatValue(properties, "lastFrame", current.lastFrame);
            if (first < 0 || last < first)
                throw Validation("firstFrame must be >= 0 and lastFrame must be >= firstFrame.");
            var cycle = FloatValue(properties, "cycleOffset", current.cycleOffset);
            if (cycle < 0 || cycle > 1)
                throw Validation("cycleOffset must be between 0 and 1.");
            var additiveEnabled = BoolValue(properties, "hasAdditiveReferencePose", current.hasAdditiveReferencePose);
            var additiveFrame = FloatValue(properties, "additiveReferencePoseFrame", current.additiveReferencePoseFrame);
            if (additiveEnabled && (additiveFrame < first || additiveFrame > last))
                throw Validation("additiveReferencePoseFrame must be within firstFrame and lastFrame when hasAdditiveReferencePose is true.");
        }

        private static float FloatValue(JObject properties, string key, float fallback)
            => properties[key] == null ? fallback : properties[key].Value<float>();

        private static bool BoolValue(JObject properties, string key, bool fallback)
            => properties[key] == null ? fallback : properties[key].Value<bool>();

        private static void RequireFiniteFloat(string key, JToken value)
        {
            if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer)
                throw Validation($"{key} must be a finite number.");
            var parsed = value.Value<float>();
            if (float.IsNaN(parsed) || float.IsInfinity(parsed))
                throw Validation($"{key} must be a finite number.");
        }

        private static WrapMode ParseWrapMode(JToken value)
        {
            if (value?.Type != JTokenType.String
                || !Enum.TryParse<WrapMode>(value.Value<string>(), true, out var parsed)
                || !Enum.IsDefined(typeof(WrapMode), parsed))
                throw Validation($"wrapMode: '{value}' is invalid. Allowed: {string.Join(", ", Enum.GetNames(typeof(WrapMode)))}.");
            return parsed;
        }

        private static void RequireType(string key, JToken value, JTokenType expected)
        {
            if (value == null || value.Type != expected)
                throw Validation($"{key} must be {expected.ToString().ToLowerInvariant()}.");
        }

        private static McpToolException Validation(string message)
            => new(McpErrorCodes.ValidationFailed, message);

        private static string RequiredExpectedHash(JToken arguments)
        {
            var value = arguments["expectedHash"];
            if (value?.Type != JTokenType.String || string.IsNullOrWhiteSpace(value.Value<string>()))
                throw Validation("expectedHash: non-empty string required.");
            return value.Value<string>();
        }

        private static string RequiredClipName(JToken arguments)
        {
            var value = arguments["clipName"] ?? arguments["name"];
            if (value?.Type != JTokenType.String || string.IsNullOrWhiteSpace(value.Value<string>()))
                throw Validation("clipName: non-empty string required.");
            return value.Value<string>();
        }

        private static string OptionalString(JToken arguments, string key)
        {
            var value = arguments[key];
            if (value == null) return null;
            if (value.Type != JTokenType.String)
                throw Validation($"{key} must be a string.");
            return value.Value<string>();
        }

        private static string CurrentHash(string path)
            => AssetDatabase.GetAssetDependencyHash(path).ToString();

        private static void RequireHash(string path, string expectedHash)
        {
            var actual = CurrentHash(path);
            if (!string.Equals(expectedHash, actual, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict,
                    $"ModelImporter changed since read: '{path}'.",
                    new JObject { ["expectedHash"] = expectedHash, ["currentHash"] = actual });
        }

        private sealed class ClipWriteRequest
        {
            public ClipWriteRequest(string path, string expectedHash, string clipName, string takeName,
                JObject properties, bool allowMetaFallback)
            {
                Path = path;
                ExpectedHash = expectedHash;
                ClipName = clipName;
                TakeName = takeName;
                Properties = properties;
                AllowMetaFallback = allowMetaFallback;
            }

            public string Path { get; }
            public string ExpectedHash { get; }
            public string ClipName { get; }
            public string TakeName { get; }
            public JObject Properties { get; }
            public bool AllowMetaFallback { get; }
        }

        private sealed class PreparedClipWrite
        {
            public PreparedClipWrite(ClipWriteRequest request, int targetIndex, string originalName,
                string originalTakeName, ModelImporterClipAnimation[] clips, bool changed)
            {
                Request = request;
                TargetIndex = targetIndex;
                OriginalName = originalName;
                OriginalTakeName = originalTakeName;
                Clips = clips;
                Changed = changed;
            }

            public ClipWriteRequest Request { get; }
            public int TargetIndex { get; }
            public string OriginalName { get; }
            public string OriginalTakeName { get; }
            public ModelImporterClipAnimation[] Clips { get; }
            public bool Changed { get; }
        }

        // ---- Property I/O ----------------------------------------------------

        private static JToken ReadProperty(ModelImporter m, string key) => key switch
        {
            "sourceAvatar" => m.sourceAvatar != null ? AssetDatabase.GetAssetPath(m.sourceAvatar) : null,
            "animationType" => m.animationType.ToString(),
            "avatarSetup" => m.avatarSetup.ToString(),
            "importAnimation" => m.importAnimation,
            "importBlendShapes" => m.importBlendShapes,
            "importVisibility" => m.importVisibility,
            "importCameras" => m.importCameras,
            "importLights" => m.importLights,
            "optimizeGameObjects" => m.optimizeGameObjects,
            "useFileScale" => m.useFileScale,
            "globalScale" => m.globalScale,
            _ => null,
        };

        /// <summary>Returns true if the value actually changed.</summary>
        private static bool WriteProperty(ModelImporter m, string key, JToken value)
        {
            switch (key)
            {
                case "sourceAvatar":
                {
                    Avatar next = null;
                    if (value != null && value.Type != JTokenType.Null)
                    {
                        var avatarPath = AssetProvider.NormalizeAssetPath((string)value);
                        next = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath)
                            ?? throw new McpToolException(McpErrorCodes.NotFound,
                                $"sourceAvatar: no Avatar asset at '{avatarPath}'.");
                    }
                    if (ReferenceEquals(m.sourceAvatar, next)) return false;
                    m.sourceAvatar = next;
                    return true;
                }
                case "animationType":
                {
                    var parsed = ParseEnum<ModelImporterAnimationType>("animationType", (string)value);
                    if (m.animationType == parsed) return false;
                    m.animationType = parsed; return true;
                }
                case "avatarSetup":
                {
                    var parsed = ParseEnum<ModelImporterAvatarSetup>("avatarSetup", (string)value);
                    if (m.avatarSetup == parsed) return false;
                    m.avatarSetup = parsed; return true;
                }
                case "importAnimation": return AssignBool(ref m, key, (bool)value, (im, v) => im.importAnimation = v, im => im.importAnimation);
                case "importBlendShapes": return AssignBool(ref m, key, (bool)value, (im, v) => im.importBlendShapes = v, im => im.importBlendShapes);
                case "importVisibility": return AssignBool(ref m, key, (bool)value, (im, v) => im.importVisibility = v, im => im.importVisibility);
                case "importCameras": return AssignBool(ref m, key, (bool)value, (im, v) => im.importCameras = v, im => im.importCameras);
                case "importLights": return AssignBool(ref m, key, (bool)value, (im, v) => im.importLights = v, im => im.importLights);
                case "optimizeGameObjects": return AssignBool(ref m, key, (bool)value, (im, v) => im.optimizeGameObjects = v, im => im.optimizeGameObjects);
                case "useFileScale": return AssignBool(ref m, key, (bool)value, (im, v) => im.useFileScale = v, im => im.useFileScale);
                case "globalScale":
                {
                    var next = (float)value;
                    if (Math.Abs(m.globalScale - next) < float.Epsilon) return false;
                    m.globalScale = next; return true;
                }
                default:
                    throw new McpToolException(McpErrorCodes.ValidationFailed, $"unsupported property '{key}'.");
            }
        }

        private static bool AssignBool(ref ModelImporter m, string key, bool next,
            Action<ModelImporter, bool> setter, Func<ModelImporter, bool> getter)
        {
            if (getter(m) == next) return false;
            setter(m, next); return true;
        }

        // ---- Helpers ---------------------------------------------------------

        private static ModelImporter RequireModelImporter(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"no ModelImporter at '{path}' (asset missing or not a model).");
            return importer;
        }

        private static void ValidateKeys(JObject props)
        {
            foreach (var kv in props)
            {
                if (!SupportedKeys.Contains(kv.Key))
                    throw new McpToolException(McpErrorCodes.ValidationFailed,
                        $"property '{kv.Key}' is not in the whitelist. Supported: {string.Join(", ", SupportedKeys)}.");
            }
        }

        private static T ParseEnum<T>(string key, string value) where T : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"{key}: non-empty string required.");
            if (!Enum.TryParse<T>(value, ignoreCase: false, out var parsed))
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"{key}: '{value}' is not a valid {typeof(T).Name}. Allowed: {string.Join(", ", Enum.GetNames(typeof(T)))}.");
            return parsed;
        }
    }
}
