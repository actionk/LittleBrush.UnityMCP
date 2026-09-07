#if HAS_UNITY_TIMELINE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp;
using LittleBrushGames.Mcp.Editor;
using LittleBrushGames.Mcp.Editor.Providers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace LittleBrushGames.Mcp.Modules.Timeline
{
    [McpToolProvider]
    public sealed class TimelineBindingProvider : IToolProvider
    {
        public string Namespace => "timeline.binding";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "timeline.binding.write",
                Description = "Bind one Timeline track on a loaded scene's PlayableDirector to a compatible scene object or component. Existing different bindings are protected; supports dryRun and optional scene save.",
                Availability = ToolAvailability.EditMode,
                ExclusiveGroup = "timeline-write",
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""timelinePath"", ""trackId"", ""directorPath"", ""targetPath""], ""additionalProperties"": false,
                    ""properties"": {
                        ""scenePath"": { ""type"": ""string"" },
                        ""timelinePath"": { ""type"": ""string"" },
                        ""trackId"": { ""type"": ""string"" },
                        ""directorPath"": { ""type"": ""string"" },
                        ""targetPath"": { ""type"": ""string"" },
                        ""targetComponentType"": { ""type"": ""string"" },
                        ""dryRun"": { ""type"": ""boolean"" },
                        ""save"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = Write,
            });
        }

        private static ValueTask<ToolResult> Write(ToolContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var scene = ResolveScene((string)ctx.Arguments["scenePath"]);
            var timelinePath = TimelineProvider.NormalizeAssetPath(RequiredString(ctx.Arguments, "timelinePath"));
            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(timelinePath)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"Timeline asset not found: '{timelinePath}'.");
            var trackId = RequiredString(ctx.Arguments, "trackId");
            var track = Flatten(timeline).FirstOrDefault(candidate =>
                TimelineProvider.LocalId(candidate).ToString(CultureInfo.InvariantCulture) == trackId)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"Timeline track not found: '{trackId}'.");
            var directorObject = ResolveObject(scene, RequiredString(ctx.Arguments, "directorPath"));
            var director = directorObject.GetComponent<PlayableDirector>()
                ?? throw new McpToolException(McpErrorCodes.NotFound,
                    $"GameObject '{SceneSerializer.GetHierarchyPath(directorObject)}' has no PlayableDirector component.");
            if (director.playableAsset != timeline)
                throw TimelineProvider.Validation($"PlayableDirector does not use Timeline '{timelinePath}'.");

            var targetObject = ResolveObject(scene, RequiredString(ctx.Arguments, "targetPath"));
            var expectedType = track.outputs.Select(output => output.outputTargetType).FirstOrDefault(type => type != null);
            var binding = ResolveBinding(targetObject, expectedType, (string)ctx.Arguments["targetComponentType"]);
            var existing = director.GetGenericBinding(track);
            if (existing != null && existing != binding)
                throw new McpToolException(McpErrorCodes.Conflict,
                    $"Track '{track.name}' is already bound to '{DescribeBinding(existing)}'.");

            var result = new JObject
            {
                ["scenePath"] = scene.path,
                ["timelinePath"] = timelinePath,
                ["trackId"] = trackId,
                ["trackName"] = track.name,
                ["directorPath"] = SceneSerializer.GetHierarchyPath(directorObject),
                ["targetPath"] = SceneSerializer.GetHierarchyPath(targetObject),
                ["bindingType"] = binding.GetType().FullName,
                ["changed"] = existing != binding,
                ["dryRun"] = ctx.Arguments["dryRun"]?.Value<bool>() == true,
            };
            if ((bool)result["dryRun"] || existing == binding)
                return new ValueTask<ToolResult>(ToolResult.Ok(result));

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP Timeline Binding Write");
            Undo.RecordObject(director, "MCP Timeline Binding Write");
            try
            {
                director.SetGenericBinding(track, binding);
                EditorUtility.SetDirty(director);
                EditorSceneManager.MarkSceneDirty(scene);
                director.RebuildGraph();
                director.Evaluate();
                if (ctx.Arguments["save"]?.Value<bool>() == true)
                    result["saved"] = EditorSceneManager.SaveScene(scene);
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
            Undo.CollapseUndoOperations(undoGroup);
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static Object ResolveBinding(GameObject target, Type expectedType, string requestedTypeName)
        {
            if (expectedType == null || expectedType == typeof(GameObject))
                return target;
            var componentType = string.IsNullOrWhiteSpace(requestedTypeName)
                ? expectedType
                : ResolveComponentType(requestedTypeName);
            if (!expectedType.IsAssignableFrom(componentType))
                throw TimelineProvider.Validation(
                    $"Component type '{componentType.FullName}' is incompatible with track output '{expectedType.FullName}'.");
            return target.GetComponent(componentType)
                ?? throw new McpToolException(McpErrorCodes.NotFound,
                    $"GameObject '{SceneSerializer.GetHierarchyPath(target)}' has no '{componentType.FullName}' component.");
        }

        private static Type ResolveComponentType(string typeName)
        {
            var type = Type.GetType(typeName, false) ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, false))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                throw TimelineProvider.Validation($"Unknown component type: '{typeName}'.");
            return type;
        }

        private static string DescribeBinding(Object binding)
            => binding is Component component
                ? SceneSerializer.GetHierarchyPath(component.gameObject) + " (" + component.GetType().FullName + ")"
                : binding is GameObject gameObject
                    ? SceneSerializer.GetHierarchyPath(gameObject)
                    : binding.name;

        private static Scene ResolveScene(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                var active = SceneManager.GetActiveScene();
                if (active.IsValid()) return active;
                throw new McpToolException(McpErrorCodes.NotFound, "No active scene loaded.");
            }
            var normalized = path.Replace('\\', '/');
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(scene.path, normalized, StringComparison.OrdinalIgnoreCase)) return scene;
            }
            throw new McpToolException(McpErrorCodes.NotFound, $"Scene is not loaded: {normalized}");
        }

        private static GameObject ResolveObject(Scene scene, string path)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var candidate in Enumerate(root))
                    if (string.Equals(SceneSerializer.GetHierarchyPath(candidate), path, StringComparison.Ordinal))
                        return candidate;
            throw new McpToolException(McpErrorCodes.NotFound, $"Scene object not found: '{path}'.");
        }

        private static IEnumerable<GameObject> Enumerate(GameObject root)
        {
            yield return root;
            foreach (Transform child in root.transform)
                foreach (var descendant in Enumerate(child.gameObject))
                    yield return descendant;
        }

        private static IEnumerable<TrackAsset> Flatten(TimelineAsset timeline)
        {
            foreach (var root in timeline.GetRootTracks())
            {
                yield return root;
                foreach (var child in Flatten(root)) yield return child;
            }
        }

        private static IEnumerable<TrackAsset> Flatten(TrackAsset track)
        {
            foreach (var child in track.GetChildTracks())
            {
                yield return child;
                foreach (var descendant in Flatten(child)) yield return descendant;
            }
        }

        private static string RequiredString(JObject obj, string name)
            => obj[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)obj[name])
                ? ((string)obj[name]).Trim()
                : throw TimelineProvider.Validation($"{name} is required.");
    }
}
#endif
