using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Host;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    /// <summary>
    /// Typed wrappers around common <see cref="AssetDatabase"/> operations. Replaces the
    /// bespoke execute_code snippets that agents reach for when no typed tool exists.
    /// Every path argument is validated project-relative and constrained to Assets/ or Packages/.
    /// </summary>
    [McpToolProvider]
    public sealed class AssetProvider : IToolProvider
    {
        public string Namespace => "asset";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "asset.find",
                Description = "Find assets by type, optional folder scope, and optional label. Returns path + guid + type. Paginated via cursor. Preferred over execute_code+AssetDatabase.FindAssets.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""type"": { ""type"": ""string"", ""description"": ""Asset type filter (e.g. 'Model', 'Prefab', 'Material', 'Texture2D')."" },
                        ""inFolders"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional folder scope, project-relative."" },
                        ""label"": { ""type"": ""string"", ""description"": ""Optional asset label filter."" },
                        ""nameContains"": { ""type"": ""string"", ""description"": ""Optional free-text filter matched against file name."" },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""description"": ""Page size. Default 25."" },
                        ""cursor"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Offset into the result set from a prior call."" }
                    }
                }"),
                Handler = Find,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "asset.create_folder",
                Description = "Create a folder under Assets/ or Packages/ if it does not already exist. Idempotent.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""parent"", ""name""],
                    ""properties"": {
                        ""parent"": { ""type"": ""string"", ""description"": ""Project-relative parent folder (e.g. 'Assets/Content')."" },
                        ""name"": { ""type"": ""string"", ""description"": ""New folder name."" }
                    }
                }"),
                Handler = CreateFolder,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "asset.copy",
                Description = "Copy an asset via AssetDatabase.CopyAsset. Safer than Instantiate+SaveAsPrefabAsset — does not touch the scene.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""src"", ""dest""],
                    ""properties"": {
                        ""src"": { ""type"": ""string"" },
                        ""dest"": { ""type"": ""string"" },
                        ""overwrite"": { ""type"": ""boolean"", ""description"": ""If true and dest exists, replace it after copying to a temporary path. Default false."" }
                    }
                }"),
                Handler = Copy,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "asset.move",
                Description = "Move or rename an asset via AssetDatabase.MoveAsset.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""src"", ""dest""],
                    ""properties"": {
                        ""src"": { ""type"": ""string"" },
                        ""dest"": { ""type"": ""string"" }
                    }
                }"),
                Handler = Move,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "asset.delete",
                Description = "Delete one or more assets. Returns paths that were deleted and those that did not exist.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""paths""],
                    ""properties"": {
                        ""paths"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256, ""items"": { ""type"": ""string"" } }
                    }
                }"),
                Handler = Delete,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "asset.import",
                Description = "Reimport one or more assets in a single StartAssetEditing/StopAssetEditing envelope so the cost is amortised.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""paths""],
                    ""properties"": {
                        ""paths"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256, ""items"": { ""type"": ""string"" } },
                        ""force"": { ""type"": ""boolean"", ""description"": ""If true, uses ImportAssetOptions.ForceUpdate."" }
                    }
                }"),
                Handler = Import,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "asset.subassets.list",
                Description = "Page the main asset and imported sub-assets at a path. Use localId with the source path for exact follow-up operations.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""type"": { ""type"": ""string"", ""description"": ""Optional type name, for example AnimationClip."" },
                        ""nameContains"": { ""type"": ""string"" },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""description"": ""Page size. Default 25."" }
                    }
                }"),
                Handler = ListSubAssets,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "asset.animation_clip.extract",
                Description = "Copy an imported AnimationClip sub-asset (for example from an FBX) into a standalone project-owned .anim asset.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""sourcePath"", ""destinationPath""],
                    ""properties"": {
                        ""sourcePath"": { ""type"": ""string"" },
                        ""destinationPath"": { ""type"": ""string"", ""description"": ""Project-owned output path ending in .anim."" },
                        ""clipName"": { ""type"": ""string"", ""description"": ""Exact imported clip name. Optional when the source contains one clip."" },
                        ""clipLocalId"": { ""type"": ""integer"", ""description"": ""Stable local file ID returned by asset.subassets.list; disambiguates duplicate names."" },
                        ""overwrite"": { ""type"": ""boolean"", ""description"": ""Replace an existing destination. Default false."" }
                    }
                }"),
                Handler = ExtractAnimationClip,
            });
        }

        // ---- Handlers --------------------------------------------------------

        private static ValueTask<ToolResult> Find(ToolContext ctx, CancellationToken ct)
        {
            var type = (string)ctx.Arguments["type"];
            var label = (string)ctx.Arguments["label"];
            var nameContains = (string)ctx.Arguments["nameContains"];
            var limit = (int?)ctx.Arguments["limit"] ?? 25;
            var cursor = (int?)ctx.Arguments["cursor"] ?? 0;

            string[] folders = null;
            if (ctx.Arguments["inFolders"] is JArray arr && arr.Count > 0)
            {
                var list = new List<string>();
                foreach (var t in arr)
                {
                    var p = (string)t;
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    list.Add(NormalizeAssetPath(p));
                }
                folders = list.ToArray();
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(type)) parts.Add($"t:{type}");
            if (!string.IsNullOrWhiteSpace(label)) parts.Add($"l:{label}");
            if (!string.IsNullOrWhiteSpace(nameContains)) parts.Add(nameContains);
            var filter = string.Join(" ", parts);

            var allGuids = folders != null
                ? AssetDatabase.FindAssets(filter, folders)
                : AssetDatabase.FindAssets(filter);

            var total = allGuids.Length;
            var items = new JArray();
            var end = Math.Min(cursor + limit, total);
            for (int i = cursor; i < end; i++)
            {
                var guid = allGuids[i];
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
                items.Add(new JObject
                {
                    ["path"] = path,
                    ["guid"] = guid,
                    ["type"] = mainType?.Name ?? "Unknown",
                });
            }

            var result = new JObject
            {
                ["items"] = items,
                ["total"] = total,
                ["returned"] = items.Count,
            };
            if (end < total) result["next_cursor"] = end;
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static ValueTask<ToolResult> CreateFolder(ToolContext ctx, CancellationToken ct)
        {
            var parent = RequireString(ctx, "parent");
            var name = RequireString(ctx, "name");
            var parentNorm = NormalizeAssetPath(parent);
            if (name.Contains('/') || name.Contains('\\'))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"name must not contain path separators, got '{name}'.");

            var full = parentNorm.TrimEnd('/') + "/" + name;
            if (AssetDatabase.IsValidFolder(full))
            {
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["path"] = full,
                    ["created"] = false,
                }));
            }
            if (!AssetDatabase.IsValidFolder(parentNorm))
                throw new McpToolException(McpErrorCodes.NotFound, $"parent folder does not exist: '{parentNorm}'.");

            var guid = AssetDatabase.CreateFolder(parentNorm, name);
            if (string.IsNullOrEmpty(guid))
                throw new McpToolException(McpErrorCodes.ToolError, $"failed to create folder '{full}'.");

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["path"] = AssetDatabase.GUIDToAssetPath(guid),
                ["guid"] = guid,
                ["created"] = true,
            }));
        }

        private static ValueTask<ToolResult> Copy(ToolContext ctx, CancellationToken ct)
        {
            var src = NormalizeAssetPath(RequireString(ctx, "src"));
            var dest = NormalizeAssetPath(RequireString(ctx, "dest"));
            var overwrite = ctx.Arguments["overwrite"]?.Value<bool>() == true;

            if (string.Equals(src, dest, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "src and dest must be different paths.");

            if (!AssetExists(src))
                throw new McpToolException(McpErrorCodes.NotFound, $"src does not exist: '{src}'.");

            EnsureParentExists(dest);

            if (!AssetExists(dest))
            {
                if (!AssetDatabase.CopyAsset(src, dest))
                    throw new McpToolException(McpErrorCodes.ToolError, $"CopyAsset failed: '{src}' -> '{dest}'.");
            }
            else
            {
                if (!overwrite)
                    throw new McpToolException(McpErrorCodes.Conflict, $"dest already exists: '{dest}'. Pass overwrite=true to replace.");
                var tempFolder = UniqueSiblingFolder(dest);
                var temp = $"{tempFolder}/{Path.GetFileName(dest)}";
                try
                {
                    var parent = Path.GetDirectoryName(tempFolder)?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(tempFolder))))
                        throw new McpToolException(McpErrorCodes.ToolError, $"Failed to create temporary folder '{tempFolder}'.");
                    if (!AssetDatabase.CopyAsset(src, temp))
                        throw new McpToolException(McpErrorCodes.ToolError, $"CopyAsset failed: '{src}' -> '{temp}'.");
                    ReplaceExistingAsset(temp, dest);
                }
                finally { DeleteIfExists(tempFolder); }
            }

            AssetDatabase.ImportAsset(dest);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["path"] = dest,
                ["guid"] = AssetDatabase.AssetPathToGUID(dest),
            }));
        }

        private static ValueTask<ToolResult> Move(ToolContext ctx, CancellationToken ct)
        {
            var src = NormalizeAssetPath(RequireString(ctx, "src"));
            var dest = NormalizeAssetPath(RequireString(ctx, "dest"));

            if (!AssetExists(src))
                throw new McpToolException(McpErrorCodes.NotFound, $"src does not exist: '{src}'.");
            EnsureParentExists(dest);

            var err = AssetDatabase.MoveAsset(src, dest);
            if (!string.IsNullOrEmpty(err))
                throw new McpToolException(McpErrorCodes.ToolError, $"MoveAsset failed: {err}");

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["path"] = dest,
                ["guid"] = AssetDatabase.AssetPathToGUID(dest),
            }));
        }

        private static ValueTask<ToolResult> Delete(ToolContext ctx, CancellationToken ct)
        {
            var arr = ctx.Arguments["paths"] as JArray
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "paths: non-empty array required.");
            var deleted = new JArray();
            var missing = new JArray();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var t in arr)
                {
                    var p = NormalizeAssetPath((string)t);
                    if (!AssetExists(p)) { missing.Add(p); continue; }
                    if (AssetDatabase.DeleteAsset(p)) deleted.Add(p);
                    else missing.Add(p);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["deleted"] = deleted,
                ["missing"] = missing,
            }));
        }

        private static ValueTask<ToolResult> Import(ToolContext ctx, CancellationToken ct)
        {
            var arr = ctx.Arguments["paths"] as JArray
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "paths: non-empty array required.");
            var force = ctx.Arguments["force"]?.Value<bool>() == true;
            var options = force ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default;

            var reimported = new JArray();
            var missing = new JArray();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var t in arr)
                {
                    var p = NormalizeAssetPath((string)t);
                    if (!AssetExists(p)) { missing.Add(p); continue; }
                    AssetDatabase.ImportAsset(p, options);
                    reimported.Add(p);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["reimported"] = reimported,
                ["missing"] = missing,
            }));
        }

        private static ValueTask<ToolResult> ListSubAssets(ToolContext ctx, CancellationToken ct)
        {
            var path = NormalizeAssetPath(RequireString(ctx, "path"));
            if (AssetDatabase.LoadMainAssetAtPath(path) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"asset not found: '{path}'.");

            var type = ((string)ctx.Arguments["type"])?.Trim();
            var nameContains = ((string)ctx.Arguments["nameContains"])?.Trim();
            var offset = (int?)ctx.Arguments["offset"] ?? 0;
            var limit = (int?)ctx.Arguments["limit"] ?? 25;
            var assets = AssetDatabase.LoadAllAssetsAtPath(path)
                .Where(asset => asset != null)
                .Where(asset => string.IsNullOrEmpty(type)
                    || string.Equals(asset.GetType().Name, type, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(asset.GetType().FullName, type, StringComparison.Ordinal))
                .Where(asset => string.IsNullOrEmpty(nameContains)
                    || (asset.name ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(AssetDatabase.IsMainAsset)
                .ThenBy(asset => asset.GetType().Name, StringComparer.Ordinal)
                .ThenBy(asset => asset.name, StringComparer.Ordinal)
                .ToArray();

            var items = new JArray();
            foreach (var asset in assets.Skip(offset).Take(limit))
            {
                ct.ThrowIfCancellationRequested();
                items.Add(DescribeAsset(asset));
            }

            var payload = new JObject
            {
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["items"] = items,
                ["total"] = assets.Length,
                ["offset"] = offset,
                ["returned"] = items.Count,
                ["truncated"] = offset + items.Count < assets.Length,
            };
            if (offset + items.Count < assets.Length)
                payload["nextOffset"] = offset + items.Count;
            return new ValueTask<ToolResult>(ToolResult.Ok(payload));
        }

        private static ValueTask<ToolResult> ExtractAnimationClip(ToolContext ctx, CancellationToken ct)
        {
            var sourcePath = NormalizeAssetPath(RequireString(ctx, "sourcePath"));
            var destinationPath = NormalizeAssetPath(RequireString(ctx, "destinationPath"));
            if (!destinationPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !destinationPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    "destinationPath must be a project-owned Assets/ path ending in .anim.");
            if (AssetDatabase.LoadMainAssetAtPath(sourcePath) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"source asset not found: '{sourcePath}'.");

            var clipName = ((string)ctx.Arguments["clipName"])?.Trim();
            var clipLocalId = ctx.Arguments["clipLocalId"]?.Type == JTokenType.Null
                ? (long?)null
                : ctx.Arguments["clipLocalId"]?.Value<long>();
            var clips = AssetDatabase.LoadAllAssetsAtPath(sourcePath)
                .OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                .ToArray();
            var matches = clipLocalId is long localId
                ? clips.Where(clip => TryGetLocalId(clip, out var candidate) && candidate == localId).ToArray()
                : clips.Where(clip => string.IsNullOrEmpty(clipName) ||
                    string.Equals(clip.name, clipName, StringComparison.Ordinal)).ToArray();

            if (matches.Length == 0)
            {
                var available = clips.Take(25).Select(DescribeAsset).ToArray();
                throw new McpToolException(McpErrorCodes.NotFound,
                    $"AnimationClip not found in '{sourcePath}'.",
                    new JObject
                    {
                        ["totalAvailable"] = clips.Length,
                        ["availableClips"] = new JArray(available),
                        ["truncated"] = available.Length < clips.Length,
                    });
            }
            if (matches.Length > 1)
            {
                var page = matches.Take(25).Select(DescribeAsset).ToArray();
                throw new McpToolException(McpErrorCodes.Conflict,
                    $"More than one AnimationClip matched in '{sourcePath}'. Pass clipName or clipLocalId.",
                    new JObject
                    {
                        ["totalMatches"] = matches.Length,
                        ["matches"] = new JArray(page),
                        ["truncated"] = page.Length < matches.Length,
                    });
            }

            var destinationExists = AssetExists(destinationPath) || File.Exists(Path.GetFullPath(destinationPath));
            var overwrite = ctx.Arguments["overwrite"]?.Value<bool>() == true;
            if (destinationExists && !overwrite)
                throw new McpToolException(McpErrorCodes.Conflict,
                    $"destination already exists: '{destinationPath}'. Pass overwrite=true to replace.");

            var workingPath = destinationExists
                ? UniqueSiblingPath(destinationPath, "animation_extract")
                : destinationPath;
            EnsureParentExists(destinationPath);
            try
            {
                ct.ThrowIfCancellationRequested();
                var copy = UnityEngine.Object.Instantiate(matches[0]);
                copy.name = matches[0].name;
                copy.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(copy, workingPath);
                AssetDatabase.SaveAssets();
                if (destinationExists)
                    ReplaceExistingAsset(workingPath, destinationPath);
                AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (!string.Equals(workingPath, destinationPath, StringComparison.Ordinal) && AssetExists(workingPath))
                    DeleteIfExists(workingPath);
            }

            var output = AssetDatabase.LoadAssetAtPath<AnimationClip>(destinationPath);
            if (output == null)
                throw new McpToolException(McpErrorCodes.ToolError,
                    $"extracted AnimationClip could not be loaded at '{destinationPath}'.");
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["sourcePath"] = sourcePath,
                ["sourceClip"] = matches[0].name,
                ["destinationPath"] = destinationPath,
                ["destinationGuid"] = AssetDatabase.AssetPathToGUID(destinationPath),
                ["destinationClip"] = output.name,
                ["overwritten"] = destinationExists,
            }));
        }

        private static JObject DescribeAsset(UnityEngine.Object asset)
        {
            TryGetLocalId(asset, out var localId);
            return new JObject
            {
                ["name"] = asset.name,
                ["type"] = asset.GetType().FullName,
                ["localId"] = localId,
                ["isMainAsset"] = AssetDatabase.IsMainAsset(asset),
            };
        }

        private static bool TryGetLocalId(UnityEngine.Object asset, out long localId)
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out localId))
                return true;
            localId = 0;
            return false;
        }

        private static void ReplaceExistingAsset(string replacement, string dest)
        {
            if (!AssetDatabase.IsValidFolder(dest))
            {
                ReplaceFileContents(replacement, dest);
                return;
            }

            var backup = UniqueSiblingPath(dest, "backup");
            var movedToBackup = false;
            try
            {
                var backupError = AssetDatabase.MoveAsset(dest, backup);
                if (!string.IsNullOrEmpty(backupError))
                    throw new McpToolException(McpErrorCodes.ToolError, $"MoveAsset failed while backing up '{dest}': {backupError}");
                movedToBackup = true;

                var replaceError = AssetDatabase.MoveAsset(replacement, dest);
                if (!string.IsNullOrEmpty(replaceError))
                {
                    var restoreError = AssetDatabase.MoveAsset(backup, dest);
                    var message = $"MoveAsset failed while replacing '{dest}': {replaceError}";
                    if (!string.IsNullOrEmpty(restoreError))
                        message += $" Restore also failed: {restoreError}";
                    throw new McpToolException(McpErrorCodes.ToolError, message);
                }

                DeleteIfExists(backup);
            }
            catch
            {
                if (movedToBackup && !AssetExists(dest) && AssetExists(backup))
                    AssetDatabase.MoveAsset(backup, dest);
                throw;
            }
        }

        private static void ReplaceFileContents(string replacement, string dest)
        {
            var backup = Path.GetTempFileName();
            var backedUp = false;
            try
            {
                File.Copy(Path.GetFullPath(dest), backup, true);
                backedUp = true;
                File.Copy(Path.GetFullPath(replacement), Path.GetFullPath(dest), true);
                AssetDatabase.ImportAsset(dest, ImportAssetOptions.ForceUpdate);
            }
            catch
            {
                if (backedUp)
                {
                    File.Copy(backup, Path.GetFullPath(dest), true);
                    AssetDatabase.ImportAsset(dest, ImportAssetOptions.ForceUpdate);
                }
                throw;
            }
            finally
            {
                File.Delete(backup);
                DeleteIfExists(replacement);
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (AssetExists(path))
                AssetDatabase.DeleteAsset(path);
        }

        private static string UniqueSiblingPath(string assetPath, string label)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(assetPath);
            var ext = Path.GetExtension(assetPath);
            for (var i = 0; i < 20; i++)
            {
                var candidateName = $"{name}.__mcp_{label}_{Guid.NewGuid():N}{ext}";
                var candidate = string.IsNullOrEmpty(dir) ? candidateName : $"{dir}/{candidateName}";
                if (!AssetExists(candidate))
                    return candidate;
            }
            throw new McpToolException(McpErrorCodes.Conflict, $"Unable to allocate temporary path beside '{assetPath}'.");
        }

        private static string UniqueSiblingFolder(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(assetPath);
            for (var i = 0; i < 20; i++)
            {
                var candidate = $"{dir}/{name}.__mcp_copy_tmp_{Guid.NewGuid():N}";
                if (!AssetExists(candidate))
                    return candidate;
            }
            throw new McpToolException(McpErrorCodes.Conflict, $"Unable to allocate temporary folder beside '{assetPath}'.");
        }

        // ---- Helpers ---------------------------------------------------------

        private static string RequireString(ToolContext ctx, string key)
        {
            var v = (string)ctx.Arguments[key];
            if (string.IsNullOrWhiteSpace(v))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"{key}: non-empty string required.");
            return v;
        }

        private static bool AssetExists(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return true;
            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath))
                   && AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
        }

        private static void EnsureParentExists(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir)) return;
            if (AssetDatabase.IsValidFolder(dir)) return;
            // Recursively create.
            var parts = dir.Split('/');
            if (parts.Length < 2) return; // nothing to create above 'Assets' or 'Packages'
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        /// <summary>
        /// Returns an AssetDatabase-style forward-slash path under Assets/ or Packages/.
        /// Rejects absolute paths and parent traversal.
        /// </summary>
        internal static string NormalizeAssetPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "path: non-empty string required.");
            var trimmed = raw.Replace('\\', '/').Trim().TrimEnd('/');
            if (Path.IsPathRooted(trimmed))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"path must be project-relative, got '{raw}'.");
            if (trimmed.Contains("..") )
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"path must not contain '..', got '{raw}'.");
            if (!trimmed.StartsWith("Assets/", StringComparison.Ordinal)
                && !trimmed.Equals("Assets", StringComparison.Ordinal)
                && !trimmed.StartsWith("Packages/", StringComparison.Ordinal)
                && !trimmed.Equals("Packages", StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"path must be under Assets/ or Packages/, got '{raw}'.");
            return trimmed;
        }
    }
}
