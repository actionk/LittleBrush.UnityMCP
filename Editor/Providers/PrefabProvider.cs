using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Host;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    /// <summary>
    /// Scene-free prefab writes plus temporary HideAndDontSave preview rendering. Replaces the
    /// <c>InstantiatePrefab + SaveAsPrefabAsset + DestroyImmediate</c> pattern that
    /// frequently triggers native Unity crashes on prefabs with missing references.
    /// </summary>
    [McpToolProvider]
    public sealed class PrefabProvider : IToolProvider
    {
        private const int PreviewLayer = 30;

        public string Namespace => "prefab";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "prefab.create",
                Description = "Create a new prefab at dest from an existing source prefab. Uses scene-free LoadPrefabContents + SaveAsPrefabAsset — does not dirty the active scene. Modes: 'copy' creates a standalone prefab, 'variant' creates a prefab variant linked to src.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""src"", ""dest""],
                    ""properties"": {
                        ""src"": { ""type"": ""string"", ""description"": ""Source prefab path."" },
                        ""dest"": { ""type"": ""string"", ""description"": ""Destination prefab path."" },
                        ""mode"": { ""type"": ""string"", ""enum"": [""copy"", ""variant""], ""description"": ""'copy' (default): standalone prefab. 'variant': linked variant."" },
                        ""overwrite"": { ""type"": ""boolean"", ""description"": ""If true and dest exists, replace it after creating a temporary prefab."" }
                    }
                }"),
                Handler = Create,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "prefab.create_from_scene_object",
                Description = "Create a prefab from one GameObject in a loaded or project scene without dirtying or modifying the source scene or loaded scene set. Dry-run is the default; writes require confirm=true. Supports an optional wrapper root, __PivotVisual-style child name, exact relative child exclusions, transform reset, and explicit overwrite.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Sync,
                RequiresMainThread = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""dest""],
                    ""additionalProperties"": false,
                    ""properties"": {
                        ""scenePath"": { ""type"": ""string"", ""description"": ""Optional project scene path; defaults to the active scene."" },
                        ""path"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Hierarchy path to the source GameObject."" },
                        ""dest"": { ""type"": ""string"", ""description"": ""Destination prefab path."" },
                        ""rootName"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Optional prefab root name; defaults to the destination filename."" },
                        ""visualChildName"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Optional wrapper child name, for example __PivotVisual. Omit to save the cloned source as the root."" },
                        ""excludePaths"": { ""type"": ""array"", ""maxItems"": 128, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Exact source-relative child paths to omit."" },
                        ""resetTransform"": { ""type"": ""boolean"", ""description"": ""Reset the saved source root to local identity. Default true."" },
                        ""dryRun"": { ""type"": ""boolean"", ""description"": ""Validate and report without writing. Default true."" },
                        ""confirm"": { ""type"": ""boolean"", ""description"": ""Must be true when dryRun=false."" },
                        ""overwrite"": { ""type"": ""boolean"", ""description"": ""Explicitly replace an existing destination while preserving its GUID."" }
                    }
                }"),
                Handler = CreateFromSceneObject,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "prefab.read",
                Description = "Read a prefab hierarchy. Defaults to a sparse outline; request targeted component properties only when needed.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                ReloadSafe = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Prefab asset path."" },
                        ""objectPath"": { ""type"": ""string"", ""description"": ""Optional hierarchy path inside the prefab."" },
                        ""maxDepth"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50 },
                        ""maxNodes"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50000, ""description"": ""Maximum returned hierarchy nodes. Default 100."" },
                        ""profile"": { ""type"": ""string"", ""enum"": [""outline"", ""full""] },
                        ""includeComponents"": { ""type"": ""boolean"" },
                        ""includeProperties"": { ""type"": ""boolean"" },
                        ""componentTypes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""propertySearch"": { ""type"": ""string"" },
                        ""propertyPaths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""propertyOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""propertyLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                        ""includeCurveValues"": { ""type"": ""boolean"", ""description"": ""Expand Gradient and AnimationCurve serialized values; omitted/false keeps compact placeholders."" }
                    }
                }"),
                Handler = Read,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "prefab.add_nested_prefab",
                Description = "Add a source prefab as a real nested Prefab instance under a parent in an existing prefab asset. Preserves prefab linkage and applies optional local transform overrides.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""prefabPath"", ""parentPath""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Existing destination prefab asset path."" },
                        ""prefabPath"": { ""type"": ""string"", ""description"": ""Source prefab asset to instantiate."" },
                        ""parentPath"": { ""type"": ""string"", ""description"": ""Hierarchy path inside the destination prefab."" },
                        ""name"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Optional instance root name override."" },
                        ""localPosition"": { ""type"": ""object"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } } },
                        ""localEulerAngles"": { ""type"": ""object"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } } },
                        ""localScale"": { ""type"": ""object"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } } }
                    }
                }"),
                Handler = AddNestedPrefab,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "prefab.write",
                Description = "Atomically apply a batch of operations to a prefab asset (add_component, remove_component, set_property, create_gameobject, delete_gameobject, rename_gameobject, set_active, set_transform, reparent). Scene-free: if any operation fails, the prefab is not saved. Paths are relative to the prefab root.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""operations""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Prefab asset path."" },
                        ""operations"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""object"" } }
                    }
                }"),
                Handler = Write,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "prefab.replace_id",
                Description = "Find exact serialized string values and authored object references matching an ID across a prefab hierarchy, replace them in scene-free prefab contents with Undo, and optionally save without reloading the active scene.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""oldId"", ""newId""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""oldId"": { ""type"": ""string"", ""minLength"": 1 },
                        ""newId"": { ""type"": ""string"", ""minLength"": 1 },
                        ""dryRun"": { ""type"": ""boolean"" },
                        ""save"": { ""type"": ""boolean"" },
                        ""maxResults"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 10000 }
                    }
                }"),
                Handler = ReplaceId,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "prefab.remove_unused_overrides",
                Description = "Inspect nested Prefab instances in a prefab asset and remove only Unity-reported unused overrides with PrefabUtility.RemoveUnusedOverrides. Dry-run is the safe default; pass dryRun=false to opt into an apply and save.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Prefab asset path."" },
                        ""dryRun"": { ""type"": ""boolean"", ""description"": ""Inspect and clean in memory without saving. Defaults to true."" },
                        ""save"": { ""type"": ""boolean"", ""description"": ""Persist a non-dry-run cleanup. Defaults to true when dryRun=false."" },
                        ""maxInstances"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 10000, ""description"": ""Maximum nested Prefab instances to inspect. Default 1000."" },
                        ""reportOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""reportLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 256, ""description"": ""Returned instance-report page size. Default 25."" }
                    }
                }"),
                Handler = RemoveUnusedOverrides,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "prefab.preview_screenshot",
                RequiresGraphics = true,
                Description = "Render a UI or 3D prefab offscreen in an isolated preview scene during Edit or Play Mode. UI prefabs report resolved layout ownership, clipping, overlap, and text-fit warnings; 3D prefabs copy gameplay camera settings, auto-frame renderer bounds, and accept an optional Euler rotation override.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                RequiresMainThread = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""description"": ""Prefab asset path."" },
                        ""width"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""height"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""viewportWidth"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""viewportHeight"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 4096 },
                        ""referenceWidth"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
                        ""referenceHeight"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
                        ""matchWidthOrHeight"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 },
                        ""uiScale"": { ""type"": ""number"", ""minimum"": 0.1, ""maximum"": 4 },
                        ""stateOverrides"": {
                            ""type"": ""array"",
                            ""items"": {
                                ""type"": ""object"",
                                ""required"": [""path""],
                                ""properties"": {
                                    ""path"": { ""type"": ""string"" },
                                    ""active"": { ""type"": ""boolean"" },
                                    ""text"": { ""type"": ""string"" }
                                }
                            }
                        },
                        ""padding"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 512 },
                        ""background"": { ""type"": ""string"", ""description"": ""Camera clear color, for example #00000000 or #1b1b1bff."" },
                        ""cameraPath"": { ""type"": ""string"", ""description"": ""3D only: hierarchy path of the gameplay camera to copy; defaults to Camera.main."" },
                        ""standaloneCamera"": { ""type"": ""boolean"", ""description"": ""3D only: render with a neutral perspective camera without requiring a loaded scene camera. Use for asset-only batch workers; does not reproduce gameplay camera effects."" },
                        ""rotation"": {
                            ""type"": ""object"",
                            ""description"": ""3D only: optional Euler rotation in degrees applied to the temporary preview camera before framing."",
                            ""properties"": {
                                ""x"": { ""type"": ""number"" },
                                ""y"": { ""type"": ""number"" },
                                ""z"": { ""type"": ""number"" }
                            }
                        },
                        ""framePadding"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1, ""description"": ""3D only: fractional bounds padding; defaults to 0.1."" }
                    }
                }"),
                Handler = PreviewScreenshot,
            });
        }

        private static ValueTask<ToolResult> Create(ToolContext ctx, CancellationToken ct)
        {
            var src = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["src"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "src required."));
            var dest = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["dest"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "dest required."));
            var mode = (string)ctx.Arguments["mode"] ?? "copy";
            var overwrite = ctx.Arguments["overwrite"]?.Value<bool>() == true;

            if (string.Equals(src, dest, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "src and dest must be different paths.");

            if (mode != "copy" && mode != "variant")
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"mode must be 'copy' or 'variant', got '{mode}'.");

            if (!dest.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"dest must end with .prefab, got '{dest}'.");

            if (AssetDatabase.LoadMainAssetAtPath(src) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"src prefab not found: '{src}'.");

            var srcType = PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadMainAssetAtPath(src));
            if (srcType == PrefabAssetType.NotAPrefab || srcType == PrefabAssetType.MissingAsset)
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"src is not a prefab asset: '{src}' ({srcType}).");

            var destExists = AssetDatabase.LoadMainAssetAtPath(dest) != null;
            if (destExists && !overwrite)
            {
                throw new McpToolException(McpErrorCodes.Conflict, $"dest already exists: '{dest}'. Pass overwrite=true to replace.");
            }

            // Ensure parent folder exists before writing.
            EnsureParentFolder(dest);

            var writePath = destExists ? UniqueSiblingPath(dest, "prefab_tmp") : dest;
            try
            {
                CreatePrefabAtPath(src, writePath, mode);
                if (destExists)
                    ReplaceExistingAsset(writePath, dest);
            }
            catch
            {
                DeleteIfExists(writePath);
                throw;
            }

            AssetDatabase.ImportAsset(dest);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["path"] = dest,
                ["guid"] = AssetDatabase.AssetPathToGUID(dest),
                ["mode"] = mode,
            }));
        }

        private static void CreatePrefabAtPath(string src, string dest, string mode)
        {
            if (mode == "copy")
            {
                var contents = PrefabUtility.LoadPrefabContents(src);
                try
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, dest, out var success);
                    if (!success)
                        throw new McpToolException(McpErrorCodes.ToolError, $"SaveAsPrefabAsset failed: '{dest}'.");
                }
                finally { PrefabUtility.UnloadPrefabContents(contents); }
                return;
            }

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(src);
            var previewScene = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(source, previewScene);
                if (instance == null)
                    throw new McpToolException(McpErrorCodes.ToolError, $"InstantiatePrefab returned null for '{src}'.");
                PrefabUtility.SaveAsPrefabAssetAndConnect(instance, dest, InteractionMode.AutomatedAction, out var success);
                if (!success)
                    throw new McpToolException(McpErrorCodes.ToolError, $"SaveAsPrefabAssetAndConnect failed: '{dest}'.");
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        private static ValueTask<ToolResult> CreateFromSceneObject(ToolContext ctx, CancellationToken ct)
        {
            var sourcePath = ((string)ctx.Arguments["path"])?.Trim();
            if (string.IsNullOrEmpty(sourcePath))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "path required.");

            var dest = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["dest"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "dest required."));
            if (!dest.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"dest must end with .prefab, got '{dest}'.");

            using var sceneScope = SceneAssetScope.Open((string)ctx.Arguments["scenePath"]);
            var source = FindSceneGameObject(sceneScope.Scene, sourcePath);
            var rootName = ((string)ctx.Arguments["rootName"])?.Trim();
            if (string.IsNullOrEmpty(rootName))
                rootName = Path.GetFileNameWithoutExtension(dest);
            ValidateGameObjectName(rootName, "rootName");

            var visualChildName = ((string)ctx.Arguments["visualChildName"])?.Trim();
            if (!string.IsNullOrEmpty(visualChildName))
                ValidateGameObjectName(visualChildName, "visualChildName");

            var excludePaths = ReadRelativePaths(ctx.Arguments["excludePaths"] as JArray);
            foreach (var excludePath in excludePaths)
            {
                if (FindRelativeTransform(source.transform, excludePath) == null)
                    throw new McpToolException(McpErrorCodes.NotFound,
                        $"excludePaths entry not found under '{sourcePath}': '{excludePath}'.");
            }

            var resetTransform = ctx.Arguments["resetTransform"]?.Value<bool>() != false;
            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() != false;
            var overwrite = ctx.Arguments["overwrite"]?.Value<bool>() == true;
            var destExists = AssetDatabase.LoadMainAssetAtPath(dest) != null;
            if (destExists && !overwrite)
                throw new McpToolException(McpErrorCodes.Conflict,
                    $"dest already exists: '{dest}'. Pass overwrite=true to replace.");
            if (!dryRun && ctx.Arguments["confirm"]?.Value<bool>() != true)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    "confirm=true is required when dryRun=false.");

            var result = sceneScope.AddMetadata(new JObject
            {
                ["sourcePath"] = sourcePath,
                ["dest"] = dest,
                ["operation"] = destExists ? "overwrite" : "create",
                ["rootName"] = rootName,
                ["visualChildName"] = visualChildName,
                ["excludePaths"] = new JArray(excludePaths),
                ["resetTransform"] = resetTransform,
                ["dryRun"] = dryRun,
            });
            if (dryRun)
                return new ValueTask<ToolResult>(ToolResult.Ok(result));

            EnsureParentFolder(dest);
            var writePath = destExists ? UniqueSiblingPath(dest, "scene_object") : dest;
            try
            {
                SaveSceneObjectAsPrefab(source, writePath, rootName, visualChildName, excludePaths, resetTransform);
                if (destExists)
                    ReplaceExistingAsset(writePath, dest);
            }
            catch
            {
                if (writePath != dest)
                    DeleteIfExists(writePath);
                throw;
            }

            AssetDatabase.ImportAsset(dest, ImportAssetOptions.ForceUpdate);
            result["dryRun"] = false;
            result["path"] = dest;
            result["guid"] = AssetDatabase.AssetPathToGUID(dest);
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static GameObject FindSceneGameObject(Scene scene, string hierarchyPath)
        {
            var parts = hierarchyPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                throw new McpToolException(McpErrorCodes.ValidationFailed, "path required.");

            var current = scene.GetRootGameObjects().FirstOrDefault(go => go.name == parts[0]);
            for (var i = 1; current != null && i < parts.Length; i++)
                current = current.transform.Cast<Transform>().FirstOrDefault(child => child.name == parts[i])?.gameObject;

            return current ?? throw new McpToolException(McpErrorCodes.NotFound,
                $"Scene GameObject not found: '{hierarchyPath}' in '{scene.path}'.");
        }

        private static List<string> ReadRelativePaths(JArray values)
        {
            var result = new List<string>();
            if (values == null)
                return result;

            foreach (var value in values)
            {
                var path = ((string)value)?.Trim().Trim('/');
                if (string.IsNullOrEmpty(path))
                    throw new McpToolException(McpErrorCodes.ValidationFailed,
                        "excludePaths entries must be non-empty relative child paths.");
                if (!result.Contains(path, StringComparer.Ordinal))
                    result.Add(path);
            }
            return result;
        }

        private static Transform FindRelativeTransform(Transform root, string relativePath)
        {
            var current = root;
            foreach (var part in relativePath.Split('/'))
            {
                current = current.Cast<Transform>().FirstOrDefault(child => child.name == part);
                if (current == null)
                    return null;
            }
            return current;
        }

        private static void ValidateGameObjectName(string value, string argumentName)
        {
            if (value.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"{argumentName} must not contain path separators, got '{value}'.");
        }

        private static void SaveSceneObjectAsPrefab(
            GameObject source,
            string dest,
            string rootName,
            string visualChildName,
            IReadOnlyList<string> excludePaths,
            bool resetTransform)
        {
            var previousActiveScene = SceneManager.GetActiveScene();
            var stagingScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            stagingScene.name = $"__McpPrefabExport_{Guid.NewGuid():N}";
            GameObject savedRoot = null;
            IReadOnlyCollection<Mesh> transientMeshes = null;
            try
            {
                SceneManager.SetActiveScene(stagingScene);
                if (!string.IsNullOrEmpty(visualChildName))
                {
                    savedRoot = new GameObject(rootName);
                    var visual = UnityEngine.Object.Instantiate(source, savedRoot.transform, false);
                    visual.name = visualChildName;
                    visual.SetActive(true);
                    RemoveExcludedChildren(visual.transform, excludePaths);
                    transientMeshes = ProtectTransientMeshes(visual);
                    if (resetTransform)
                        ResetLocalTransform(visual.transform);
                }
                else
                {
                    var container = new GameObject("__McpSceneObjectContainer");
                    var clone = UnityEngine.Object.Instantiate(source, container.transform, false);
                    RemoveExcludedChildren(clone.transform, excludePaths);
                    transientMeshes = ProtectTransientMeshes(clone);
                    clone.transform.SetParent(null, false);
                    UnityEngine.Object.DestroyImmediate(container);
                    savedRoot = clone;
                    savedRoot.name = rootName;
                    if (resetTransform)
                        ResetLocalTransform(savedRoot.transform);
                }

                savedRoot.SetActive(true);
                PrefabUtility.SaveAsPrefabAsset(savedRoot, dest, out var success);
                if (!success)
                    throw new McpToolException(McpErrorCodes.ToolError,
                        $"SaveAsPrefabAsset failed: '{dest}'.");
                if (transientMeshes != null && transientMeshes.Count > 0)
                {
                    foreach (var mesh in transientMeshes)
                        AssetDatabase.AddObjectToAsset(mesh, dest);
                    PrefabUtility.SaveAsPrefabAsset(savedRoot, dest, out success);
                    if (!success)
                        throw new McpToolException(McpErrorCodes.ToolError,
                            $"Saving embedded meshes failed: '{dest}'.");
                }
            }
            finally
            {
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                    SceneManager.SetActiveScene(previousActiveScene);
                EditorSceneManager.CloseScene(stagingScene, true);
                if (transientMeshes != null)
                    foreach (var mesh in transientMeshes)
                        if (mesh != null && !AssetDatabase.Contains(mesh))
                            UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static IReadOnlyCollection<Mesh> ProtectTransientMeshes(GameObject clone)
        {
            var replacements = new Dictionary<Mesh, Mesh>();
            foreach (var filter in clone.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || AssetDatabase.Contains(mesh))
                    continue;
                if (!replacements.TryGetValue(mesh, out var replacement))
                {
                    replacement = UnityEngine.Object.Instantiate(mesh);
                    replacement.name = mesh.name;
                    replacements.Add(mesh, replacement);
                }
                filter.sharedMesh = replacement;
            }

            foreach (var collider in clone.GetComponentsInChildren<MeshCollider>(true))
            {
                var mesh = collider.sharedMesh;
                if (mesh == null || AssetDatabase.Contains(mesh))
                    continue;
                if (!replacements.TryGetValue(mesh, out var replacement))
                {
                    replacement = UnityEngine.Object.Instantiate(mesh);
                    replacement.name = mesh.name;
                    replacements.Add(mesh, replacement);
                }
                collider.sharedMesh = replacement;
            }

            if (replacements.Count == 0)
                return replacements.Values;

            foreach (var component in clone.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                var serialized = new SerializedObject(component);
                var meshProperty = serialized.FindProperty("m_Mesh");
                if (meshProperty?.propertyType != SerializedPropertyType.ObjectReference ||
                    meshProperty.objectReferenceValue is not Mesh mesh ||
                    !replacements.TryGetValue(mesh, out var replacement))
                {
                    continue;
                }
                meshProperty.objectReferenceValue = replacement;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return replacements.Values;
        }

        private static void RemoveExcludedChildren(Transform cloneRoot, IReadOnlyList<string> excludePaths)
        {
            foreach (var excludePath in excludePaths.OrderByDescending(path => path.Count(ch => ch == '/')))
            {
                var target = FindRelativeTransform(cloneRoot, excludePath);
                if (target != null)
                    UnityEngine.Object.DestroyImmediate(target.gameObject);
            }
        }

        private static void ResetLocalTransform(Transform transform)
        {
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        private static ValueTask<ToolResult> AddNestedPrefab(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            var prefabPath = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["prefabPath"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "prefabPath required."));
            var parentPath = (string)ctx.Arguments["parentPath"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "parentPath required.");

            if (string.Equals(path, prefabPath, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "A prefab cannot be nested inside itself.");

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (source == null || PrefabUtility.GetPrefabAssetType(source) is PrefabAssetType.NotAPrefab or PrefabAssetType.MissingAsset)
                throw new McpToolException(McpErrorCodes.NotFound, $"Source prefab not found: '{prefabPath}'.");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Destination prefab not found: '{path}'.");

            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var parent = FindPrefabTransform(contents, parentPath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, contents.scene);
                if (instance == null)
                    throw new McpToolException(McpErrorCodes.ToolError, $"InstantiatePrefab returned null for '{prefabPath}'.");

                instance.transform.SetParent(parent, false);
                if ((string)ctx.Arguments["name"] is { } name && !string.IsNullOrWhiteSpace(name))
                    instance.name = name;
                ApplyLocalTransform(instance.transform, ctx.Arguments);

                var instancePath = SceneSerializer.GetHierarchyPath(instance);
                PrefabUtility.SaveAsPrefabAsset(contents, path, out var success);
                if (!success)
                    throw new McpToolException(McpErrorCodes.ToolError, $"SaveAsPrefabAsset failed: '{path}'.");
                AssetDatabase.ImportAsset(path);

                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["assetPath"] = path,
                    ["instancePath"] = instancePath,
                    ["sourcePrefabPath"] = prefabPath,
                    ["isPrefabInstance"] = true,
                }));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static Transform FindPrefabTransform(GameObject root, string path)
        {
            var normalized = path.Trim('/');
            if (normalized == root.name)
                return root.transform;
            if (normalized.StartsWith(root.name + "/", StringComparison.Ordinal))
                normalized = normalized[(root.name.Length + 1)..];
            return root.transform.Find(normalized)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"GameObject not found: {path}");
        }

        private static void ApplyLocalTransform(Transform transform, JObject args)
        {
            if (args["localPosition"] is JObject position) transform.localPosition = ReadVector3(position);
            if (args["localEulerAngles"] is JObject euler) transform.localEulerAngles = ReadVector3(euler);
            if (args["localScale"] is JObject scale) transform.localScale = ReadVector3(scale);
        }

        private static Vector3 ReadVector3(JObject value)
            => new((float)(value["x"] ?? 0), (float)(value["y"] ?? 0), (float)(value["z"] ?? 0));

        // ---- Read / Write -----------------------------------------------------

        private static ValueTask<ToolResult> Read(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            if (AssetDatabase.LoadMainAssetAtPath(path) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"prefab not found: '{path}'.");

            var options = new SceneSerializer.Options
            {
                MaxDepth = (int?)ctx.Arguments["maxDepth"] ?? 10,
                MaxNodes = (int?)ctx.Arguments["maxNodes"] ?? 100,
                IncludeComponents = ctx.Arguments["includeComponents"]?.Value<bool>() ?? false,
                IncludeProperties = ctx.Arguments["includeProperties"]?.Value<bool>() == true,
                IncludeCurveValues = ctx.Arguments["includeCurveValues"]?.Value<bool>() == true,
                Profile = ctx.Arguments["profile"]?.Value<string>() ?? "outline",
                ComponentTypes = ctx.Arguments["componentTypes"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                PropertySearch = ctx.Arguments["propertySearch"]?.Value<string>(),
                PropertyPaths = ctx.Arguments["propertyPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                PropertyOffset = ctx.Arguments["propertyOffset"]?.Value<int>() ?? 0,
                PropertyLimit = ctx.Arguments["propertyLimit"]?.Value<int>() ?? 25,
            };

            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var target = ctx.Arguments["objectPath"]?.Value<string>() is { Length: > 0 } objectPath
                    ? FindPrefabTransform(contents, objectPath).gameObject
                    : contents;
                var node = SceneSerializer.SerializeRoot(target, options);
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["assetPath"] = path,
                    ["root"] = node,
                }));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static ValueTask<ToolResult> Write(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            var ops = ctx.Arguments["operations"] as JArray
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "operations: non-empty array required.");

            // Reject deleting the prefab root — SaveAsPrefabAsset on a destroyed contents
            // root either crashes natively or writes an empty prefab. The agent must use
            // asset.delete on the prefab path itself.
            foreach (var op in ops)
            {
                if ((string)op?["type"] != "delete_gameobject") continue;
                var p = (string)op["path"];
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (!p.Contains('/'))
                    throw new McpToolException(McpErrorCodes.InvalidParams,
                        $"delete_gameobject on prefab root '{p}' is not allowed; use asset.delete to remove the prefab itself.");
            }

            if (AssetDatabase.LoadMainAssetAtPath(path) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"prefab not found: '{path}'.");

            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var opsResult = SceneWriteOperations.ApplyToRoot(contents, ops);
                var failed = (int?)opsResult["failed"] ?? 0;
                var saved = false;

                if (failed == 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, path, out var success);
                    if (!success)
                        throw new McpToolException(McpErrorCodes.ToolError, $"SaveAsPrefabAsset failed: '{path}'.");
                    AssetDatabase.ImportAsset(path);
                    saved = true;
                }

                var envelope = new JObject
                {
                    ["assetPath"] = path,
                    ["saved"] = saved,
                    ["succeeded"] = opsResult["succeeded"],
                    ["failed"] = opsResult["failed"],
                    ["results"] = opsResult["results"],
                };
                if (!saved)
                    envelope["abortReason"] = "atomic_failure: at least one operation failed";
                return new ValueTask<ToolResult>(ToolResult.Ok(envelope));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static ValueTask<ToolResult> ReplaceId(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"path must end with .prefab, got '{path}'.");
            if (AssetDatabase.LoadMainAssetAtPath(path) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"prefab not found: '{path}'.");

            var contents = PrefabUtility.LoadPrefabContents(path);
            if (contents == null)
                throw new McpToolException(McpErrorCodes.ToolError, $"Could not load prefab contents: '{path}'.");

            try
            {
                var result = SceneSerializedReplacement.ReplacePrefab(
                    contents,
                    path,
                    (string)ctx.Arguments["oldId"],
                    (string)ctx.Arguments["newId"],
                    ctx.Arguments["dryRun"]?.Value<bool>() == true,
                    ctx.Arguments["save"]?.Value<bool>() == true,
                    ctx.Arguments["maxResults"]?.Value<int>() ?? 100);
                return new ValueTask<ToolResult>(ToolResult.Ok(result));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static ValueTask<ToolResult> RemoveUnusedOverrides(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed, $"path must end with .prefab, got '{path}'.");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"prefab not found: '{path}'.");
            var assetType = PrefabUtility.GetPrefabAssetType(asset);
            if (assetType is PrefabAssetType.NotAPrefab or PrefabAssetType.MissingAsset)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    $"path is not a valid prefab asset: '{path}' ({assetType}).");

            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() ?? true;
            var save = ctx.Arguments["save"]?.Value<bool>() ?? !dryRun;
            var maxInstances = ctx.Arguments["maxInstances"]?.Value<int>() ?? 1000;
            var reportOffset = ctx.Arguments["reportOffset"]?.Value<int>() ?? 0;
            var reportLimit = ctx.Arguments["reportLimit"]?.Value<int>() ?? 25;
            if (maxInstances < 1)
                throw new McpToolException(McpErrorCodes.ValidationFailed, "maxInstances must be at least 1.");
            if (reportOffset < 0 || reportLimit is < 1 or > 256)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "reportOffset must be >= 0 and reportLimit must be between 1 and 256.");

            ct.ThrowIfCancellationRequested();
            var contents = PrefabUtility.LoadPrefabContents(path);
            if (contents == null)
                throw new McpToolException(McpErrorCodes.ToolError, $"Could not load prefab contents: '{path}'.");

            var saved = false;
            var before = new List<PrefabOverrideSnapshot>();
            var after = new List<PrefabOverrideSnapshot>();
            try
            {
                var instances = FindNestedPrefabInstanceRoots(contents, maxInstances);
                before = CapturePropertyOverrides(instances);

                if (instances.Count > 0)
                {
                    // UserAction preserves the normal Unity Undo contract for an apply. A dry
                    // run uses AutomatedAction and is discarded with the isolated prefab scene.
                    PrefabUtility.RemoveUnusedOverrides(
                        instances.ToArray(),
                        dryRun ? InteractionMode.AutomatedAction : InteractionMode.UserAction);
                }

                after = CapturePropertyOverrides(instances);
                var beforeCount = before.Sum(snapshot => snapshot.Count);
                var afterCount = after.Sum(snapshot => snapshot.Count);
                var removedCount = Math.Max(0, beforeCount - afterCount);
                var reports = BuildOverrideReports(before, after, reportOffset, reportLimit);

                if (!dryRun && save && removedCount > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, path, out var success);
                    if (!success)
                        throw new McpToolException(McpErrorCodes.ToolError, $"SaveAsPrefabAsset failed: '{path}'.");
                    saved = true;
                }

                var result = new JObject
                {
                    ["assetPath"] = path,
                    ["dryRun"] = dryRun,
                    ["saveRequested"] = save,
                    ["saved"] = false,
                    ["instanceCount"] = instances.Count,
                    ["propertyOverrideCountBefore"] = beforeCount,
                    ["propertyOverrideCountAfter"] = afterCount,
                    ["unusedPropertyOverrideCountBefore"] = removedCount,
                    ["unusedPropertyOverrideCountAfter"] = 0,
                    ["removedUnusedPropertyOverrideCount"] = removedCount,
                    ["reportOffset"] = reportOffset,
                    ["reportReturned"] = reports.Count,
                    ["reportTruncated"] = (long)reportOffset + reports.Count < before.Count,
                    ["instances"] = reports,
                };
                if ((long)reportOffset + reports.Count < before.Count)
                    result["nextReportOffset"] = reportOffset + reports.Count;

                if (!saved)
                    return new ValueTask<ToolResult>(ToolResult.Ok(result));

                // Saving a prefab can normalize nested instance data. Import and read back the
                // persisted asset so callers can distinguish an in-memory cleanup from a durable one.
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var readback = ReadPropertyOverrides(path, maxInstances);
                result["saved"] = true;
                result["readback"] = new JObject
                {
                    ["instanceCount"] = readback.InstanceCount,
                    ["propertyOverrideCount"] = readback.PropertyOverrideCount,
                    ["verified"] = readback.PropertyOverrideCount == afterCount,
                };
                return new ValueTask<ToolResult>(ToolResult.Ok(result));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private sealed class PrefabOverrideSnapshot
        {
            public GameObject Instance;
            public int Count;
        }

        private sealed class PrefabOverrideReadback
        {
            public int InstanceCount;
            public int PropertyOverrideCount;
        }

        private static List<GameObject> FindNestedPrefabInstanceRoots(GameObject root, int maxInstances)
        {
            var instances = new List<GameObject>();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var gameObject = transform.gameObject;
                // A loaded Prefab Variant stores its overrides on the loaded root itself;
                // regular prefab assets have no root instance and are intentionally skipped.
                var isVariantRoot = gameObject == root && PrefabUtility.IsPartOfVariantPrefab(gameObject);
                if ((!isVariantRoot && !PrefabUtility.IsAnyPrefabInstanceRoot(gameObject)) ||
                    (gameObject != root && !PrefabUtility.IsPartOfPrefabInstance(gameObject)))
                    continue;

                instances.Add(gameObject);
                if (instances.Count > maxInstances)
                    throw new McpToolException(
                        McpErrorCodes.InvalidParams,
                        $"Prefab contains more than maxInstances ({maxInstances}) nested Prefab instances.");
            }
            return instances;
        }

        private static List<PrefabOverrideSnapshot> CapturePropertyOverrides(List<GameObject> instances)
        {
            var snapshots = new List<PrefabOverrideSnapshot>(instances.Count);
            foreach (var instance in instances)
            {
                var modifications = PrefabUtility.GetPropertyModifications(instance);
                snapshots.Add(new PrefabOverrideSnapshot
                {
                    Instance = instance,
                    Count = modifications?.Length ?? 0,
                });
            }
            return snapshots;
        }

        private static JArray BuildOverrideReports(
            List<PrefabOverrideSnapshot> before,
            List<PrefabOverrideSnapshot> after,
            int offset,
            int limit)
        {
            var reports = new JArray();
            var end = (int)Math.Min(before.Count, (long)offset + limit);
            for (var i = offset; i < end; i++)
            {
                var beforeSnapshot = before[i];
                var afterCount = i < after.Count ? after[i].Count : 0;
                reports.Add(new JObject
                {
                    ["path"] = SceneSerializer.GetHierarchyPath(beforeSnapshot.Instance),
                    ["propertyOverrideCountBefore"] = beforeSnapshot.Count,
                    ["propertyOverrideCountAfter"] = afterCount,
                    ["removedUnusedPropertyOverrideCount"] = Math.Max(0, beforeSnapshot.Count - afterCount),
                });
            }
            return reports;
        }

        private static PrefabOverrideReadback ReadPropertyOverrides(string path, int maxInstances)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            if (contents == null)
                throw new McpToolException(McpErrorCodes.ToolError, $"Could not read back prefab contents: '{path}'.");

            try
            {
                var instances = FindNestedPrefabInstanceRoots(contents, maxInstances);
                return new PrefabOverrideReadback
                {
                    InstanceCount = instances.Count,
                    PropertyOverrideCount = CapturePropertyOverrides(instances).Sum(snapshot => snapshot.Count),
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static ValueTask<ToolResult> PreviewScreenshot(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "path required."));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"prefab not found: '{path}'.");

            var sourceRect = prefab.GetComponent<RectTransform>();
            if (sourceRect == null)
                return Preview3dScreenshot(path, ctx);

            var padding = ctx.Arguments["padding"]?.Value<int>() ?? 24;
            var prefabSize = ResolveRectSize(sourceRect);
            var hasViewportWidth = ctx.Arguments["viewportWidth"] != null;
            var hasViewportHeight = ctx.Arguments["viewportHeight"] != null;
            if (hasViewportWidth != hasViewportHeight)
            {
                throw new McpToolException(
                    McpErrorCodes.ValidationFailed,
                    "viewportWidth and viewportHeight must be provided together.");
            }

            var responsive = hasViewportWidth;
            var viewportWidth = responsive ? ctx.Arguments["viewportWidth"].Value<int>() : 0;
            var viewportHeight = responsive ? ctx.Arguments["viewportHeight"].Value<int>() : 0;
            var width = ctx.Arguments["width"]?.Value<int>() ??
                        Mathf.CeilToInt((responsive ? viewportWidth : prefabSize.x) + padding * 2);
            var height = ctx.Arguments["height"]?.Value<int>() ??
                         Mathf.CeilToInt((responsive ? viewportHeight : prefabSize.y) + padding * 2);
            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);
            var background = ParseColor((string)ctx.Arguments["background"], Color.clear);
            var referenceWidth = ctx.Arguments["referenceWidth"]?.Value<float>() ??
                                 (responsive ? viewportWidth : prefabSize.x);
            var referenceHeight = ctx.Arguments["referenceHeight"]?.Value<float>() ??
                                  (responsive ? viewportHeight : prefabSize.y);
            var matchWidthOrHeight = Mathf.Clamp01(ctx.Arguments["matchWidthOrHeight"]?.Value<float>() ?? 0.5f);
            var uiScale = Mathf.Clamp(ctx.Arguments["uiScale"]?.Value<float>() ?? 1f, 0.1f, 4f);
            var canvasScale = responsive
                ? ResolveScaleWithScreenSize(
                    viewportWidth,
                    viewportHeight,
                    referenceWidth / uiScale,
                    referenceHeight / uiScale,
                    matchWidthOrHeight)
                : 1f;
            var logicalViewport = responsive
                ? new Vector2(viewportWidth / canvasScale, viewportHeight / canvasScale)
                : new Vector2(width, height);

            GameObject canvasGo = null;
            GameObject instance = null;
            Texture2D tex = null;
            PreviewRenderUtility preview = null;
            try
            {
                preview = new PreviewRenderUtility();
                var previewScene = preview.camera.gameObject.scene;
                PrefabUtility.LoadPrefabContentsIntoPreviewScene(path, previewScene, out instance);
                var previewOrigin = new Vector3(100000f, 100000f, 0f);

                var camera = preview.camera;
                camera.cameraType = CameraType.Game;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = background;
                camera.cullingMask = 1 << PreviewLayer;
                camera.orthographic = true;
                var logicalPadding = padding / canvasScale;
                var logicalCaptureWidth = responsive ? logicalViewport.x + logicalPadding * 2f : width;
                var logicalCaptureHeight = responsive ? logicalViewport.y + logicalPadding * 2f : height;
                camera.aspect = (float)width / height;
                camera.orthographicSize = Mathf.Max(
                    logicalCaptureHeight * 0.5f,
                    logicalCaptureWidth / (2f * camera.aspect));
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 200f;
                camera.transform.position = previewOrigin + new Vector3(0f, 0f, -100f);

                canvasGo = EditorUtility.CreateGameObjectWithHideFlags(
                    "MCP Prefab Preview Canvas",
                    HideFlags.HideAndDontSave,
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(CanvasScaler));
                SceneManager.MoveGameObjectToScene(canvasGo, previewScene);
                SetLayerRecursive(canvasGo, PreviewLayer);
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = camera;
                canvas.pixelPerfect = false;

                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;
                scaler.referencePixelsPerUnit = 100f;

                var canvasRect = canvasGo.GetComponent<RectTransform>();
                canvasRect.sizeDelta = logicalViewport;
                canvasRect.position = previewOrigin;
                canvasRect.localScale = Vector3.one;

                instance.hideFlags = HideFlags.HideAndDontSave;
                SetLayerRecursive(instance, PreviewLayer);

                var rect = instance.GetComponent<RectTransform>();
                rect.SetParent(canvasGo.transform, false);
                if (!responsive)
                {
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.anchoredPosition = Vector2.zero;
                    rect.sizeDelta = prefabSize;
                }

                var appliedOverrides = ApplyStateOverrides(instance.transform, ctx.Arguments["stateOverrides"] as JArray);
                canvas.worldCamera = camera;
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                ForceTmpMeshUpdates(instance);
                Canvas.ForceUpdateCanvases();
                var rootSize = rect.rect.size;
                var layoutReport = BuildLayoutReport(rect, canvasRect);
                preview.BeginStaticPreview(new Rect(0, 0, width, height));
                preview.Render(true, false);
                tex = preview.EndStaticPreview();

                var png = tex.EncodeToPNG();
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["path"] = path,
                    ["width"] = width,
                    ["height"] = height,
                    ["padding"] = padding,
                    ["responsive"] = responsive,
                    ["viewportWidth"] = responsive ? viewportWidth : width,
                    ["viewportHeight"] = responsive ? viewportHeight : height,
                    ["referenceWidth"] = referenceWidth,
                    ["referenceHeight"] = referenceHeight,
                    ["matchWidthOrHeight"] = matchWidthOrHeight,
                    ["uiScale"] = uiScale,
                    ["canvasScaleFactor"] = canvasScale,
                    ["logicalViewportSize"] = new JArray(logicalViewport.x, logicalViewport.y),
                    ["resolvedRootSize"] = new JArray(rootSize.x, rootSize.y),
                    ["stateOverridesApplied"] = appliedOverrides,
                    ["layoutReport"] = layoutReport,
                    ["sizeBytes"] = png.Length,
                }, new ImageContent { Data = png, MimeType = "image/png" }));
            }
            finally
            {
                if (tex != null)
                    UnityEngine.Object.DestroyImmediate(tex);
                preview?.Cleanup();
            }
        }

        private static ValueTask<ToolResult> Preview3dScreenshot(string path, ToolContext ctx)
        {
            var standalone = ctx.Arguments.Value<bool?>("standaloneCamera") == true;
            if (standalone && !string.IsNullOrWhiteSpace((string)ctx.Arguments["cameraPath"]))
                throw new McpToolException(McpErrorCodes.InvalidParams, "standaloneCamera and cameraPath are mutually exclusive.");
            var sourceCamera = standalone ? null : ResolveGameplayCamera((string)ctx.Arguments["cameraPath"]);
            var width = Mathf.Clamp(ctx.Arguments["width"]?.Value<int>() ?? (standalone ? 512 : GetDefaultCameraWidth(sourceCamera)), 64, 4096);
            var height = Mathf.Clamp(ctx.Arguments["height"]?.Value<int>() ?? (standalone ? 512 : GetDefaultCameraHeight(sourceCamera)), 64, 4096);
            var framePadding = Mathf.Clamp(ctx.Arguments["framePadding"]?.Value<float>() ?? 0.1f, 0f, 1f);
            var rotation = ReadEulerRotation(ctx.Arguments["rotation"]);
            var background = (string)ctx.Arguments["background"];

            Scene previewScene = default;
            GameObject previewObject = null;
            RenderTexture renderTexture = null;
            Texture2D texture = null;
            GameObject instance = null;
            try
            {
                previewScene = EditorSceneManager.NewPreviewScene();
                PrefabUtility.LoadPrefabContentsIntoPreviewScene(path, previewScene, out instance);
                if (instance == null)
                    throw new McpToolException(McpErrorCodes.ToolError, $"Failed to load prefab into a preview scene: '{path}'.");

                instance.hideFlags = HideFlags.HideAndDontSave;
                SetLayerRecursive(instance, PreviewLayer);
                var bounds = GetRendererBounds(instance, path);

                previewObject = new GameObject("MCP Prefab 3D Preview Camera")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                SceneManager.MoveGameObjectToScene(previewObject, previewScene);
                var camera = previewObject.AddComponent<Camera>();
                if (sourceCamera != null) CopyGameplayCamera(sourceCamera, camera);
                else
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);
                    camera.transform.rotation = Quaternion.Euler(25f, 35f, 0f);
                    var light = previewObject.AddComponent<Light>();
                    light.type = LightType.Directional;
                    light.intensity = 1f;
                    light.cullingMask = 1 << PreviewLayer;
                }
                camera.scene = previewScene;
                camera.enabled = false;
                camera.cullingMask = 1 << PreviewLayer;
                camera.aspect = width / (float)height;
                if (rotation.HasValue)
                    camera.transform.rotation = Quaternion.Euler(rotation.Value);

                var framing = Frame3dCamera(camera, bounds, width, height, framePadding);
                if (!string.IsNullOrWhiteSpace(background))
                {
                    if (!ColorUtility.TryParseHtmlString(background, out var color))
                        throw new McpToolException(McpErrorCodes.InvalidParams, $"background is not a valid HTML color: '{background}'.");
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = color;
                }

                renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                var previousTarget = camera.targetTexture;
                var previousActive = RenderTexture.active;
                try
                {
                    camera.targetTexture = renderTexture;
                    camera.Render();
                    RenderTexture.active = renderTexture;
                    texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    texture.Apply();
                }
                finally
                {
                    RenderTexture.active = previousActive;
                    camera.targetTexture = previousTarget;
                }

                var png = texture.EncodeToPNG();
                var cameraPath = sourceCamera != null ? SceneSerializer.GetHierarchyPath(sourceCamera.gameObject) : null;
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["path"] = path,
                    ["previewType"] = "3d",
                    ["camera"] = sourceCamera != null ? sourceCamera.name : "standalone",
                    ["standaloneCamera"] = standalone,
                    ["cameraPath"] = cameraPath,
                    ["width"] = width,
                    ["height"] = height,
                    ["framePadding"] = framePadding,
                    ["rotationOverride"] = rotation.HasValue,
                    ["rotation"] = UnitySerializer.ToJson(camera.transform.eulerAngles),
                    ["boundsCenter"] = UnitySerializer.ToJson(bounds.center),
                    ["boundsSize"] = UnitySerializer.ToJson(bounds.size),
                    ["frameDistance"] = framing.Distance,
                    ["orthographicSize"] = framing.OrthographicSize,
                    ["nonDestructive"] = true,
                    ["sizeBytes"] = png.Length,
                }, new ImageContent { Data = png, MimeType = "image/png" }));
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
                if (previewObject != null)
                    UnityEngine.Object.DestroyImmediate(previewObject);
                if (instance != null)
                    UnityEngine.Object.DestroyImmediate(instance);
                if (previewScene.IsValid())
                    EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        private static Camera ResolveGameplayCamera(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
                {
                    foreach (var go in EnumerateSceneObjects(SceneManager.GetSceneAt(sceneIndex)))
                    {
                        if (!string.Equals(SceneSerializer.GetHierarchyPath(go), path, StringComparison.Ordinal))
                            continue;
                        var camera = go.GetComponent<Camera>();
                        if (camera != null)
                            return camera;
                        throw new McpToolException(McpErrorCodes.NotFound, $"No Camera component found at cameraPath '{path}'.");
                    }
                }
                throw new McpToolException(McpErrorCodes.NotFound, $"Camera GameObject not found: '{path}'.");
            }

            var main = Camera.main;
            if (main != null && main.enabled && main.gameObject.activeInHierarchy)
                return main;

            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                foreach (var go in EnumerateSceneObjects(SceneManager.GetSceneAt(sceneIndex)))
                {
                    var camera = go.GetComponent<Camera>();
                    if (camera != null && camera.enabled && go.activeInHierarchy)
                        return camera;
                }
            }

            throw new McpToolException(McpErrorCodes.NotFound, "No enabled gameplay camera found in the loaded scenes.");
        }

        private static void CopyGameplayCamera(Camera source, Camera target)
        {
            target.CopyFrom(source);
            target.targetTexture = null;

            foreach (var component in source.GetComponents<Component>())
            {
                if (component == null || component is Transform || component is Camera || component is AudioListener)
                    continue;

                var type = component.GetType();
                var typeName = type.FullName;
                if (string.IsNullOrEmpty(typeName) || !typeName.EndsWith("AdditionalCameraData", StringComparison.Ordinal))
                    continue;

                var copy = target.gameObject.GetComponent(type) ?? target.gameObject.AddComponent(type);
                EditorUtility.CopySerialized(component, copy);
            }
        }

        private static Vector3? ReadEulerRotation(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token is not JObject rotation)
                throw new McpToolException(McpErrorCodes.InvalidParams, "rotation must be an object with optional x, y, and z Euler angles in degrees.");

            return new Vector3(
                (float)(rotation["x"] ?? 0f),
                (float)(rotation["y"] ?? 0f),
                (float)(rotation["z"] ?? 0f));
        }

        private static Bounds GetRendererBounds(GameObject root, string path)
        {
            var found = false;
            var bounds = default(Bounds);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                    bounds.Encapsulate(renderer.bounds);
            }

            if (!found)
                throw new McpToolException(McpErrorCodes.NotFound, $"Prefab '{path}' has no enabled renderer in its hierarchy.");
            return bounds;
        }

        private static Frame3dResult Frame3dCamera(Camera camera, Bounds bounds, int width, int height, float padding)
        {
            var rotation = camera.transform.rotation;
            var forward = rotation * Vector3.forward;
            var radius = Mathf.Max(0.001f, bounds.extents.magnitude);
            var paddedRadius = radius * (1f + padding);
            var distance = 0f;
            var orthographicSize = 0f;

            if (camera.orthographic)
            {
                orthographicSize = paddedRadius;
                distance = Mathf.Max(paddedRadius * 2f, camera.nearClipPlane + paddedRadius);
            }
            else
            {
                var aspect = width / (float)Mathf.Max(1, height);
                var verticalHalfFov = camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
                var horizontalHalfFov = Mathf.Atan(Mathf.Tan(verticalHalfFov) * aspect);
                var halfFov = Mathf.Min(verticalHalfFov, horizontalHalfFov);
                distance = paddedRadius / Mathf.Max(0.01f, Mathf.Sin(halfFov));
            }

            camera.transform.SetPositionAndRotation(bounds.center - forward * distance, rotation);
            if (camera.farClipPlane < distance + radius)
                camera.farClipPlane = distance + radius * 2f;
            if (camera.nearClipPlane > distance - radius)
                camera.nearClipPlane = Mathf.Max(0.01f, (distance - radius) * 0.5f);

            return new Frame3dResult(distance, orthographicSize);
        }

        private static IEnumerable<GameObject> EnumerateSceneObjects(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var go in EnumerateTree(root))
                    yield return go;
            }
        }

        private static IEnumerable<GameObject> EnumerateTree(GameObject root)
        {
            yield return root;
            foreach (Transform child in root.transform)
            {
                foreach (var go in EnumerateTree(child.gameObject))
                    yield return go;
            }
        }

        private static int GetDefaultCameraWidth(Camera camera) => camera.pixelWidth > 0 ? camera.pixelWidth : 1280;

        private static int GetDefaultCameraHeight(Camera camera) => camera.pixelHeight > 0 ? camera.pixelHeight : 720;

        private readonly struct Frame3dResult
        {
            public float Distance { get; }
            public float OrthographicSize { get; }

            public Frame3dResult(float distance, float orthographicSize)
            {
                Distance = distance;
                OrthographicSize = orthographicSize;
            }
        }

        private static JObject BuildLayoutReport(RectTransform root, RectTransform viewport)
        {
            var nodes = new JArray();
            var warnings = new JArray();
            var controls = new List<(string path, Rect bounds)>();
            var corners = new Vector3[4];

            foreach (var rect in root.GetComponentsInChildren<RectTransform>(false))
            {
                var path = PreviewPathOf(root, rect);
                var bounds = RectIn(viewport, rect, corners);
                var parentLayout = ActiveParentLayout(rect);
                var fitter = rect.GetComponent<ContentSizeFitter>();
                var horizontalFitter = fitter != null && fitter.isActiveAndEnabled &&
                                       fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained;
                var verticalFitter = fitter != null && fitter.isActiveAndEnabled &&
                                     fitter.verticalFit != ContentSizeFitter.FitMode.Unconstrained;
                var parentOwnsWidth = ParentControlsSize(parentLayout, true);
                var parentOwnsHeight = ParentControlsSize(parentLayout, false);
                var aspect = rect.GetComponent<AspectRatioFitter>();
                var aspectOwnsWidth = aspect != null && aspect.isActiveAndEnabled &&
                                      aspect.aspectMode is AspectRatioFitter.AspectMode.HeightControlsWidth or
                                          AspectRatioFitter.AspectMode.FitInParent or
                                          AspectRatioFitter.AspectMode.EnvelopeParent;
                var aspectOwnsHeight = aspect != null && aspect.isActiveAndEnabled &&
                                       aspect.aspectMode is AspectRatioFitter.AspectMode.WidthControlsHeight or
                                           AspectRatioFitter.AspectMode.FitInParent or
                                           AspectRatioFitter.AspectMode.EnvelopeParent;
                var aspectOwnsPosition = aspect != null && aspect.isActiveAndEnabled &&
                                         aspect.aspectMode is AspectRatioFitter.AspectMode.FitInParent or
                                             AspectRatioFitter.AspectMode.EnvelopeParent;
                var node = new JObject
                {
                    ["path"] = path,
                    ["rect"] = RectJson(bounds),
                    ["resolvedSize"] = new JArray(Round(rect.rect.width), Round(rect.rect.height)),
                    ["positionOwner"] = parentLayout != null
                        ? "parentLayout"
                        : aspectOwnsPosition ? "aspectRatioFitter" : "anchors",
                    ["widthOwners"] = Owners(parentOwnsWidth, horizontalFitter, aspectOwnsWidth, rect.anchorMin.x != rect.anchorMax.x),
                    ["heightOwners"] = Owners(parentOwnsHeight, verticalFitter, aspectOwnsHeight, rect.anchorMin.y != rect.anchorMax.y),
                    ["anchors"] = new JObject
                    {
                        ["min"] = new JArray(Round(rect.anchorMin.x), Round(rect.anchorMin.y)),
                        ["max"] = new JArray(Round(rect.anchorMax.x), Round(rect.anchorMax.y)),
                        ["pivot"] = new JArray(Round(rect.pivot.x), Round(rect.pivot.y)),
                    },
                };
                if (rect.drivenByObject != null)
                {
                    node["driven"] = new JObject
                    {
                        ["by"] = rect.drivenByObject.GetType().Name,
                    };
                }

                if (rect.rect.width <= 0.01f || rect.rect.height <= 0.01f)
                    AddLayoutWarning(warnings, "non_positive_size", path, "Resolved width or height is not positive.");

                var viewportBounds = viewport.rect;
                if (!bounds.Overlaps(viewportBounds))
                    AddLayoutWarning(warnings, "outside_viewport", path, "Active element is outside the preview viewport.", "info");
                else if (!Contains(viewportBounds, bounds))
                    AddLayoutWarning(warnings, "clipped_by_viewport", path, "Active element extends beyond the preview viewport.", "info");

                if (parentOwnsWidth && horizontalFitter)
                    AddLayoutWarning(warnings, "multiple_width_owners", path, "Parent layout and ContentSizeFitter both control width.");
                if (parentOwnsHeight && verticalFitter)
                    AddLayoutWarning(warnings, "multiple_height_owners", path, "Parent layout and ContentSizeFitter both control height.");
                if (aspectOwnsWidth && (parentOwnsWidth || horizontalFitter))
                    AddLayoutWarning(warnings, "multiple_width_owners", path, "AspectRatioFitter and another layout system both control width.");
                if (aspectOwnsHeight && (parentOwnsHeight || verticalFitter))
                    AddLayoutWarning(warnings, "multiple_height_owners", path, "AspectRatioFitter and another layout system both control height.");

                var activeLayouts = 0;
                foreach (var layout in rect.GetComponents<LayoutGroup>())
                    if (layout != null && layout.isActiveAndEnabled)
                        activeLayouts++;
                if (activeLayouts > 1)
                    AddLayoutWarning(warnings, "multiple_layout_groups", path, "More than one active LayoutGroup controls this element.");

                var layoutElement = rect.GetComponent<LayoutElement>();
                if (layoutElement != null && layoutElement.ignoreLayout &&
                    (layoutElement.minWidth >= 0f || layoutElement.minHeight >= 0f ||
                     layoutElement.preferredWidth >= 0f || layoutElement.preferredHeight >= 0f ||
                     layoutElement.flexibleWidth >= 0f || layoutElement.flexibleHeight >= 0f))
                {
                    AddLayoutWarning(warnings, "ignored_layout_values", path, "LayoutElement sizing values are ignored because ignoreLayout is enabled.", "info");
                }

                AddTextLayout(node, warnings, rect, path);
                AddMaskWarnings(warnings, root, viewport, rect, bounds, path, corners);
                nodes.Add(node);

                var selectable = rect.GetComponent<Selectable>();
                if (selectable != null && selectable.isActiveAndEnabled && selectable.interactable)
                    controls.Add((path, bounds));
            }

            for (var i = 0; i < controls.Count; i++)
            for (var j = i + 1; j < controls.Count; j++)
            {
                var overlap = Intersection(controls[i].bounds, controls[j].bounds);
                if (overlap.width <= 1f || overlap.height <= 1f)
                    continue;
                AddLayoutWarning(
                    warnings,
                    "overlapping_controls",
                    controls[i].path,
                    $"Interactive control overlaps '{controls[j].path}'.",
                    "warning",
                    controls[j].path);
            }

            return new JObject
            {
                ["nodeCount"] = nodes.Count,
                ["warningCount"] = warnings.Count,
                ["nodes"] = nodes,
                ["warnings"] = warnings,
            };
        }

        private static void AddTextLayout(JObject node, JArray warnings, RectTransform rect, string path)
        {
            var text = FindComponentByTypeName(rect.gameObject, "TMPro.TMP_Text");
            if (text == null)
                return;

            var preferredWidth = GetFloatProperty(text, "preferredWidth");
            var preferredHeight = GetFloatProperty(text, "preferredHeight");
            var wrapping = GetPropertyString(text, "textWrappingMode");
            var overflow = GetPropertyString(text, "overflowMode");
            node["textFit"] = new JObject
            {
                ["preferredSize"] = new JArray(Round(preferredWidth), Round(preferredHeight)),
                ["wrapping"] = wrapping,
                ["overflow"] = overflow,
            };

            var noWrap = wrapping.IndexOf("NoWrap", StringComparison.OrdinalIgnoreCase) >= 0;
            var clippedWidth = noWrap && preferredWidth > rect.rect.width + 1f;
            var clippedHeight = preferredHeight > rect.rect.height + 1f;
            if (!string.Equals(overflow, "Overflow", StringComparison.OrdinalIgnoreCase) &&
                (clippedWidth || clippedHeight))
            {
                AddLayoutWarning(warnings, "text_exceeds_rect", path, "Resolved text content exceeds its RectTransform.");
            }
        }

        private static void AddMaskWarnings(
            JArray warnings,
            RectTransform root,
            RectTransform viewport,
            RectTransform rect,
            Rect bounds,
            string path,
            Vector3[] corners)
        {
            for (var parent = rect.parent as RectTransform;
                 parent != null && parent != viewport;
                 parent = parent.parent as RectTransform)
            {
                var mask = parent.GetComponent<Mask>();
                var rectMask = parent.GetComponent<RectMask2D>();
                if ((mask == null || !mask.isActiveAndEnabled) &&
                    (rectMask == null || !rectMask.isActiveAndEnabled))
                {
                    continue;
                }

                if (!Contains(RectIn(viewport, parent, corners), bounds))
                {
                    AddLayoutWarning(
                        warnings,
                        "clipped_by_parent_mask",
                        path,
                        $"Element extends beyond mask '{PreviewPathOf(root, parent)}'.",
                        "info");
                }
                return;
            }
        }

        private static LayoutGroup ActiveParentLayout(RectTransform rect)
        {
            if (rect.parent is not RectTransform parent)
                return null;
            var element = rect.GetComponent<LayoutElement>();
            if (element != null && element.ignoreLayout)
                return null;
            foreach (var layout in parent.GetComponents<LayoutGroup>())
                if (layout != null && layout.isActiveAndEnabled)
                    return layout;
            return null;
        }

        private static bool ParentControlsSize(LayoutGroup layout, bool horizontal)
        {
            if (layout is GridLayoutGroup)
                return true;
            if (layout is HorizontalOrVerticalLayoutGroup group)
                return horizontal ? group.childControlWidth : group.childControlHeight;
            return false;
        }

        private static JArray Owners(bool parentLayout, bool fitter, bool aspect, bool stretched)
        {
            var owners = new JArray();
            if (parentLayout)
                owners.Add("parentLayout");
            if (fitter)
                owners.Add("contentSizeFitter");
            if (aspect)
                owners.Add("aspectRatioFitter");
            if (!parentLayout && !fitter && !aspect)
                owners.Add(stretched ? "anchors" : "sizeDelta");
            return owners;
        }

        private static Rect RectIn(RectTransform coordinateSpace, RectTransform rect, Vector3[] corners)
        {
            rect.GetWorldCorners(corners);
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (var i = 0; i < corners.Length; i++)
            {
                var point = (Vector2)coordinateSpace.InverseTransformPoint(corners[i]);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static Rect Intersection(Rect a, Rect b)
            => Rect.MinMaxRect(
                Mathf.Max(a.xMin, b.xMin),
                Mathf.Max(a.yMin, b.yMin),
                Mathf.Min(a.xMax, b.xMax),
                Mathf.Min(a.yMax, b.yMax));

        private static bool Contains(Rect outer, Rect inner)
            => inner.xMin >= outer.xMin - 0.01f &&
               inner.yMin >= outer.yMin - 0.01f &&
               inner.xMax <= outer.xMax + 0.01f &&
               inner.yMax <= outer.yMax + 0.01f;

        private static JObject RectJson(Rect rect)
            => new()
            {
                ["x"] = Round(rect.x),
                ["y"] = Round(rect.y),
                ["width"] = Round(rect.width),
                ["height"] = Round(rect.height),
            };

        private static void AddLayoutWarning(
            JArray warnings,
            string code,
            string path,
            string message,
            string severity = "warning",
            string relatedPath = null)
        {
            var warning = new JObject
            {
                ["code"] = code,
                ["severity"] = severity,
                ["path"] = path,
                ["message"] = message,
            };
            if (!string.IsNullOrWhiteSpace(relatedPath))
                warning["relatedPath"] = relatedPath;
            warnings.Add(warning);
        }

        private static float GetFloatProperty(Component component, string propertyName)
        {
            try
            {
                var value = component.GetType().GetProperty(propertyName)?.GetValue(component);
                return value == null ? 0f : Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
        }

        private static string GetPropertyString(Component component, string propertyName)
        {
            try
            {
                return component.GetType().GetProperty(propertyName)?.GetValue(component)?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static float Round(float value)
            => Mathf.Abs(value) < 0.0005f ? 0f : Mathf.Round(value * 1000f) / 1000f;

        private static void EnsureParentFolder(string assetPath)
        {
            var dir = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || AssetDatabase.IsValidFolder(dir)) return;

            var parts = dir.Split('/');
            if (parts.Length < 2) return;
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        private static void ReplaceExistingAsset(string replacement, string dest)
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
            if (AssetDatabase.LoadMainAssetAtPath(path) != null || AssetDatabase.IsValidFolder(path))
                AssetDatabase.DeleteAsset(path);
        }

        private static string UniqueSiblingPath(string assetPath, string label)
        {
            var dir = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            var name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            var ext = System.IO.Path.GetExtension(assetPath);
            for (var i = 0; i < 20; i++)
            {
                var candidateName = $"{name}.__mcp_{label}_{Guid.NewGuid():N}{ext}";
                var candidate = string.IsNullOrEmpty(dir) ? candidateName : $"{dir}/{candidateName}";
                if (AssetDatabase.LoadMainAssetAtPath(candidate) == null && !AssetDatabase.IsValidFolder(candidate))
                    return candidate;
            }
            throw new McpToolException(McpErrorCodes.Conflict, $"Unable to allocate temporary path beside '{assetPath}'.");
        }

        private static Vector2 ResolveRectSize(RectTransform rect)
        {
            var size = rect.sizeDelta;
            if (size.x <= 0f)
                size.x = rect.rect.width;
            if (size.y <= 0f)
                size.y = rect.rect.height;
            if (size.x <= 0f)
                size.x = 512f;
            if (size.y <= 0f)
                size.y = 256f;
            return size;
        }

        internal static float ResolveScaleWithScreenSize(
            float viewportWidth,
            float viewportHeight,
            float referenceWidth,
            float referenceHeight,
            float matchWidthOrHeight)
        {
            if (viewportWidth <= 0f || viewportHeight <= 0f || referenceWidth <= 0f || referenceHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(viewportWidth), "Viewport and reference dimensions must be positive.");

            var logWidth = Mathf.Log(viewportWidth / referenceWidth, 2f);
            var logHeight = Mathf.Log(viewportHeight / referenceHeight, 2f);
            return Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, Mathf.Clamp01(matchWidthOrHeight)));
        }

        private static int ApplyStateOverrides(Transform root, JArray overrides)
        {
            if (overrides == null || overrides.Count == 0)
                return 0;

            var paths = new Dictionary<string, Transform>(StringComparer.Ordinal);
            IndexPreviewPaths(root, root, paths);
            var applied = 0;
            foreach (var token in overrides)
            {
                if (token is not JObject state)
                    throw new McpToolException(McpErrorCodes.ValidationFailed, "stateOverrides entries must be objects.");
                var path = state.Value<string>("path");
                if (string.IsNullOrWhiteSpace(path) || !paths.TryGetValue(path, out var target))
                    throw new McpToolException(McpErrorCodes.NotFound, $"state override path not found: '{path}'.");

                if (state.Property("active") != null)
                    target.gameObject.SetActive(state.Value<bool>("active"));
                if (state.Property("text") != null)
                {
                    var text = FindComponentByTypeName(target.gameObject, "TMPro.TMP_Text");
                    if (text == null)
                        throw new McpToolException(McpErrorCodes.ValidationFailed, $"state override path has no TMP_Text: '{path}'.");
                    var serialized = new SerializedObject(text);
                    var value = serialized.FindProperty("m_text");
                    if (value == null)
                        throw new McpToolException(McpErrorCodes.ToolError, $"TMP_Text at '{path}' has no serialized m_text property.");
                    value.stringValue = state.Value<string>("text") ?? string.Empty;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                if (state.Property("sprite") != null)
                {
                    var image = target.GetComponent<Image>();
                    if (image == null)
                        throw new McpToolException(McpErrorCodes.ValidationFailed, $"state override path has no Image: '{path}'.");
                    var spritePath = state.Value<string>("sprite");
                    var sprite = string.IsNullOrWhiteSpace(spritePath)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                    if (!string.IsNullOrWhiteSpace(spritePath) && sprite == null)
                        throw new McpToolException(McpErrorCodes.NotFound, $"state override sprite not found: '{spritePath}'.");
                    image.sprite = sprite;
                }
                applied++;
            }
            return applied;
        }

        private static Component FindComponentByTypeName(GameObject gameObject, string typeName)
        {
            foreach (var component in gameObject.GetComponents<Component>())
            {
                for (var type = component?.GetType(); type != null; type = type.BaseType)
                    if (type.FullName == typeName)
                        return component;
            }
            return null;
        }

        private static void ForceTmpMeshUpdates(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().FullName != "TMPro.TextMeshProUGUI")
                    continue;
                var method = component.GetType().GetMethod(
                    "ForceMeshUpdate",
                    new[] { typeof(bool), typeof(bool) });
                method?.Invoke(component, new object[] { true, true });
            }
        }

        private static void IndexPreviewPaths(Transform root, Transform transform, Dictionary<string, Transform> paths)
        {
            paths[PreviewPathOf(root, transform)] = transform;
            foreach (Transform child in transform)
                IndexPreviewPaths(root, child, paths);
        }

        private static string PreviewPathOf(Transform root, Transform transform)
        {
            var parts = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
            {
                parts.Push(current == root ? current.name : $"{current.name}[{current.GetSiblingIndex()}]");
                if (current == root)
                    break;
            }
            return string.Join("/", parts);
        }

        private static Color ParseColor(string value, Color fallback)
        {
            return !string.IsNullOrWhiteSpace(value) && ColorUtility.TryParseHtmlString(value, out var color)
                ? color
                : fallback;
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            root.layer = layer;
            foreach (Transform child in root.transform)
                SetLayerRecursive(child.gameObject, layer);
        }
    }
}
