using System;
using System.Collections.Generic;
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

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class SceneProvider : IToolProvider
    {
        public string Namespace => "scene";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "scene.list",
                Description = "List loaded scenes.",
                Availability = ToolAvailability.Either,
                InputSchema = new JObject { ["type"] = "object" },
                Handler = ListScenes,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "scene.request_open",
                Description = "Open a scene in Edit Mode under the local EditorState trust policy. Replacing dirty loaded scenes is governed separately by UnsavedWork.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Async,
                Timeout = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10),
                ExclusiveGroup = "scene-open",
                Annotations = new JObject { ["destructiveHint"] = true },
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""reason""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"", ""minLength"": 1 },
                        ""reason"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 500, ""description"": ""Why the AI needs to change the loaded scene set."" }
                    }
                }"),
                Handler = RequestOpenScene,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "scene.read",
                Description = "Read a loaded or project scene hierarchy without changing the loaded scene set. Defaults to a sparse outline without components; request full profile or targeted component properties only when needed.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""scenePath"": { ""type"": ""string"" },
                        ""maxDepth"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 64, ""description"": ""Maximum hierarchy depth (1-64). Omit to use the configured default."" },
                        ""maxNodes"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50000, ""description"": ""Maximum returned hierarchy nodes. Default 100."" },
                        ""profile"": { ""type"": ""string"", ""enum"": [""outline"", ""full""] },
                        ""includeComponents"": { ""type"": ""boolean"" },
                        ""includeProperties"": { ""type"": ""boolean"" },
                        ""componentTypes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""propertySearch"": { ""type"": ""string"" },
                        ""propertyPaths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""propertyOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""propertyLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                        ""maxStringCharacters"": { ""type"": ""integer"", ""minimum"": 256, ""maximum"": 100000, ""description"": ""Per serialized string cap. Default 2000."" },
                        ""includeCurveValues"": { ""type"": ""boolean"", ""description"": ""Expand Gradient and AnimationCurve serialized values; omitted/false keeps compact placeholders."" }
                    }
                }"),
                Handler = ReadScene,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "scene.find",
                Description = "Find GameObjects in a loaded or project scene by name, path, tag, or component type without changing the loaded scene set. Returns compact path + entity ID results.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""scenePath"": { ""type"": ""string"" },
                        ""name"": { ""type"": ""string"" },
                        ""pathContains"": { ""type"": ""string"" },
                        ""tag"": { ""type"": ""string"" },
                        ""componentType"": { ""type"": ""string"" },
                        ""includeInactive"": { ""type"": ""boolean"" },
                        ""includeComponents"": { ""type"": ""boolean"" },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""description"": ""Page size. Default 25."" }
                    }
                }"),
                Handler = FindObjects,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "scene.read_object",
                Description = "Read one GameObject subtree from a loaded or project scene without changing the loaded scene set. Defaults to a sparse outline with component descriptors; serialized properties are opt-in, filterable, and paged.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""scenePath"": { ""type"": ""string"" },
                        ""path"": { ""type"": ""string"" },
                        ""entityId"": { ""type"": ""string"" },
                        ""maxDepth"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 64, ""description"": ""Maximum hierarchy depth (1-64). Omit to use the configured default."" },
                        ""maxNodes"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50000, ""description"": ""Maximum returned hierarchy nodes. Default 100."" },
                        ""profile"": { ""type"": ""string"", ""enum"": [""outline"", ""full""] },
                        ""includeComponents"": { ""type"": ""boolean"" },
                        ""includeProperties"": { ""type"": ""boolean"" },
                        ""componentTypes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""propertySearch"": { ""type"": ""string"" },
                        ""propertyPaths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""propertyOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""propertyLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                        ""maxStringCharacters"": { ""type"": ""integer"", ""minimum"": 256, ""maximum"": 100000, ""description"": ""Per serialized string cap. Default 2000."" },
                        ""includeCurveValues"": { ""type"": ""boolean"", ""description"": ""Expand Gradient and AnimationCurve serialized values; omitted/false keeps compact placeholders."" }
                    }
                }"),
                Handler = ReadObject,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "scene.write",
                Description = "Apply one transactional GameObject batch. Existing scenes require expectedHash from scene.read; dirty loaded scenes are rejected to protect unsaved work. Explicit unloaded scenePath values are opened additively only for this call, saved, and closed with the prior active scene restored. createIfMissing creates a new scene through this same tool. Mutating ops accept path or entityId. Op types: create_gameobject, delete_gameobject, rename_gameobject, set_active, set_transform, reparent, add_component, remove_component, set_property, instantiate_prefab.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""operations""],
                    ""properties"": {
                        ""scenePath"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"", ""description"": ""assetHash from scene.read; required for every existing scene."" },
                        ""createIfMissing"": { ""type"": ""boolean"", ""description"": ""Create scenePath when it does not exist. Requires explicit scenePath and save=true."" },
                        ""operations"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256, ""items"": { ""type"": ""object"" } },
                        ""save"": { ""type"": ""boolean"", ""description"": ""Required true for transient unloaded-scene writes; optional for already loaded scenes."" }
                    }
                }"),
                Handler = WriteScene,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "scene.replace_id",
                Description = "Find exact serialized string values and authored object references matching an ID across a loaded or project scene. Existing scene writes require expectedHash from scene.read; dirty loaded scenes are rejected. Unloaded scene writes open additively for this call, save, and close.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""oldId"", ""newId""],
                    ""properties"": {
                        ""scenePath"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"", ""description"": ""assetHash from scene.read; required for every existing scene write."" },
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
                Name = "scene.save",
                Description = "Save the active or specified loaded scene. Transient unloaded-scene writes save within scene.write or scene.replace_id.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": { ""scenePath"": { ""type"": ""string"" } }
                }"),
                Handler = SaveScene,
            });
        }

        private static ValueTask<ToolResult> ListScenes(ToolContext _, CancellationToken __)
        {
            var arr = new JArray();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                arr.Add(new JObject
                {
                    ["name"] = s.name,
                    ["path"] = s.path,
                    ["isLoaded"] = s.isLoaded,
                    ["isDirty"] = s.isDirty,
                });
            }
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["scenes"] = arr }));
        }

        private static async ValueTask<ToolResult> RequestOpenScene(ToolContext ctx, CancellationToken ct)
        {
            var requestedMode = (string)ctx.Arguments["mode"];
            if (requestedMode != null && requestedMode != "single")
                throw new McpToolException(McpErrorCodes.InvalidParams, "Only single-scene opening is supported.");

            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Scene asset not found: {path}");

            var reason = (string)ctx.Arguments["reason"];
            if (string.IsNullOrWhiteSpace(reason))
                throw new McpToolException(McpErrorCodes.InvalidParams, "reason is required.");
            reason = SanitizeReason(reason);

            var loaded = SceneManager.GetSceneByPath(path);
            if (loaded.IsValid() && loaded.isLoaded && SceneManager.sceneCount == 1)
                return ToolResult.Ok(SerializeOpenedScene(loaded, true));

            await AuthorizeDirtyLoadedScenes(ctx, reason, ct);

            return ToolResult.Ok(OpenScene(ctx, path, "trust_policy"));
        }

        private static JObject OpenScene(
            ToolContext ctx,
            string path,
            string authorization)
        {
            var opened = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Debug.Log(
                $"● [MCP] scene.request_open changed scenes mode=single authorization={authorization} " +
                $"request={ctx.RequestId} target={path}");
            var result = SerializeOpenedScene(opened, false);
            result["authorization"] = authorization;
            return result;
        }

        private static JObject SerializeOpenedScene(Scene scene, bool alreadyLoaded) => new()
        {
            ["action"] = alreadyLoaded ? "already_open" : "open",
            ["approved"] = true,
            ["name"] = scene.name,
            ["path"] = scene.path,
            ["mode"] = "single",
            ["isLoaded"] = scene.isLoaded,
            ["isActive"] = scene == SceneManager.GetActiveScene(),
            ["alreadyLoaded"] = alreadyLoaded,
        };

        private static async ValueTask AuthorizeDirtyLoadedScenes(ToolContext ctx, string reason, CancellationToken ct)
        {
            var dirtyScenes = GetDirtyLoadedScenes();
            if (dirtyScenes.Count > 0)
                await McpTrustPolicy.AuthorizeOnMainThreadAsync(
                    ctx,
                    ToolTrustCategory.UnsavedWork,
                    "AI requests to replace dirty scenes",
                    dirtyScenes.ToString(),
                    reason,
                    ct,
                    () => GetDirtyLoadedScenes().Count > 0);
        }

        private static JArray GetDirtyLoadedScenes()
        {
            var dirtyScenes = new JArray();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    dirtyScenes.Add(new JObject { ["name"] = scene.name, ["path"] = scene.path });
            }

            return dirtyScenes;
        }

        private static string SanitizeReason(string reason)
            => reason.Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static ValueTask<ToolResult> ReadScene(ToolContext ctx, CancellationToken __)
        {
            var scenePath = (string)ctx.Arguments["scenePath"];
            using var scope = SceneAssetScope.Open(scenePath);
            var scene = scope.Scene;
            var settings = Host.McpBridgeSettings.GetOrLoad();
            var opts = new SceneSerializer.Options
            {
                MaxDepth = ctx.Arguments["maxDepth"]?.Value<int>() ?? settings?.MaxSceneDepth ?? 10,
                MaxNodes = ctx.Arguments["maxNodes"]?.Value<int>() ?? Math.Min(settings?.MaxSceneNodes ?? 100, 100),
                IncludeComponents = ctx.Arguments["includeComponents"]?.Value<bool>() ?? false,
                IncludeProperties = ctx.Arguments["includeProperties"]?.Value<bool>() == true,
                IncludeCurveValues = ctx.Arguments["includeCurveValues"]?.Value<bool>() == true,
                Profile = ctx.Arguments["profile"]?.Value<string>() ?? "outline",
                ComponentTypes = ctx.Arguments["componentTypes"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                PropertySearch = ctx.Arguments["propertySearch"]?.Value<string>(),
                PropertyPaths = ctx.Arguments["propertyPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                PropertyOffset = ctx.Arguments["propertyOffset"]?.Value<int>() ?? 0,
                PropertyLimit = ctx.Arguments["propertyLimit"]?.Value<int>() ?? 25,
                MaxStringCharacters = ctx.Arguments["maxStringCharacters"]?.Value<int>() ?? 2000,
            };
            return new ValueTask<ToolResult>(ToolResult.Ok(scope.AddMetadata(SceneSerializer.Serialize(scene, opts))));
        }

        private static ValueTask<ToolResult> FindObjects(ToolContext ctx, CancellationToken __)
        {
            using var scope = SceneAssetScope.Open((string)ctx.Arguments["scenePath"]);
            var scene = scope.Scene;
            var name = (string)ctx.Arguments["name"];
            var pathContains = (string)ctx.Arguments["pathContains"];
            var tag = (string)ctx.Arguments["tag"];
            var componentTypeName = (string)ctx.Arguments["componentType"];
            var includeInactive = ctx.Arguments["includeInactive"]?.Value<bool>() == true;
            var includeComponents = ctx.Arguments["includeComponents"]?.Value<bool>() == true;
            var offset = ctx.Arguments["offset"]?.Value<int>() ?? 0;
            var limit = ctx.Arguments["limit"]?.Value<int>() ?? 25;
            var componentType = string.IsNullOrWhiteSpace(componentTypeName)
                ? null
                : ResolveComponentType(componentTypeName);

            if (string.IsNullOrWhiteSpace(name)
                && string.IsNullOrWhiteSpace(pathContains)
                && string.IsNullOrWhiteSpace(tag)
                && componentType == null)
                throw new McpToolException(McpErrorCodes.InvalidParams, "scene.find requires name, pathContains, tag, or componentType.");

            var matches = new JArray();
            var total = 0;
            foreach (var go in EnumerateSceneObjects(scene))
            {
                if (!includeInactive && !go.activeInHierarchy)
                    continue;
                if (!Matches(go, name, pathContains, tag, componentType))
                    continue;

                total++;
                if (total <= offset)
                    continue;
                if (matches.Count >= limit)
                    continue;

                matches.Add(SerializeMatch(go, includeComponents));
            }

            var result = new JObject
            {
                ["scenePath"] = scene.path,
                ["count"] = total,
                ["offset"] = offset,
                ["returned"] = matches.Count,
                ["truncated"] = offset + matches.Count < total,
                ["matches"] = matches,
            };
            if (offset + matches.Count < total)
                result["nextOffset"] = offset + matches.Count;
            return new ValueTask<ToolResult>(ToolResult.Ok(scope.AddMetadata(result)));
        }

        private static ValueTask<ToolResult> ReadObject(ToolContext ctx, CancellationToken __)
        {
            using var scope = SceneAssetScope.Open((string)ctx.Arguments["scenePath"]);
            var scene = scope.Scene;
            var go = ResolveObject(scene, ctx.Arguments);
            var settings = Host.McpBridgeSettings.GetOrLoad();
            var opts = new SceneSerializer.Options
            {
                MaxDepth = ctx.Arguments["maxDepth"]?.Value<int>() ?? settings?.MaxSceneDepth ?? 10,
                MaxNodes = ctx.Arguments["maxNodes"]?.Value<int>() ?? Math.Min(settings?.MaxSceneNodes ?? 100, 100),
                IncludeComponents = ctx.Arguments["includeComponents"]?.Value<bool>() ?? true,
                IncludeProperties = ctx.Arguments["includeProperties"]?.Value<bool>() == true,
                IncludeCurveValues = ctx.Arguments["includeCurveValues"]?.Value<bool>() == true,
                Profile = ctx.Arguments["profile"]?.Value<string>() ?? "outline",
                ComponentTypes = ctx.Arguments["componentTypes"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                PropertySearch = ctx.Arguments["propertySearch"]?.Value<string>(),
                PropertyPaths = ctx.Arguments["propertyPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                PropertyOffset = ctx.Arguments["propertyOffset"]?.Value<int>() ?? 0,
                PropertyLimit = ctx.Arguments["propertyLimit"]?.Value<int>() ?? 25,
                MaxStringCharacters = ctx.Arguments["maxStringCharacters"]?.Value<int>() ?? 2000,
            };

            return new ValueTask<ToolResult>(ToolResult.Ok(scope.AddMetadata(new JObject
            {
                ["scenePath"] = scene.path,
                ["object"] = SceneSerializer.SerializeRoot(go, opts),
            })));
        }

        private static ValueTask<ToolResult> WriteScene(ToolContext ctx, CancellationToken __)
        {
            var scenePath = (string)ctx.Arguments["scenePath"];
            var save = ctx.Arguments["save"]?.Value<bool>() == true;
            using var scope = SceneAssetScope.OpenForWrite(
                scenePath,
                ctx.Arguments["createIfMissing"]?.Value<bool>() == true);
            if (scope.OwnsTransientScene && !save)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    "save=true is required when writing an unloaded or new scene asset.");

            scope.RequireExpectedHash((string)ctx.Arguments["expectedHash"]);
            if (save)
                scope.ValidateCanSave();

            var operations = (JArray)ctx.Arguments["operations"];
            var result = SceneWriteOperations.Apply(scope.Scene, operations);
            var succeeded = result["failed"]?.Value<int>() == 0;
            if (succeeded && save)
                scope.Save();
            result["saved"] = succeeded && save;
            return new ValueTask<ToolResult>(ToolResult.Ok(scope.AddMetadata(result)));
        }

        private static ValueTask<ToolResult> ReplaceId(ToolContext ctx, CancellationToken __)
        {
            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            var save = ctx.Arguments["save"]?.Value<bool>() == true;
            using var scope = dryRun
                ? SceneAssetScope.Open((string)ctx.Arguments["scenePath"])
                : SceneAssetScope.OpenForWrite((string)ctx.Arguments["scenePath"], false);
            if (scope.OwnsTransientScene && !dryRun && !save)
                throw new McpToolException(McpErrorCodes.ValidationFailed,
                    "save=true is required when replacing IDs in an unloaded scene asset.");
            if (!dryRun)
                scope.RequireExpectedHash((string)ctx.Arguments["expectedHash"]);
            if (!dryRun && save)
                scope.ValidateCanSave();

            var result = SceneSerializedReplacement.Replace(
                scope.Scene,
                (string)ctx.Arguments["oldId"],
                (string)ctx.Arguments["newId"],
                dryRun,
                false,
                ctx.Arguments["maxResults"]?.Value<int>() ?? 100);
            if (!dryRun && save && result["changedCount"]?.Value<int>() > 0)
            {
                scope.Save();
                result["saved"] = true;
            }
            return new ValueTask<ToolResult>(ToolResult.Ok(scope.AddMetadata(result)));
        }

        private static ValueTask<ToolResult> SaveScene(ToolContext ctx, CancellationToken __)
        {
            var scene = ResolveScene((string)ctx.Arguments["scenePath"]);
            var ok = EditorSceneManager.SaveScene(scene);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["saved"] = ok, ["path"] = scene.path }));
        }

        private static Scene ResolveScene(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                var active = SceneManager.GetActiveScene();
                if (!active.IsValid())
                    throw new McpToolException(McpErrorCodes.NotFound, "No active scene loaded.");
                return active;
            }

            var normalized = path.Replace('\\', '/');
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var loaded = SceneManager.GetSceneAt(i);
                if (string.Equals(loaded.path, normalized, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }
            throw new McpToolException(McpErrorCodes.NotFound, $"Scene is not loaded: {normalized}");
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

        private static bool Matches(GameObject go, string name, string pathContains, string tag, Type componentType)
        {
            if (!string.IsNullOrWhiteSpace(name)
                && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            var path = SceneSerializer.GetHierarchyPath(go);
            if (!string.IsNullOrWhiteSpace(pathContains)
                && path.IndexOf(pathContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (!string.IsNullOrWhiteSpace(tag)
                && !string.Equals(go.tag, tag, StringComparison.Ordinal))
                return false;

            if (componentType != null && go.GetComponent(componentType) == null)
                return false;

            return true;
        }

        private static JObject SerializeMatch(GameObject go, bool includeComponents)
        {
            var entry = new JObject
            {
                ["name"] = go.name,
                ["path"] = SceneSerializer.GetHierarchyPath(go),
                ["entityId"] = UnitySerializer.ToEntityIdString(go),
                ["activeSelf"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["tag"] = go.tag,
                ["layer"] = go.layer,
            };

            if (includeComponents)
            {
                var components = new JArray();
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    components.Add(c.GetType().FullName);
                }
                entry["components"] = components;
            }

            return entry;
        }

        private static GameObject ResolveObject(Scene scene, JToken arguments)
        {
            var entityIdToken = arguments["entityId"];
            if (entityIdToken != null)
            {
                if (!UnitySerializer.TryParseEntityId(entityIdToken, out EntityId entityId))
                    throw new McpToolException(McpErrorCodes.InvalidParams, "entityId must be an unsigned 64-bit integer string.");

                var obj = EditorUtility.EntityIdToObject(entityId);
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null)
                    throw new McpToolException(McpErrorCodes.NotFound, $"GameObject not found for entityId {entityId}.");
                if (go.scene != scene)
                    throw new McpToolException(McpErrorCodes.NotFound, $"GameObject entityId {entityId} is not in scene '{scene.path}'.");
                return go;
            }

            var path = (string)arguments["path"];
            if (string.IsNullOrWhiteSpace(path))
                throw new McpToolException(McpErrorCodes.InvalidParams, "scene.read_object requires path or entityId.");

            foreach (var go in EnumerateSceneObjects(scene))
            {
                if (string.Equals(SceneSerializer.GetHierarchyPath(go), path, StringComparison.Ordinal))
                    return go;
            }

            throw new McpToolException(McpErrorCodes.NotFound, $"GameObject not found: {path}");
        }

        private static Type ResolveComponentType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (!typeof(Component).IsAssignableFrom(type)) continue;
                    if (type.FullName == name || type.Name == name) return type;
                }
            }

            throw new McpToolException(McpErrorCodes.NotFound, $"Component type not found: '{name}'.");
        }
    }
}
