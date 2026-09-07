using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class ProjectProvider : IToolProvider
    {
        public string Namespace => "project";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "project.prefab.instantiate",
                Description = "Instantiate a prefab into the active scene at optional world position.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""prefabPath""],
                    ""properties"": {
                        ""prefabPath"": { ""type"": ""string"" },
                        ""position"": { ""type"": ""object"" },
                        ""name"": { ""type"": ""string"" }
                    }
                }"),
                Handler = Instantiate,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.guid_to_path",
                Description = "Resolve a GUID to an asset path.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""required"": [""guid""], ""properties"": { ""guid"": { ""type"": ""string"" } } }"),
                Handler = GuidToPath,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.path_to_guid",
                Description = "Resolve an asset path to a GUID.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""required"": [""path""], ""properties"": { ""path"": { ""type"": ""string"" } } }"),
                Handler = PathToGuid,
            });
        }

        private static ValueTask<ToolResult> Instantiate(ToolContext ctx, CancellationToken __)
        {
            var path = (string)ctx.Arguments["prefabPath"];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new McpToolException(McpErrorCodes.NotFound, $"Prefab not found: {path}");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "MCP instantiate");
            if ((string)ctx.Arguments["name"] is { } name && !string.IsNullOrWhiteSpace(name))
                instance.name = name;
            if (ctx.Arguments["position"] is JObject pos)
                instance.transform.position = new Vector3((float)(pos["x"] ?? 0), (float)(pos["y"] ?? 0), (float)(pos["z"] ?? 0));

            EditorSceneManager.MarkSceneDirty(instance.scene);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["path"] = SceneSerializer.GetHierarchyPath(instance),
                ["prefabPath"] = path,
            }));
        }

        private static ValueTask<ToolResult> GuidToPath(ToolContext ctx, CancellationToken __)
        {
            var guid = (string)ctx.Arguments["guid"];
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["guid"] = guid, ["path"] = path }));
        }

        private static ValueTask<ToolResult> PathToGuid(ToolContext ctx, CancellationToken __)
        {
            var path = (string)ctx.Arguments["path"];
            var guid = AssetDatabase.AssetPathToGUID(path);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["guid"] = guid, ["path"] = path }));
        }
    }
}
