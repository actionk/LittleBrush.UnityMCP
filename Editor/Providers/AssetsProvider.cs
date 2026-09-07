using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class AssetsProvider : IToolProvider
    {
        public string Namespace => "project.assets";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "project.assets.read",
                Description = "Return asset metadata and an optional filtered page of serialized properties. Properties are omitted unless explicitly requested.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""includeDependencies"": { ""type"": ""boolean"", ""description"": ""Include dependency paths; omitted/false returns only dependencyCount."" },
                        ""dependencyOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""dependencyLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""description"": ""Dependency page size. Default 25."" },
                        ""includeProperties"": { ""type"": ""boolean"" },
                        ""propertySearch"": { ""type"": ""string"" },
                        ""propertyPaths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                        ""includeCurveValues"": { ""type"": ""boolean"", ""description"": ""Expand Gradient and AnimationCurve serialized values; omitted/false keeps compact placeholders."" },
                        ""maxStringCharacters"": { ""type"": ""integer"", ""minimum"": 256, ""maximum"": 100000, ""description"": ""Per serialized string cap. Default 2000."" }
                    }
                }"),
                Handler = Read,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.assets.open",
                Description = "Select and ping an asset in the Project window.",
                Availability = ToolAvailability.Either,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path""],
                    ""properties"": { ""path"": { ""type"": ""string"" } }
                }"),
                Handler = Open,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.assets.write",
                Description = "Set serialized properties on an asset with optimistic concurrency. Requires expectedHash from project.assets.read; supports dryRun.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""expectedHash"", ""properties""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"" },
                        ""properties"": { ""type"": ""object"" },
                        ""dryRun"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = Write,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.assets.create",
                Description = "Create a ScriptableObject asset by concrete type and optionally set serialized properties. Supports dryRun.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""type""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""type"": { ""type"": ""string"" },
                        ""properties"": { ""type"": ""object"" },
                        ""dryRun"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = Create,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.importer.read",
                Description = "Read a filtered page of serialized AssetImporter properties.",
                Availability = ToolAvailability.EditMode,
                InputSchema = PropertyReadSchema(),
                Handler = ReadImporter,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.importer.write",
                Description = "Set one AssetImporter with expectedHash concurrency and dryRun validation; then reimport once. Use project.importer.write_many for multiple assets.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path"", ""expectedHash"", ""properties""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"" },
                        ""properties"": { ""type"": ""object"" },
                        ""dryRun"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = WriteImporter,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.importer.write_many",
                Description = "Batch different serialized AssetImporter changes with per-asset expectedHash values in one StartAssetEditing/StopAssetEditing envelope.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""items""], ""additionalProperties"": false,
                    ""properties"": {
                        ""dryRun"": { ""type"": ""boolean"" },
                        ""items"": {
                            ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256,
                            ""items"": {
                                ""type"": ""object"", ""required"": [""path"", ""expectedHash"", ""properties""], ""additionalProperties"": false,
                                ""properties"": {
                                    ""path"": { ""type"": ""string"" },
                                    ""expectedHash"": { ""type"": ""string"" },
                                    ""properties"": { ""type"": ""object"" }
                                }
                            }
                        }
                    }
                }"),
                Handler = WriteImporters,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "project.settings.list",
                Description = "Page serialized Unity ProjectSettings assets (tags/layers, physics, graphics, quality, navigation, and more).",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""properties"": { ""includeHashes"": { ""type"": ""boolean"", ""description"": ""Include per-file concurrency hashes; omitted/false keeps discovery compact."" }, ""offset"": { ""type"": ""integer"", ""minimum"": 0 }, ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""description"": ""Page size. Default 25."" } } }"),
                Handler = ListProjectSettings,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "project.settings.read",
                Description = "Read a filtered page of serialized properties and a concurrency hash from one ProjectSettings asset.",
                Availability = ToolAvailability.EditMode,
                InputSchema = PropertyReadSchema(),
                Handler = ReadProjectSettings,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "project.settings.write",
                Description = "Set serialized ProjectSettings properties with expectedHash and dryRun validation.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path"", ""expectedHash"", ""properties""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"" },
                        ""properties"": { ""type"": ""object"" },
                        ""dryRun"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = WriteProjectSettings,
            });
        }

        private static JObject PropertyReadSchema()
            => JObject.Parse(@"{
                ""type"": ""object"",
                ""required"": [""path""],
                ""properties"": {
                    ""path"": { ""type"": ""string"" },
                    ""propertySearch"": { ""type"": ""string"" },
                    ""propertyPaths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                    ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000 },
                    ""maxStringCharacters"": { ""type"": ""integer"", ""minimum"": 256, ""maximum"": 100000 }
                }
            }");

        private static ValueTask<ToolResult> Read(ToolContext ctx, CancellationToken __)
        {
            var path = (string)ctx.Arguments["path"];
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
                throw new McpToolException(McpErrorCodes.NotFound, $"Asset not found: {path}");

            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var dependencies = AssetDatabase.GetDependencies(path, false);
            var result = new JObject
            {
                ["path"] = path,
                ["guid"] = guid,
                ["type"] = AssetDatabase.GetMainAssetTypeAtPath(path)?.FullName,
                ["labels"] = new JArray(AssetDatabase.GetLabels(asset)),
                ["dependencyCount"] = dependencies.Length,
                ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
            };
            if (ctx.Arguments["includeDependencies"]?.Value<bool>() == true)
            {
                var dependencyOffset = ctx.Arguments["dependencyOffset"]?.Value<int>() ?? 0;
                var dependencyLimit = ctx.Arguments["dependencyLimit"]?.Value<int>() ?? 25;
                var page = dependencies.Skip(dependencyOffset).Take(dependencyLimit).ToArray();
                result["dependencyOffset"] = dependencyOffset;
                result["returnedDependencies"] = page.Length;
                result["dependenciesTruncated"] = dependencyOffset + page.Length < dependencies.Length;
                if (dependencyOffset + page.Length < dependencies.Length)
                    result["nextDependencyOffset"] = dependencyOffset + page.Length;
                result["dependencies"] = new JArray(page);
            }

            var includeProps = ctx.Arguments["includeProperties"]?.Value<bool>() == true
                || ctx.Arguments["propertySearch"] != null
                || ctx.Arguments["propertyPaths"] != null;
            if (includeProps && asset != null)
            {
                var includeCurveValues = ctx.Arguments["includeCurveValues"]?.Value<bool>() == true;
                AddPropertyPage(result, asset, ctx.Arguments, includeCurveValues);

                // For prefabs, include root GameObject components
                if (asset is GameObject go)
                    result["components"] = SerializeGameObjectComponents(go);
            }

            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static ValueTask<ToolResult> Open(ToolContext ctx, CancellationToken __)
        {
            var path = (string)ctx.Arguments["path"];
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Asset not found: {path}");
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["path"] = path,
                ["selected"] = true,
            }));
        }

        private static ValueTask<ToolResult> Write(ToolContext ctx, CancellationToken __)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]);
            var asset = AssetDatabase.LoadMainAssetAtPath(path)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"Asset not found: {path}");
            var expectedHash = (string)ctx.Arguments["expectedHash"];
            var currentHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            if (!string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict, $"Asset changed since read: '{path}'.", new JObject { ["expectedHash"] = expectedHash, ["currentHash"] = currentHash });

            var properties = ctx.Arguments["properties"] as JObject
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "properties required.");
            using var serialized = new SerializedObject(asset);
            var changed = ApplyProperties(serialized, properties);
            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            if (!dryRun)
            {
                Undo.RecordObject(asset, "MCP write asset properties");
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                currentHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            }

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["path"] = path,
                ["hash"] = currentHash,
                ["dryRun"] = dryRun,
                ["properties"] = changed,
            }));
        }

        private static ValueTask<ToolResult> Create(ToolContext ctx, CancellationToken __)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]);
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "path must end with .asset.");
            if (System.IO.File.Exists(System.IO.Path.GetFullPath(path)))
                throw new McpToolException(McpErrorCodes.Conflict, $"Asset already exists: '{path}'.");
            var parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent))
                throw new McpToolException(McpErrorCodes.NotFound, $"Parent folder not found: '{parent}'.");

            var type = ResolveScriptableObjectType((string)ctx.Arguments["type"]);
            var instance = ScriptableObject.CreateInstance(type);
            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            var created = false;
            try
            {
                using var serialized = new SerializedObject(instance);
                var changed = ApplyProperties(serialized, ctx.Arguments["properties"] as JObject ?? new JObject());
                if (dryRun)
                    return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["path"] = path, ["type"] = type.FullName, ["dryRun"] = true, ["properties"] = changed }));

                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.CreateAsset(instance, path);
                created = true;
                AssetDatabase.SaveAssetIfDirty(instance);
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["path"] = path,
                    ["guid"] = AssetDatabase.AssetPathToGUID(path),
                    ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                    ["type"] = type.FullName,
                    ["properties"] = changed,
                }));
            }
            catch
            {
                if (created) AssetDatabase.DeleteAsset(path);
                throw;
            }
            finally
            {
                if (!created) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static ValueTask<ToolResult> ReadImporter(ToolContext ctx, CancellationToken __)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]);
            var importer = AssetImporter.GetAtPath(path)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"AssetImporter not found: '{path}'.");
            var result = new JObject
            {
                ["path"] = path,
                ["type"] = importer.GetType().FullName,
                ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
            };
            AddPropertyPage(result, importer, ctx.Arguments);
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static ValueTask<ToolResult> WriteImporter(ToolContext ctx, CancellationToken __)
        {
            var path = AssetProvider.NormalizeAssetPath((string)ctx.Arguments["path"]);
            var importer = AssetImporter.GetAtPath(path)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"AssetImporter not found: '{path}'.");
            var currentHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            var expectedHash = (string)ctx.Arguments["expectedHash"];
            if (!string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict, $"Importer changed since read: '{path}'.", new JObject { ["expectedHash"] = expectedHash, ["currentHash"] = currentHash });

            using var serialized = new SerializedObject(importer);
            var changed = ApplyProperties(serialized, ctx.Arguments["properties"] as JObject
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "properties required."));
            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            if (!dryRun)
            {
                Undo.RecordObject(importer, "MCP write importer properties");
                serialized.ApplyModifiedProperties();
                importer.SaveAndReimport();
                currentHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            }
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["path"] = path, ["hash"] = currentHash, ["dryRun"] = dryRun, ["properties"] = changed }));
        }

        private static ValueTask<ToolResult> WriteImporters(ToolContext ctx, CancellationToken __)
        {
            var items = ctx.Arguments["items"] as JArray
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "items required.");
            if (items.Count is < 1 or > 256)
                throw new McpToolException(McpErrorCodes.InvalidParams, "items must contain between 1 and 256 entries.");

            var prepared = new JArray();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject item in items.OfType<JObject>())
            {
                string path = AssetProvider.NormalizeAssetPath((string)item["path"]);
                if (!paths.Add(path))
                    throw new McpToolException(McpErrorCodes.InvalidParams, $"Duplicate importer path: '{path}'.");
                AssetImporter importer = AssetImporter.GetAtPath(path)
                    ?? throw new McpToolException(McpErrorCodes.NotFound, $"AssetImporter not found: '{path}'.");
                string currentHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                string expectedHash = (string)item["expectedHash"];
                if (!string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
                    throw new McpToolException(McpErrorCodes.Conflict,
                        $"Importer changed since read: '{path}'.",
                        new JObject { ["expectedHash"] = expectedHash, ["currentHash"] = currentHash });
                JObject properties = item["properties"] as JObject
                    ?? throw new McpToolException(McpErrorCodes.ValidationFailed, $"properties required for '{path}'.");
                using var serialized = new SerializedObject(importer);
                JArray changed = ApplyProperties(serialized, properties);
                prepared.Add(new JObject
                {
                    ["path"] = path,
                    ["properties"] = properties.DeepClone(),
                    ["changed"] = changed,
                });
            }
            if (prepared.Count != items.Count)
                throw new McpToolException(McpErrorCodes.ValidationFailed, "Every item must be an object.");

            bool dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            if (!dryRun)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (JObject item in prepared.OfType<JObject>())
                    {
                        string path = (string)item["path"];
                        AssetImporter importer = AssetImporter.GetAtPath(path)
                            ?? throw new McpToolException(McpErrorCodes.NotFound, $"AssetImporter not found: '{path}'.");
                        Undo.RecordObject(importer, "MCP batch write importer properties");
                        using var serialized = new SerializedObject(importer);
                        ApplyProperties(serialized, (JObject)item["properties"]);
                        serialized.ApplyModifiedProperties();
                        importer.SaveAndReimport();
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }

            var results = new JArray(prepared.OfType<JObject>().Select(item => new JObject
            {
                ["path"] = (string)item["path"],
                ["hash"] = AssetDatabase.GetAssetDependencyHash((string)item["path"]).ToString(),
                ["properties"] = item["changed"].DeepClone(),
            }));
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["dryRun"] = dryRun,
                ["count"] = results.Count,
                ["results"] = results,
            }));
        }

        private static ValueTask<ToolResult> ListProjectSettings(ToolContext ctx, CancellationToken __)
        {
            var offset = ctx.Arguments["offset"]?.Value<int>() ?? 0;
            var limit = ctx.Arguments["limit"]?.Value<int>() ?? 25;
            var includeHashes = ctx.Arguments["includeHashes"]?.Value<bool>() == true;
            var paths = Directory.EnumerateFiles("ProjectSettings", "*.asset")
                .OrderBy(path => path, StringComparer.Ordinal).ToList();
            var items = new JArray();
            foreach (var path in paths.Skip(offset).Take(limit))
            {
                var normalized = path.Replace('\\', '/');
                var asset = LoadProjectSettingsObject(normalized);
                var item = new JObject { ["path"] = normalized, ["type"] = asset?.GetType().FullName };
                if (includeHashes) item["hash"] = HashFile(normalized);
                items.Add(item);
            }
            var result = new JObject
            {
                ["total"] = paths.Count,
                ["offset"] = offset,
                ["returned"] = items.Count,
                ["truncated"] = offset + items.Count < paths.Count,
                ["settings"] = items,
            };
            if (offset + items.Count < paths.Count)
                result["nextOffset"] = offset + items.Count;
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static ValueTask<ToolResult> ReadProjectSettings(ToolContext ctx, CancellationToken __)
        {
            var path = NormalizeProjectSettingsPath((string)ctx.Arguments["path"]);
            var asset = LoadProjectSettingsObject(path)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"ProjectSettings asset not found: '{path}'.");
            var result = new JObject
            {
                ["path"] = path,
                ["type"] = asset.GetType().FullName,
                ["hash"] = HashFile(path),
            };
            AddPropertyPage(result, asset, ctx.Arguments);
            return new ValueTask<ToolResult>(ToolResult.Ok(result));
        }

        private static ValueTask<ToolResult> WriteProjectSettings(ToolContext ctx, CancellationToken __)
        {
            var path = NormalizeProjectSettingsPath((string)ctx.Arguments["path"]);
            var asset = LoadProjectSettingsObject(path)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"ProjectSettings asset not found: '{path}'.");
            var currentHash = HashFile(path);
            if (!string.Equals((string)ctx.Arguments["expectedHash"], currentHash, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict, $"ProjectSettings changed since read: '{path}'.", new JObject { ["currentHash"] = currentHash });
            using var serialized = new SerializedObject(asset);
            var changed = ApplyProperties(serialized, ctx.Arguments["properties"] as JObject
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "properties required."));
            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            if (!dryRun)
            {
                Undo.RecordObject(asset, "MCP write project settings");
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                currentHash = HashFile(path);
            }
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["path"] = path, ["hash"] = currentHash, ["dryRun"] = dryRun, ["properties"] = changed }));
        }

        private static UnityEngine.Object LoadProjectSettingsObject(string path)
            => AssetDatabase.LoadMainAssetAtPath(path) ?? AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(asset => asset != null);

        public static string NormalizeProjectSettingsPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || Path.IsPathRooted(raw))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "ProjectSettings path must be project-relative.");
            var path = raw.Replace('\\', '/').Trim();
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath("ProjectSettings") + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "Path must be a .asset file directly under ProjectSettings/.");
            return path;
        }

        private static string HashFile(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static JArray ApplyProperties(SerializedObject serialized, JObject properties)
        {
            var changed = new JArray();
            foreach (var pair in properties)
            {
                if (pair.Key == "m_Script")
                    throw new McpToolException(McpErrorCodes.ValidationFailed, "m_Script cannot be changed.");
                var property = serialized.FindProperty(pair.Key)
                    ?? throw new McpToolException(McpErrorCodes.NotFound, $"Serialized property not found: '{pair.Key}'.");
                SceneWriteOperations.ApplySerializedValue(property, pair.Value);
                changed.Add(pair.Key);
            }
            return changed;
        }

        private static Type ResolveScriptableObjectType(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "type required.");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                var match = types.FirstOrDefault(t => !t.IsAbstract && typeof(ScriptableObject).IsAssignableFrom(t) && (t.FullName == name || t.Name == name));
                if (match != null) return match;
            }
            throw new McpToolException(McpErrorCodes.NotFound, $"ScriptableObject type not found: '{name}'.");
        }

        internal static JObject SerializeObjectProperties(
            UnityEngine.Object asset,
            bool includeCurveValues = false,
            int maxStringCharacters = 2000)
        {
            var props = new JObject();
            using var so = new SerializedObject(asset);
            var iter = so.GetIterator();
            if (!iter.NextVisible(true)) return props;

            do
            {
                // Skip script reference
                if (iter.name == "m_Script") continue;
                if (iter.propertyType != SerializedPropertyType.Generic)
                    props[iter.propertyPath] = ReadSerializedValue(iter, includeCurveValues, maxStringCharacters);
            }
            while (iter.NextVisible(true));

            return props;
        }

        internal static void AddPropertyPage(JObject result, UnityEngine.Object asset, JObject args, bool includeCurveValues = false)
        {
            var offset = args["offset"]?.Value<int>() ?? 0;
            var limit = args["limit"]?.Value<int>() ?? 25;
            if (offset < 0 || limit is < 1 or > 1000)
                throw new McpToolException(McpErrorCodes.InvalidParams, "offset must be >= 0 and limit must be between 1 and 1000.");

            var maxStringCharacters = args["maxStringCharacters"]?.Value<int>() ?? 2000;
            var search = args["propertySearch"]?.Value<string>();
            var paths = args["propertyPaths"] is JArray pathArray ? pathArray.Values<string>().ToArray() : Array.Empty<string>();
            var page = ReadPropertyPage(asset, paths, search, offset, limit, includeCurveValues, maxStringCharacters, out var count, out var filteredCount);
            result["propertyCount"] = count;
            result["filteredPropertyCount"] = filteredCount;
            result["offset"] = offset;
            result["returned"] = page.Count;
            result["truncated"] = offset + page.Count < filteredCount;
            if (offset + page.Count < filteredCount)
                result["nextOffset"] = offset + page.Count;
            result["properties"] = page;
        }

        internal static JObject ReadPropertyPage(UnityEngine.Object asset, string[] paths, string search,
            int offset, int limit, bool includeCurveValues, int maxStringCharacters, out int count, out int filteredCount)
        {
            count = filteredCount = 0;
            var wanted = new HashSet<string>(paths ?? Array.Empty<string>(), StringComparer.Ordinal);
            var page = new JObject();
            using var serialized = new SerializedObject(asset);
            var property = serialized.GetIterator();
            while (property.NextVisible(true))
            {
                if (property.name == "m_Script" || property.propertyType == SerializedPropertyType.Generic) continue;
                count++;
                var path = property.propertyPath;
                if ((wanted.Count > 0 && !wanted.Contains(path))
                    || (!string.IsNullOrWhiteSpace(search) && path.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                filteredCount++;
                if (filteredCount <= offset || page.Count >= limit) continue;
                page[path] = ReadSerializedValue(property, includeCurveValues, maxStringCharacters);
            }
            return page;
        }

        private static JToken ReadSerializedValue(SerializedProperty sp, bool includeCurveValues, int maxStringCharacters)
        {
            return sp.propertyType switch
            {
                SerializedPropertyType.Integer => sp.intValue,
                SerializedPropertyType.Boolean => sp.boolValue,
                SerializedPropertyType.Float => sp.floatValue,
                SerializedPropertyType.String => BoundedString(sp.stringValue, maxStringCharacters),
                SerializedPropertyType.Enum => sp.enumNames.Length > sp.enumValueIndex && sp.enumValueIndex >= 0
                    ? sp.enumNames[sp.enumValueIndex] : sp.enumValueIndex.ToString(),
                SerializedPropertyType.Color => new JObject
                {
                    ["r"] = sp.colorValue.r, ["g"] = sp.colorValue.g,
                    ["b"] = sp.colorValue.b, ["a"] = sp.colorValue.a,
                },
                SerializedPropertyType.Vector2 => new JObject
                {
                    ["x"] = sp.vector2Value.x, ["y"] = sp.vector2Value.y,
                },
                SerializedPropertyType.Vector3 => new JObject
                {
                    ["x"] = sp.vector3Value.x, ["y"] = sp.vector3Value.y, ["z"] = sp.vector3Value.z,
                },
                SerializedPropertyType.Vector4 => new JObject
                {
                    ["x"] = sp.vector4Value.x, ["y"] = sp.vector4Value.y,
                    ["z"] = sp.vector4Value.z, ["w"] = sp.vector4Value.w,
                },
                SerializedPropertyType.Quaternion => new JObject
                {
                    ["x"] = sp.quaternionValue.x, ["y"] = sp.quaternionValue.y,
                    ["z"] = sp.quaternionValue.z, ["w"] = sp.quaternionValue.w,
                },
                SerializedPropertyType.ObjectReference => sp.objectReferenceValue != null
                    ? UnitySerializer.ToJson(sp.objectReferenceValue) : JValue.CreateNull(),
                SerializedPropertyType.LayerMask => sp.intValue,
                SerializedPropertyType.Bounds => new JObject
                {
                    ["center"] = new JObject { ["x"] = sp.boundsValue.center.x, ["y"] = sp.boundsValue.center.y, ["z"] = sp.boundsValue.center.z },
                    ["size"] = new JObject { ["x"] = sp.boundsValue.size.x, ["y"] = sp.boundsValue.size.y, ["z"] = sp.boundsValue.size.z },
                },
                SerializedPropertyType.ArraySize => sp.intValue,
                SerializedPropertyType.AnimationCurve when includeCurveValues => SerializeAnimationCurve(sp.animationCurveValue),
                SerializedPropertyType.Gradient when includeCurveValues => SerializeGradient(sp.gradientValue),
                _ => $"<{sp.propertyType}>",
            };
        }

        private static JToken BoundedString(string value, int maxCharacters)
        {
            value ??= string.Empty;
            if (value.Length <= maxCharacters) return value;
            return new JObject
            {
                ["$type"] = "truncatedString",
                ["text"] = value.Substring(0, maxCharacters),
                ["characters"] = value.Length,
                ["truncated"] = true,
            };
        }

        private static JObject SerializeAnimationCurve(AnimationCurve curve)
            => new()
            {
                ["preWrapMode"] = curve.preWrapMode.ToString(),
                ["postWrapMode"] = curve.postWrapMode.ToString(),
                ["keys"] = new JArray(curve.keys.Select(key => new JObject
                {
                    ["time"] = key.time,
                    ["value"] = key.value,
                    ["inTangent"] = key.inTangent,
                    ["outTangent"] = key.outTangent,
                    ["inWeight"] = key.inWeight,
                    ["outWeight"] = key.outWeight,
                    ["weightedMode"] = key.weightedMode.ToString(),
                })),
            };

        private static JObject SerializeGradient(Gradient gradient)
            => new()
            {
                ["mode"] = gradient.mode.ToString(),
                ["colorKeys"] = new JArray(gradient.colorKeys.Select(key => new JObject
                {
                    ["color"] = new JObject
                    {
                        ["r"] = key.color.r,
                        ["g"] = key.color.g,
                        ["b"] = key.color.b,
                        ["a"] = key.color.a,
                    },
                    ["time"] = key.time,
                })),
                ["alphaKeys"] = new JArray(gradient.alphaKeys.Select(key => new JObject
                {
                    ["alpha"] = key.alpha,
                    ["time"] = key.time,
                })),
            };

        private static JArray SerializeGameObjectComponents(GameObject go)
        {
            var arr = new JArray();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                var entry = new JObject
                {
                    ["type"] = comp.GetType().FullName,
                    ["name"] = comp.GetType().Name,
                };
                arr.Add(entry);
            }
            return arr;
        }
    }
}
