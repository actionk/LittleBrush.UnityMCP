using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using LittleBrushGames.Mcp.Editor.Dispatch;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class MaterialProvider : IToolProvider
    {
        private const int DefaultPageSize = 25;

        public string Namespace => "material";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "material.read",
                Description = "Read a material's shader, keywords, render queue, and paged shader properties with a concurrency hash. Properties default to 25; descriptions are opt-in.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path""], ""additionalProperties"": false,
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""propertyOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""propertyLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                        ""includeDescriptions"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = Read,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "material.write",
                Description = "Set material shader, keywords, renderQueue, and shader properties with expectedHash and dryRun validation.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path"", ""expectedHash""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"" },
                        ""shader"": { ""type"": ""string"" },
                        ""keywords"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""renderQueue"": { ""type"": ""integer"" },
                        ""properties"": { ""type"": ""object"" },
                        ""dryRun"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = Write,
            });
            reg.Register(new ToolDescriptor
            {
                Name = "shader.inspect",
                Description = "Inspect a Shader asset's name plus paged properties and compiler messages. Each page defaults to 25; property descriptions are opt-in.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path""], ""additionalProperties"": false,
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""propertyOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""propertyLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                        ""messageOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""messageLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                        ""includeDescriptions"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = InspectShader,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
        }

        private static ValueTask<ToolResult> Read(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"Material not found: '{path}'.");
            return new ValueTask<ToolResult>(ToolResult.Ok(SerializeMaterial(path, material, ctx.Arguments)));
        }

        private static ValueTask<ToolResult> Write(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"Material not found: '{path}'.");
            var hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            if (!string.Equals((string)ctx.Arguments["expectedHash"], hash, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict, $"Material changed since read: '{path}'.", new JObject { ["currentHash"] = hash });

            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            var target = dryRun ? new Material(material) : material;
            try
            {
                if (!dryRun) Undo.RecordObject(material, "MCP write material");
                Apply(target, ctx.Arguments);
                if (!dryRun)
                {
                    EditorUtility.SetDirty(material);
                    AssetDatabase.SaveAssetIfDirty(material);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                }
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["path"] = path, ["hash"] = hash, ["dryRun"] = dryRun }));
            }
            finally
            {
                if (dryRun) UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static ValueTask<ToolResult> InspectShader(ToolContext ctx, CancellationToken ct)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"Shader not found: '{path}'.");
            var shaderMessages = ShaderUtil.GetShaderMessages(shader);
            var messageOffset = ctx.Arguments["messageOffset"]?.Value<int>() ?? 0;
            var messageLimit = ctx.Arguments["messageLimit"]?.Value<int>() ?? DefaultPageSize;
            var messageEnd = (int)Math.Min(shaderMessages.Length, (long)messageOffset + messageLimit);
            var messages = new JArray();
            for (var i = messageOffset; i < messageEnd; i++)
            {
                var message = shaderMessages[i];
                messages.Add(new JObject { ["message"] = message.message, ["severity"] = message.severity.ToString(), ["line"] = message.line, ["platform"] = message.platform.ToString() });
            }
            var result = new JObject
            {
                ["path"] = path,
                ["name"] = shader.name,
                ["messageCount"] = shaderMessages.Length,
                ["messageOffset"] = messageOffset,
                ["returnedMessages"] = messages.Count,
                ["messagesTruncated"] = messageEnd < shaderMessages.Length,
                ["messages"] = messages,
            };
            if (messageEnd < shaderMessages.Length) result["nextMessageOffset"] = messageEnd;
            AddPropertyPage(result, shader, null, ctx.Arguments);
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static JObject SerializeMaterial(string path, Material material, JObject args)
        {
            var result = new JObject
            {
                ["path"] = path,
                ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                ["shader"] = material.shader?.name,
                ["shaderPath"] = material.shader != null ? AssetDatabase.GetAssetPath(material.shader) : null,
                ["renderQueue"] = material.renderQueue,
                ["keywords"] = new JArray(material.shaderKeywords),
            };
            AddPropertyPage(result, material.shader, material, args);
            return result;
        }

        private static void AddPropertyPage(JObject result, Shader shader, Material material, JObject args)
        {
            var count = shader?.GetPropertyCount() ?? 0;
            var offset = args["propertyOffset"]?.Value<int>() ?? 0;
            var limit = args["propertyLimit"]?.Value<int>() ?? DefaultPageSize;
            var end = (int)Math.Min(count, (long)offset + limit);
            var properties = new JArray();
            for (var i = offset; i < end; i++)
            {
                var name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                var entry = new JObject { ["name"] = name, ["type"] = type.ToString() };
                if (args["includeDescriptions"]?.Value<bool>() == true)
                    entry["description"] = shader.GetPropertyDescription(i);
                if (material != null) entry["value"] = ReadValue(material, name, type);
                properties.Add(entry);
            }
            result["propertyCount"] = count;
            result["propertyOffset"] = offset;
            result["returnedProperties"] = properties.Count;
            result["propertiesTruncated"] = end < count;
            if (end < count) result["nextPropertyOffset"] = end;
            result["properties"] = properties;
        }

        private static JToken ReadValue(Material material, string name, ShaderPropertyType type) => type switch
        {
            ShaderPropertyType.Color => new JObject { ["r"] = material.GetColor(name).r, ["g"] = material.GetColor(name).g, ["b"] = material.GetColor(name).b, ["a"] = material.GetColor(name).a },
            ShaderPropertyType.Vector => new JObject { ["x"] = material.GetVector(name).x, ["y"] = material.GetVector(name).y, ["z"] = material.GetVector(name).z, ["w"] = material.GetVector(name).w },
            ShaderPropertyType.Texture => material.GetTexture(name) != null ? UnitySerializer.ToJson(material.GetTexture(name)) : JValue.CreateNull(),
            ShaderPropertyType.Int => material.GetInt(name),
            _ => material.GetFloat(name),
        };

        private static void Apply(Material material, JObject args)
        {
            if ((string)args["shader"] is { Length: > 0 } shaderSelector)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderSelector) ?? Shader.Find(shaderSelector)
                    ?? throw new McpToolException(McpErrorCodes.NotFound, $"Shader not found: '{shaderSelector}'.");
                material.shader = shader;
            }
            if (args["renderQueue"] != null) material.renderQueue = (int)args["renderQueue"];
            if (args["keywords"] is JArray keywords) material.shaderKeywords = keywords.Values<string>().ToArray();
            if (args["properties"] is not JObject properties) return;
            foreach (var property in properties)
            {
                var index = material.shader.FindPropertyIndex(property.Key);
                if (index < 0) throw new McpToolException(McpErrorCodes.NotFound, $"Shader property not found: '{property.Key}'.");
                WriteValue(material, property.Key, material.shader.GetPropertyType(index), property.Value);
            }
        }

        private static void WriteValue(Material material, string name, ShaderPropertyType type, JToken value)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    material.SetColor(name, new Color((float)value["r"], (float)value["g"], (float)value["b"], (float)(value["a"] ?? 1f))); break;
                case ShaderPropertyType.Vector:
                    material.SetVector(name, new Vector4((float)value["x"], (float)value["y"], (float)value["z"], (float)value["w"])); break;
                case ShaderPropertyType.Texture:
                    if (value == null || value.Type == JTokenType.Null) material.SetTexture(name, null);
                    else
                    {
                        var path = value.Type == JTokenType.String ? (string)value : (string)value["assetPath"];
                        material.SetTexture(name, AssetDatabase.LoadAssetAtPath<Texture>(AssetProvider.NormalizeAssetPath(path))
                            ?? throw new McpToolException(McpErrorCodes.NotFound, $"Texture not found: '{path}'."));
                    }
                    break;
                case ShaderPropertyType.Int: material.SetInt(name, value.Value<int>()); break;
                default: material.SetFloat(name, value.Value<float>()); break;
            }
        }
    }
}
