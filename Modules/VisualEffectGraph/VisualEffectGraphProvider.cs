#if HAS_VFX_GRAPH
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp;
using LittleBrushGames.Mcp.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LittleBrushGames.Mcp.Modules.VisualEffectGraph
{
    [McpToolProvider]
    public sealed class VisualEffectGraphProvider : IToolProvider
    {
        private const int MaxDocumentCharacters = 2 * 1024 * 1024;
        private static readonly VfxApi Api = new();

        public string Namespace => "vfx";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "vfx.catalog",
                Description = "Search the live Visual Effect Graph node/type catalog. Returns descriptors accepted by vfx.graph.write.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""kind"": { ""type"": ""string"", ""enum"": [""all"", ""context"", ""block"", ""operator"", ""parameter"", ""slotType""] },
                        ""detail"": { ""type"": ""string"", ""enum"": [""summary"", ""full""] },
                        ""search"": { ""type"": ""string"" },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200 }
                    }
                }"),
                Handler = Catalog,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });

            reg.Register(new ToolDescriptor
            {
                Name = "vfx.graph.read",
                Description = "Read a .vfx asset as normalized JSON, including contexts, blocks, operators, parameters, settings, values, links, notes, groups, layout, and an optimistic hash.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""required"": [""path""], ""properties"": { ""path"": { ""type"": ""string"" }, ""profile"": { ""type"": ""string"", ""enum"": [""outline"", ""topology"", ""full""] } } }"),
                Handler = Read,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });

            reg.Register(new ToolDescriptor
            {
                Name = "vfx.graph.write",
                Description = "Validate, compile, and transactionally replace a complete .vfx graph from normalized JSON. Existing assets require expectedHash. Supports dryRun; custom HLSL requires allowCustomHlsl=true.",
                Availability = ToolAvailability.EditMode,
                ExclusiveGroup = "vfx-graph-write",
                Timeout = TimeSpan.FromMinutes(3),
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""path"", ""graph""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"" },
                        ""graph"": { ""type"": ""object"" },
                        ""dryRun"": { ""type"": ""boolean"" },
                        ""allowCustomHlsl"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = Write,
            });
        }

        private static ValueTask<ToolResult> Catalog(ToolContext ctx, CancellationToken _)
        {
            Api.EnsureCompatible();
            var kind = ((string)ctx.Arguments["kind"] ?? "all").Trim();
            var detail = ((string)ctx.Arguments["detail"] ?? "summary").Trim();
            var search = ((string)ctx.Arguments["search"] ?? string.Empty).Trim();
            var offset = ctx.Arguments["offset"]?.Value<int>() ?? 0;
            var limit = ctx.Arguments["limit"]?.Value<int>() ?? 25;
            if (offset < 0 || limit is < 1 or > 200)
                throw Validation("offset must be >= 0 and limit must be between 1 and 200.");
            if (detail is not ("summary" or "full")) throw Validation("detail must be summary or full.");
            if (detail == "full" && limit > 25) throw Validation("detail=full supports at most 25 items per response.");

            var items = Api.Catalog(kind, search, detail == "full").ToList();
            var page = new JArray(items.Skip(offset).Take(limit));
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["schemaVersion"] = 1,
                ["unityVersion"] = Application.unityVersion,
                ["packageVersion"] = Api.PackageVersion,
                ["renderPipeline"] = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.GetType().FullName,
                ["kind"] = kind,
                ["detail"] = detail,
                ["search"] = search,
                ["count"] = items.Count,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["truncated"] = offset + page.Count < items.Count,
                ["items"] = page,
            }));
        }

        private static ValueTask<ToolResult> Read(ToolContext ctx, CancellationToken _)
        {
            Api.EnsureCompatible();
            var path = NormalizePath((string)ctx.Arguments["path"]);
            if (!File.Exists(path))
                throw new McpToolException(McpErrorCodes.NotFound, $"VFX Graph not found: '{path}'.");

            var profile = (string)ctx.Arguments["profile"] ?? "outline";
            return new ValueTask<ToolResult>(ToolResult.Ok(ProjectGraphRead(Api.Read(path), profile)));
        }

        internal static JObject ProjectGraphRead(JObject document, string profile)
        {
            if (profile == "full") return document;
            if (profile is not ("outline" or "topology")) throw Validation("profile must be outline, topology, or full.");
            var result = (JObject)document.DeepClone();
            var graph = (JObject)result["graph"];
            if (profile == "topology")
            {
                RemoveFields(graph, "settings", "inputSlots", "value", "nodes", "ui");
            }
            else
            {
                var contexts = graph["contexts"] as JArray ?? new JArray();
                var operators = graph["operators"] as JArray ?? new JArray();
                var parameters = graph["parameters"] as JArray ?? new JArray();
                graph.Replace(new JObject
                {
                    ["contextCount"] = contexts.Count,
                    ["operatorCount"] = operators.Count,
                    ["parameterCount"] = parameters.Count,
                    ["flowEdgeCount"] = (graph["flowEdges"] as JArray)?.Count ?? 0,
                    ["dataEdgeCount"] = (graph["dataEdges"] as JArray)?.Count ?? 0,
                    ["contexts"] = new JArray(contexts.OfType<JObject>().Select(item => ModelSummary(item, true))),
                    ["operators"] = new JArray(operators.OfType<JObject>().Select(item => ModelSummary(item, false))),
                    ["parameters"] = new JArray(parameters.OfType<JObject>().Select(ParameterSummary)),
                });
            }
            result["profile"] = profile;
            return result;
        }

        private static JObject ModelSummary(JObject item, bool includeBlocks)
        {
            var result = new JObject { ["id"] = item["id"]?.DeepClone(), ["catalogId"] = item["catalogId"]?.DeepClone() };
            if (item["label"] != null) result["label"] = item["label"].DeepClone();
            if (includeBlocks) result["blockCount"] = (item["blocks"] as JArray)?.Count ?? 0;
            return result;
        }

        private static JObject ParameterSummary(JObject item)
        {
            var result = new JObject { ["id"] = item["id"]?.DeepClone(), ["type"] = item["type"]?.DeepClone(), ["name"] = item["name"]?.DeepClone() };
            if (item["exposed"]?.Value<bool>() == true) result["exposed"] = true;
            return result;
        }

        private static void RemoveFields(JToken token, params string[] names)
        {
            if (token is JObject obj)
            {
                foreach (var name in names) obj.Remove(name);
                foreach (var child in obj.Properties().Select(property => property.Value).ToArray()) RemoveFields(child, names);
            }
            else if (token is JArray array)
                foreach (var child in array) RemoveFields(child, names);
        }

        private static ValueTask<ToolResult> Write(ToolContext ctx, CancellationToken _)
        {
            Api.EnsureCompatible();
            if (ctx.Arguments.ToString(Formatting.None).Length > MaxDocumentCharacters)
                throw Validation($"Request exceeds the {MaxDocumentCharacters} character limit.");

            var path = NormalizePath((string)ctx.Arguments["path"]);
            var graph = ctx.Arguments["graph"] as JObject ?? throw Validation("graph is required.");
            var dryRun = ctx.Arguments["dryRun"]?.Value<bool>() == true;
            var allowCustomHlsl = ctx.Arguments["allowCustomHlsl"]?.Value<bool>() == true;
            return new ValueTask<ToolResult>(ToolResult.Ok(Api.Write(
                path,
                (string)ctx.Arguments["expectedHash"],
                graph,
                dryRun,
                allowCustomHlsl)));
        }

        internal static string NormalizePath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || Path.IsPathRooted(raw))
                throw Validation("path must be project-relative.");
            var path = raw.Replace('\\', '/').Trim();
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath("Assets") + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
                throw Validation("path must be a .vfx asset under Assets/.");
            return path;
        }

        private static McpToolException Validation(string message)
            => new(McpErrorCodes.ValidationFailed, message);

        private sealed class VfxApi
        {
            private const int MaxModels = 2000;
            private const int MaxEdges = 5000;
            private readonly BindingFlags _all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            private readonly Type _library;
            private readonly Type _resource;
            private readonly Type _resourceExtensions;
            private readonly Type _assetUtility;
            private readonly Type _graph;
            private readonly Type _model;
            private readonly Type _context;
            private readonly Type _block;
            private readonly Type _operator;
            private readonly Type _parameter;
            private readonly Type _slot;
            private readonly Type _subgraphBlock;
            private readonly Type _subgraphOperator;
            private string _compatibilityError;

            public string PackageVersion { get; }

            public VfxApi()
            {
                _library = FindType("UnityEditor.VFX.VFXLibrary");
                _resource = FindType("UnityEditor.VFX.VisualEffectResource");
                _resourceExtensions = FindType("UnityEditor.VFX.VisualEffectResourceExtensions");
                _assetUtility = FindType("UnityEditor.VisualEffectAssetEditorUtility");
                _graph = FindType("UnityEditor.VFX.VFXGraph");
                _model = FindType("UnityEditor.VFX.VFXModel");
                _context = FindType("UnityEditor.VFX.VFXContext");
                _block = FindType("UnityEditor.VFX.VFXBlock");
                _operator = FindType("UnityEditor.VFX.VFXOperator");
                _parameter = FindType("UnityEditor.VFX.VFXParameter");
                _slot = FindType("UnityEditor.VFX.VFXSlot");
                _subgraphBlock = FindType("UnityEditor.VFX.VFXSubgraphBlock");
                _subgraphOperator = FindType("UnityEditor.VFX.VFXSubgraphOperator");

                var required = new[] { _library, _resource, _resourceExtensions, _assetUtility, _graph, _model, _context, _block, _operator, _parameter, _slot };
                if (required.Any(type => type == null))
                {
                    _compatibilityError = "Required Unity VFX Graph editor types were not found.";
                    return;
                }

                PackageVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(_graph.Assembly)?.version;
                try
                {
                    RequireMethod(_library, "GetContexts", true);
                    RequireMethod(_library, "GetBlocks", true);
                    RequireMethod(_library, "GetOperators", true);
                    RequireMethod(_library, "GetParameters", true);
                    RequireMethod(_resource, "GetResourceAtPath", true);
                    RequireMethod(_resourceExtensions, "GetOrCreateGraph", true);
                    RequireMethod(_resourceExtensions, "WriteAssetWithSubAssets", true);
                    RequireMethod(_assetUtility, "CreateNewAsset", true);
                    RequireMethod(_model, "AddChild");
                    RequireMethod(_model, "RemoveAllChildren");
                    RequireMethod(_model, "GetSettings");
                    RequireMethod(_context, "Accept");
                    RequireMethod(_context, "LinkTo");
                    RequireMethod(_slot, "CanLink");
                    RequireMethod(_slot, "Link");
                }
                catch (Exception ex)
                {
                    _compatibilityError = ex.Message;
                }
            }

            public void EnsureCompatible()
            {
                if (_compatibilityError != null)
                    throw new McpToolException(
                        McpErrorCodes.ToolUnavailable,
                        $"Visual Effect Graph {PackageVersion ?? "<unknown>"} is incompatible with this MCP adapter: {_compatibilityError}");
            }

            public IEnumerable<JObject> Catalog(string requestedKind, string search, bool includeDetails)
            {
                var kinds = requestedKind == "all"
                    ? new[] { "context", "block", "operator", "parameter", "slotType" }
                    : new[] { requestedKind };
                if (kinds.Any(kind => kind is not ("context" or "block" or "operator" or "parameter" or "slotType")))
                    throw Validation($"Unknown catalog kind: '{requestedKind}'.");

                foreach (var kind in kinds)
                {
                    if (kind == "slotType")
                    {
                        foreach (var type in ((IEnumerable)InvokeStatic(_library, "GetSlotsType")).Cast<Type>()
                                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
                        {
                            var item = new JObject { ["kind"] = kind, ["type"] = type.FullName };
                            if (Matches(item, search)) yield return item;
                        }
                        continue;
                    }

                    foreach (var descriptor in Descriptors(kind)
                                 .OrderBy(DescriptorId, StringComparer.Ordinal))
                    {
                        var basic = new JObject
                        {
                            ["kind"] = kind,
                            ["id"] = DescriptorId(descriptor),
                            ["name"] = Get<string>(descriptor, "name"),
                            ["category"] = Get<string>(descriptor, "category"),
                            ["modelType"] = Get<Type>(descriptor, "modelType")?.FullName,
                            ["synonyms"] = new JArray(Get<IEnumerable>(descriptor, "synonyms")?.Cast<object>().Select(value => value?.ToString()) ?? Array.Empty<string>()),
                        };
                        if (!Matches(basic, search)) continue;

                        if (!includeDetails)
                        {
                            RemoveEmptyCatalogFields(basic);
                            yield return basic;
                            continue;
                        }

                        var model = Invoke(descriptor, "CreateInstance") as Object;
                        try
                        {
                            basic["customHlsl"] = IsCustomHlsl(model);
                            basic["settings"] = SerializeSettings(model, includeDefaults: true);
                            if (Has(model, "inputSlots"))
                            {
                                basic["inputSlots"] = SerializeSlotSchema(GetEnumerable(model, "inputSlots"));
                                basic["outputSlots"] = SerializeSlotSchema(GetEnumerable(model, "outputSlots"));
                            }
                            if (_context.IsInstanceOfType(model))
                            {
                                basic["contextType"] = Get(model, "contextType")?.ToString();
                                basic["inputData"] = Get(model, "inputType")?.ToString();
                                basic["outputData"] = Get(model, "outputType")?.ToString();
                            }
                            else if (_block.IsInstanceOfType(model))
                            {
                                basic["compatibleContexts"] = Get(model, "compatibleContexts")?.ToString();
                                basic["compatibleData"] = Get(model, "compatibleData")?.ToString();
                            }
                            yield return basic;
                        }
                        finally
                        {
                            if (model != null) Object.DestroyImmediate(model);
                        }
                    }

                    foreach (var item in SubgraphCatalog(kind))
                    {
                        if (!Matches(item, search)) continue;
                        if (includeDetails) yield return item;
                        else
                        {
                            var summary = new JObject
                            {
                                ["kind"] = item["kind"]?.DeepClone(),
                                ["id"] = item["id"]?.DeepClone(),
                                ["name"] = item["name"]?.DeepClone(),
                                ["category"] = item["category"]?.DeepClone(),
                                ["modelType"] = item["modelType"]?.DeepClone(),
                            };
                            RemoveEmptyCatalogFields(summary);
                            yield return summary;
                        }
                    }
                }
            }

            private static void RemoveEmptyCatalogFields(JObject item)
            {
                foreach (var property in item.Properties().ToArray())
                    if (property.Value.Type == JTokenType.Null || property.Value is JArray array && array.Count == 0)
                        property.Remove();
            }

            public JObject Read(string path)
            {
                var resource = ResourceAt(path) ?? throw new McpToolException(McpErrorCodes.NotFound, $"VFX resource not found: '{path}'.");
                var graph = InvokeStatic(_resourceExtensions, "GetOrCreateGraph", resource);
                var roots = Children(graph).ToList();
                var ids = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
                var contexts = roots.Where(_context.IsInstanceOfType).ToList();
                var operators = roots.Where(_operator.IsInstanceOfType).ToList();
                var parameters = roots.Where(_parameter.IsInstanceOfType).ToList();

                for (var i = 0; i < contexts.Count; i++) ids[contexts[i]] = $"context{i}";
                for (var i = 0; i < operators.Count; i++) ids[operators[i]] = $"operator{i}";
                for (var i = 0; i < parameters.Count; i++) ids[parameters[i]] = $"parameter{i}";

                var contextJson = new JArray();
                foreach (var context in contexts)
                {
                    var item = SerializeModel(context, ids[context]);
                    item["label"] = Get<string>(context, "label");
                    var blocks = new JArray();
                    var index = 0;
                    foreach (var block in Children(context).Where(_block.IsInstanceOfType))
                    {
                        var id = $"{ids[context]}.block{index++}";
                        ids[block] = id;
                        blocks.Add(SerializeModel(block, id));
                    }
                    item["blocks"] = blocks;
                    contextJson.Add(item);
                }

                var operatorJson = new JArray(operators.Select(model => SerializeModel(model, ids[model])));
                var parameterJson = new JArray(parameters.Select(model => SerializeParameter(model, ids[model])));
                var allModels = ids.Keys.ToList();

                return new JObject
                {
                    ["schemaVersion"] = 1,
                    ["unityVersion"] = Application.unityVersion,
                    ["packageVersion"] = PackageVersion,
                    ["path"] = path,
                    ["guid"] = AssetDatabase.AssetPathToGUID(path),
                    ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                    ["graph"] = new JObject
                    {
                        ["schemaVersion"] = 1,
                        ["packageVersion"] = PackageVersion,
                        ["settings"] = SerializeSettings(graph),
                        ["contexts"] = contextJson,
                        ["operators"] = operatorJson,
                        ["parameters"] = parameterJson,
                        ["flowEdges"] = SerializeFlowEdges(contexts, ids),
                        ["dataEdges"] = SerializeDataEdges(allModels, ids),
                        ["ui"] = SerializeUi(graph, ids),
                    },
                };
            }

            public JObject Write(string path, string expectedHash, JObject document, bool dryRun, bool allowCustomHlsl)
            {
                if (document["schemaVersion"]?.Value<int>() is not (null or 1))
                    throw Validation("Only graph schemaVersion 1 is supported.");
                if (document["packageVersion"] is JValue packageToken &&
                    packageToken.Type == JTokenType.String &&
                    !string.Equals((string)packageToken, PackageVersion, StringComparison.Ordinal))
                    throw Validation($"Graph packageVersion '{packageToken}' does not match installed Visual Effect Graph '{PackageVersion}'.");

                var exists = File.Exists(path);
                var currentHash = exists ? AssetDatabase.GetAssetDependencyHash(path).ToString() : null;
                if (exists && string.IsNullOrEmpty(expectedHash))
                    throw Validation("expectedHash is required when replacing an existing VFX Graph.");
                if (exists && !string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
                    throw new McpToolException(
                        McpErrorCodes.Conflict,
                        $"VFX Graph changed since read: '{path}'.",
                        new JObject { ["expectedHash"] = expectedHash, ["currentHash"] = currentHash });
                if (!exists && !string.IsNullOrEmpty(expectedHash))
                    throw new McpToolException(McpErrorCodes.Conflict, $"VFX Graph does not exist but expectedHash was supplied: '{path}'.");

                var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent))
                    throw new McpToolException(McpErrorCodes.NotFound, $"Parent folder not found: '{parent}'.");

                ValidateSize(document);
                var tempPath = $"{parent}/__McpVfxTemp_{Guid.NewGuid():N}.vfx";
                byte[] backup = null;
                try
                {
                    var tempAsset = InvokeStatic(_assetUtility, "CreateNewAsset", tempPath) as Object;
                    if (tempAsset == null)
                        throw new McpToolException(McpErrorCodes.ToolError, "Unity failed to create a temporary VFX Graph.");

                    var resource = ResourceAt(tempPath) ?? throw new McpToolException(McpErrorCodes.ToolError, "Temporary VFX resource was not created.");
                    var graph = InvokeStatic(_resourceExtensions, "GetOrCreateGraph", resource);
                    Build(graph, document, allowCustomHlsl);
                    var compileOutput = Invoke(graph, "RecompileIfNeeded", false, true);
                    if (compileOutput == null || Get<bool>(compileOutput, "success") != true)
                        throw new McpToolException(McpErrorCodes.ValidationFailed, "Unity could not compile the proposed VFX Graph.");
                    InvokeStatic(_resourceExtensions, "WriteAssetWithSubAssets", resource);
                    AssetDatabase.ImportAsset(tempPath, ImportAssetOptions.ForceUpdate);

                    if (dryRun)
                    {
                        return new JObject
                        {
                            ["path"] = path,
                            ["dryRun"] = true,
                            ["compiled"] = true,
                            ["wouldCreate"] = !exists,
                            ["currentHash"] = currentHash,
                            ["summary"] = Summarize(document),
                        };
                    }

                    if (exists) backup = File.ReadAllBytes(path);
                    File.Copy(tempPath, path, overwrite: true);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    if (ResourceAt(path) == null || AssetDatabase.LoadMainAssetAtPath(path) == null)
                        throw new InvalidOperationException("Unity did not import the replaced VFX Graph.");

                    return new JObject
                    {
                        ["path"] = path,
                        ["guid"] = AssetDatabase.AssetPathToGUID(path),
                        ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                        ["dryRun"] = false,
                        ["compiled"] = true,
                        ["created"] = !exists,
                        ["summary"] = Summarize(document),
                    };
                }
                catch
                {
                    if (!dryRun)
                    {
                        if (backup != null)
                        {
                            File.WriteAllBytes(path, backup);
                            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        }
                        else if (!exists && File.Exists(path))
                        {
                            AssetDatabase.DeleteAsset(path);
                        }
                    }
                    throw;
                }
                finally
                {
                    AssetDatabase.DeleteAsset(tempPath);
                }
            }

            private void Build(object graph, JObject document, bool allowCustomHlsl)
            {
                Invoke(graph, "RemoveAllChildren", true);
                ApplySettings(graph, document["settings"] as JObject);
                var models = new Dictionary<string, object>(StringComparer.Ordinal);
                var modelCount = 0;

                foreach (var item in Objects(document["contexts"], "contexts"))
                {
                    var context = CreateDescriptorModel("context", item, allowCustomHlsl);
                    AddUnique(models, item, context);
                    modelCount++;
                    ApplyModel(context, item);
                    Invoke(graph, "AddChild", context, -1, true);

                    var blockIndex = 0;
                    foreach (var blockItem in Objects(item["blocks"], $"contexts[{(string)item["id"]}].blocks"))
                    {
                        var block = CreateDescriptorModel("block", blockItem, allowCustomHlsl);
                        AddUnique(models, blockItem, block);
                        modelCount++;
                        if (!Get<bool>(Invoke(context, "Accept", block, blockIndex), null))
                            throw Validation($"Block '{(string)blockItem["id"]}' is incompatible with context '{(string)item["id"]}'.");
                        Invoke(context, "AddChild", block, blockIndex++, true);
                        ApplyModel(block, blockItem);
                    }
                }

                foreach (var item in Objects(document["operators"], "operators"))
                {
                    var model = CreateDescriptorModel("operator", item, allowCustomHlsl);
                    AddUnique(models, item, model);
                    modelCount++;
                    ApplyModel(model, item);
                    Invoke(graph, "AddChild", model, -1, true);
                }

                foreach (var item in Objects(document["parameters"], "parameters"))
                {
                    var typeName = RequiredString(item, "type");
                    var descriptor = Descriptors("parameter").FirstOrDefault(candidate =>
                        string.Equals(Get<Type>(candidate, "modelType")?.FullName, typeName, StringComparison.Ordinal));
                    if (descriptor == null) throw Validation($"Unknown VFX parameter type: '{typeName}'.");
                    var parameter = Invoke(descriptor, "CreateInstance");
                    AddUnique(models, item, parameter);
                    modelCount++;
                    Invoke(graph, "AddChild", parameter, -1, true);
                    ApplyParameter(parameter, item);
                }

                if (modelCount > MaxModels)
                    throw Validation($"Graph contains {modelCount} models; maximum is {MaxModels}.");

                LinkFlow(document["flowEdges"], models);
                LinkData(document["dataEdges"], models);

                foreach (var item in Objects(document["parameters"], "parameters"))
                {
                    var parameter = models[(string)item["id"]];
                    var nodes = item["nodes"] as JArray;
                    if (nodes is { Count: > 0 })
                        SetParameterNodes(parameter, nodes);
                    else
                        Invoke(parameter, "CreateDefaultNode", ReadVector2(item["position"] as JObject));
                }

                ApplyUi(graph, document["ui"] as JObject, models);
            }

            private object CreateDescriptorModel(string kind, JObject item, bool allowCustomHlsl)
            {
                var id = RequiredString(item, "catalogId");
                object model;
                if (id.StartsWith("Subgraph/", StringComparison.Ordinal))
                {
                    var parts = id.Split('/');
                    var expectedLabel = kind == "block" ? "Block" : kind == "operator" ? "Operator" : null;
                    var modelType = kind == "block" ? _subgraphBlock : kind == "operator" ? _subgraphOperator : null;
                    if (parts.Length != 3 || expectedLabel == null || parts[1] != expectedLabel || modelType == null)
                        throw Validation($"Invalid {kind} subgraph catalogId: '{id}'.");
                    var path = AssetDatabase.GUIDToAssetPath(parts[2]);
                    var asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset == null) throw new McpToolException(McpErrorCodes.NotFound, $"VFX subgraph asset not found for GUID '{parts[2]}'.");
                    var fieldType = Field(modelType, "m_Subgraph")?.FieldType;
                    if (fieldType == null || !fieldType.IsInstanceOfType(asset))
                        throw Validation($"Asset '{path}' is not the required {expectedLabel.ToLowerInvariant()} VFX subgraph type.");
                    model = ScriptableObject.CreateInstance(modelType);
                    Invoke(model, "SetSettingValue", "m_Subgraph", asset);
                }
                else if (id.StartsWith("type:", StringComparison.Ordinal))
                {
                    var expectedBase = kind switch
                    {
                        "context" => _context,
                        "block" => _block,
                        "operator" => _operator,
                        _ => null,
                    };
                    var type = _graph.Assembly.GetType(id.Substring("type:".Length), false);
                    if (type == null || expectedBase == null || !expectedBase.IsAssignableFrom(type) || type.IsAbstract)
                        throw Validation($"Unsafe or unknown VFX model type: '{id}'.");
                    model = ScriptableObject.CreateInstance(type);
                }
                else
                {
                    var descriptor = Descriptors(kind).FirstOrDefault(candidate => string.Equals(DescriptorId(candidate), id, StringComparison.Ordinal));
                    if (descriptor == null) throw Validation($"Unknown {kind} catalogId: '{id}'. Query vfx.catalog for live IDs.");
                    model = Invoke(descriptor, "CreateInstance");
                }
                if (IsCustomHlsl(model) && !allowCustomHlsl)
                    throw Validation($"'{id}' contains custom HLSL. Pass allowCustomHlsl=true to authorize shader code.");
                return model;
            }

            private void ApplyModel(object model, JObject item)
            {
                SetIfPresent(model, "position", item["position"], token => ReadVector2(token as JObject));
                SetIfPresent(model, "collapsed", item["collapsed"], token => token.Value<bool>());
                SetIfPresent(model, "superCollapsed", item["superCollapsed"], token => token.Value<bool>());
                if (_context.IsInstanceOfType(model) && item["label"] != null)
                    Set(model, "label", (string)item["label"]);
                ApplySettings(model, item["settings"] as JObject);
                ApplyOperatorSlotTypes(model, item);
                ApplySlots(model, item);
            }

            private void ApplyOperatorSlotTypes(object model, JObject item)
            {
                if (!_operator.IsInstanceOfType(model) || item["inputSlots"] is not JArray slots) return;
                var roots = slots.OfType<JObject>()
                    .Where(slot => !RequiredString(slot, "path").Contains('.'))
                    .ToList();
                if (roots.Count == 0) return;

                var indexed = model.GetType().GetMethods(_all).FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return method.Name == "SetOperandType" && parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(Type);
                });
                if (indexed != null)
                {
                    var count = Math.Min(Get<int>(model, "operandCount"), roots.Count);
                    for (var i = 0; i < count; i++)
                        indexed.Invoke(model, new object[] { i, RequiredSlotType(roots[i]) });
                    Invoke(model, "ResyncSlots", false);
                    return;
                }

                var uniform = model.GetType().GetMethods(_all).FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return method.Name == "SetOperandType" && parameters.Length == 1 && parameters[0].ParameterType == typeof(Type);
                });
                if (uniform != null)
                {
                    uniform.Invoke(model, new object[] { RequiredSlotType(roots[0]) });
                    Invoke(model, "ResyncSlots", false);
                }
            }

            private Type RequiredSlotType(JObject slot)
            {
                var name = RequiredString(slot, "type");
                return ResolveAllowedType(name) ?? throw Validation($"Unknown VFX slot type: '{name}'.");
            }

            private void ApplyParameter(object parameter, JObject item)
            {
                ApplyModel(parameter, item);
                if (item["name"] != null)
                    Invoke(parameter, "SetSettingValue", "m_ExposedName", (string)item["name"]);
                if (item["exposed"] != null)
                    Invoke(parameter, "SetSettingValue", "m_Exposed", item["exposed"].Value<bool>());
                SetIfPresent(parameter, "order", item["order"], token => token.Value<int>());
                SetIfPresent(parameter, "category", item["category"], token => (string)token);
                if (item["value"] != null)
                {
                    var type = Get<Type>(parameter, "type");
                    Set(parameter, "value", VfxValueCodec.Read(item["value"], type, ResolveAllowedType));
                }
            }

            private void ApplySettings(object model, JObject settings)
            {
                if (settings == null) return;
                var available = Settings(model).ToDictionary(setting => Get<string>(setting, "name"), StringComparer.Ordinal);
                foreach (var pair in settings)
                {
                    if (!available.TryGetValue(pair.Key, out var setting))
                        throw Validation($"Setting '{pair.Key}' does not exist on {model.GetType().FullName}.");
                    var field = Get<FieldInfo>(setting, "field");
                    var value = VfxValueCodec.Read(pair.Value, field.FieldType, ResolveAllowedType);
                    Invoke(model, "SetSettingValue", pair.Key, value);
                }
            }

            private void ApplySlots(object model, JObject item)
            {
                var values = new List<(string Path, JToken Value, string Space)>();
                if (item["slots"] is JObject slotsObject)
                {
                    values.AddRange(slotsObject.Properties().Select(pair => (pair.Name, pair.Value, (string)null)));
                }
                foreach (var token in (item["inputSlots"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (token["value"] != null)
                        values.Add((RequiredString(token, "path"), token["value"], (string)token["space"]));
                }

                if (values.Count == 0) return;
                var slots = FlattenSlots(GetEnumerable(model, "inputSlots")).ToDictionary(pair => pair.Path, pair => pair.Slot, StringComparer.Ordinal);
                foreach (var value in values)
                {
                    if (!slots.TryGetValue(value.Path, out var slot))
                        throw Validation($"Input slot '{value.Path}' does not exist on {model.GetType().FullName}.");
                    var current = Get(slot, "value");
                    var targetType = current?.GetType() ?? SlotType(slot);
                    try
                    {
                        Set(slot, "value", VfxValueCodec.Read(value.Value, targetType, ResolveAllowedType));
                    }
                    catch (McpToolException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw Validation($"Input slot '{value.Path}' on {model.GetType().FullName} could not decode as {targetType.FullName}: {ex.Message}");
                    }
                    if (!string.IsNullOrEmpty(value.Space) && Has(slot, "space"))
                    {
                        var spaceType = Property(slot, "space").PropertyType;
                        Set(slot, "space", Enum.Parse(spaceType, value.Space, ignoreCase: true));
                    }
                }
            }

            private void LinkFlow(JToken token, IReadOnlyDictionary<string, object> models)
            {
                var edges = Objects(token, "flowEdges").ToList();
                if (edges.Count > MaxEdges) throw Validation($"flowEdges exceeds the {MaxEdges} edge limit.");
                foreach (var edge in edges)
                {
                    var from = Endpoint(edge, "from", models, _context);
                    var to = Endpoint(edge, "to", models, _context);
                    var fromSlot = (edge["from"]?["slot"]?.Value<int>()).GetValueOrDefault();
                    var toSlot = (edge["to"]?["slot"]?.Value<int>()).GetValueOrDefault();
                    var canLink = (bool)InvokeStatic(_context, "CanLink", from, to, fromSlot, toSlot);
                    if (!canLink) throw Validation($"Invalid context flow edge '{Get<string>(from, "label")}' -> '{Get<string>(to, "label")}'.");
                    Invoke(from, "LinkTo", to, fromSlot, toSlot);
                }
            }

            private void LinkData(JToken token, IReadOnlyDictionary<string, object> models)
            {
                var edges = Objects(token, "dataEdges").ToList();
                if (edges.Count > MaxEdges) throw Validation($"dataEdges exceeds the {MaxEdges} edge limit.");
                foreach (var edge in edges)
                {
                    var fromEndpoint = edge["from"] as JObject ?? throw Validation("dataEdges[].from is required.");
                    var toEndpoint = edge["to"] as JObject ?? throw Validation("dataEdges[].to is required.");
                    var fromModel = Endpoint(edge, "from", models, null);
                    var toModel = Endpoint(edge, "to", models, null);
                    var from = ResolveSlot(fromModel, "outputSlots", RequiredString(fromEndpoint, "slot"));
                    var to = ResolveSlot(toModel, "inputSlots", RequiredString(toEndpoint, "slot"));
                    if (!(bool)Invoke(from, "CanLink", to) || !(bool)Invoke(to, "CanLink", from))
                        throw Validation($"Incompatible data edge '{(string)fromEndpoint["node"]}:{(string)fromEndpoint["slot"]}' -> '{(string)toEndpoint["node"]}:{(string)toEndpoint["slot"]}'.");
                    if (!(bool)Invoke(from, "Link", to, true))
                        throw Validation("Unity rejected a validated data edge.");
                }
            }

            private JObject SerializeModel(object model, string id)
            {
                return new JObject
                {
                    ["id"] = id,
                    ["catalogId"] = FindDescriptorId(model),
                    ["modelType"] = model.GetType().FullName,
                    ["position"] = VfxValueCodec.Write(Get(model, "position")),
                    ["collapsed"] = Has(model, "collapsed") ? Get<bool>(model, "collapsed") : false,
                    ["superCollapsed"] = Has(model, "superCollapsed") ? Get<bool>(model, "superCollapsed") : false,
                    ["settings"] = SerializeSettings(model),
                    ["inputSlots"] = SerializeSlots(GetEnumerable(model, "inputSlots")),
                };
            }

            private JObject SerializeParameter(object parameter, string id)
            {
                var item = SerializeModel(parameter, id);
                item.Remove("catalogId");
                item["type"] = Get<Type>(parameter, "type")?.FullName;
                item["name"] = Get<string>(parameter, "exposedName");
                item["exposed"] = Get<bool>(parameter, "exposed");
                item["order"] = Get<int>(parameter, "order");
                item["category"] = Get<string>(parameter, "category");
                item["value"] = VfxValueCodec.Write(Get(parameter, "value"));
                item["nodes"] = new JArray(GetEnumerable(parameter, "nodes").Cast<object>().Select(node =>
                    new JObject
                    {
                        ["id"] = Get<int>(node, "id"),
                        ["position"] = VfxValueCodec.Write(Get(node, "position")),
                        ["expanded"] = Get<bool>(node, "expanded"),
                        ["superCollapsed"] = Get<bool>(node, "supecollapsed"),
                    }));
                return item;
            }

            private void SetParameterNodes(object parameter, JArray json)
            {
                var nodeType = _parameter.GetNestedType("Node", _all)
                    ?? throw new MissingMemberException(_parameter.FullName, "Node");
                var nodes = Array.CreateInstance(nodeType, json.Count);
                var usedIds = new HashSet<int>();
                for (var i = 0; i < json.Count; i++)
                {
                    if (json[i] is not JObject item) throw Validation("parameters[].nodes must contain objects.");
                    var id = item["id"]?.Value<int>() ?? i;
                    if (!usedIds.Add(id)) throw Validation($"Duplicate parameter node id: {id}.");
                    var node = Activator.CreateInstance(nodeType, _all, null, new object[] { id }, CultureInfo.InvariantCulture);
                    SetField(node, "position", ReadVector2(item["position"] as JObject));
                    SetField(node, "expanded", item["expanded"]?.Value<bool>() ?? true);
                    SetField(node, "supecollapsed", item["superCollapsed"]?.Value<bool>() ?? false);
                    nodes.SetValue(node, i);
                }
                Invoke(parameter, "SetNodes", nodes);
            }

            private JObject SerializeSettings(object model, bool includeDefaults = false)
            {
                var result = new JObject();
                foreach (var setting in Settings(model))
                {
                    var name = Get<string>(setting, "name");
                    result[name] = VfxValueCodec.Write(Get(setting, "value"));
                    if (includeDefaults)
                    {
                        var field = Get<FieldInfo>(setting, "field");
                        result[name] = new JObject
                        {
                            ["type"] = field.FieldType.FullName,
                            ["default"] = result[name],
                            ["allowedValues"] = field.FieldType.IsEnum ? new JArray(Enum.GetNames(field.FieldType)) : null,
                        };
                    }
                }
                return result;
            }

            private JArray SerializeSlotSchema(IEnumerable slots)
            {
                return new JArray(FlattenSlots(slots).Select(pair => new JObject
                {
                    ["path"] = pair.Path,
                    ["type"] = SlotType(pair.Slot)?.FullName,
                    ["default"] = VfxValueCodec.Write(Get(pair.Slot, "value")),
                }));
            }

            private JArray SerializeSlots(IEnumerable slots)
            {
                return new JArray(FlattenSlots(slots).Select(pair => new JObject
                {
                    ["path"] = pair.Path,
                    ["type"] = SlotType(pair.Slot)?.FullName,
                    ["value"] = VfxValueCodec.Write(Get(pair.Slot, "value")),
                    ["space"] = Has(pair.Slot, "spaceable") && Get<bool>(pair.Slot, "spaceable")
                        ? Get(pair.Slot, "space")?.ToString()
                        : null,
                }));
            }

            private JArray SerializeFlowEdges(IEnumerable<object> contexts, IReadOnlyDictionary<object, string> ids)
            {
                var result = new JArray();
                foreach (var context in contexts)
                {
                    var slots = Field(context.GetType(), "m_OutputFlowSlot")?.GetValue(context) as IEnumerable;
                    if (slots == null) continue;
                    var fromIndex = 0;
                    foreach (var slot in slots)
                    {
                        foreach (var link in GetEnumerable(slot, "link"))
                        {
                            var target = Get(link, "context");
                            if (target != null && ids.TryGetValue(target, out var targetId))
                            {
                                result.Add(new JObject
                                {
                                    ["from"] = new JObject { ["node"] = ids[context], ["slot"] = fromIndex },
                                    ["to"] = new JObject { ["node"] = targetId, ["slot"] = Get<int>(link, "slotIndex") },
                                });
                            }
                        }
                        fromIndex++;
                    }
                }
                return result;
            }

            private JArray SerializeDataEdges(IEnumerable<object> models, IReadOnlyDictionary<object, string> ids)
            {
                var result = new JArray();
                var endpoints = new Dictionary<object, (string Node, string Path, bool Output)>(ReferenceEqualityComparer.Instance);
                foreach (var model in models)
                {
                    if (!Has(model, "inputSlots")) continue;
                    foreach (var pair in FlattenSlots(GetEnumerable(model, "inputSlots"))) endpoints[pair.Slot] = (ids[model], pair.Path, false);
                    foreach (var pair in FlattenSlots(GetEnumerable(model, "outputSlots"))) endpoints[pair.Slot] = (ids[model], pair.Path, true);
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in endpoints.Where(pair => pair.Value.Output))
                {
                    foreach (var linked in GetEnumerable(pair.Key, "LinkedSlots"))
                    {
                        if (!endpoints.TryGetValue(linked, out var to) || to.Output) continue;
                        var key = $"{pair.Value.Node}:{pair.Value.Path}>{to.Node}:{to.Path}";
                        if (!seen.Add(key)) continue;
                        result.Add(new JObject
                        {
                            ["from"] = new JObject { ["node"] = pair.Value.Node, ["slot"] = pair.Value.Path },
                            ["to"] = new JObject { ["node"] = to.Node, ["slot"] = to.Path },
                        });
                    }
                }
                return result;
            }

            private JObject SerializeUi(object graph, IReadOnlyDictionary<object, string> ids)
            {
                var ui = Get(graph, "UIInfos");
                var notes = new JArray();
                foreach (var note in GetEnumerable(ui, "stickyNoteInfos"))
                {
                    notes.Add(new JObject
                    {
                        ["title"] = Get<string>(note, "title"),
                        ["contents"] = Get<string>(note, "contents"),
                        ["position"] = VfxValueCodec.Write(Get(note, "position")),
                        ["theme"] = Get<string>(note, "theme"),
                        ["textSize"] = Get<string>(note, "textSize"),
                        ["colorTheme"] = Get<int>(note, "colorTheme"),
                    });
                }

                var groups = new JArray();
                foreach (var group in GetEnumerable(ui, "groupInfos"))
                {
                    var contents = new JArray();
                    foreach (var content in GetEnumerable(group, "contents"))
                    {
                        if (Get<bool>(content, "isStickyNote"))
                            contents.Add(new JObject { ["note"] = Get<int>(content, "id") });
                        else
                        {
                            var model = Get(content, "model");
                            if (model != null && ids.TryGetValue(model, out var id))
                                contents.Add(new JObject { ["node"] = id, ["nodeId"] = Get<int>(content, "id") });
                        }
                    }
                    groups.Add(new JObject
                    {
                        ["title"] = Get<string>(group, "title"),
                        ["position"] = VfxValueCodec.Write(Get(group, "position")),
                        ["contents"] = contents,
                    });
                }
                return new JObject { ["notes"] = notes, ["groups"] = groups };
            }

            private void ApplyUi(object graph, JObject json, IReadOnlyDictionary<string, object> models)
            {
                if (json == null) return;
                var ui = Get(graph, "UIInfos");
                var uiType = ui.GetType();
                var noteType = uiType.GetNestedType("StickyNoteInfo", _all);
                var groupType = uiType.GetNestedType("GroupInfo", _all);
                var nodeIdType = FindType("UnityEditor.VFX.VFXNodeID");

                var notesJson = Objects(json["notes"], "ui.notes").ToList();
                var notes = Array.CreateInstance(noteType, notesJson.Count);
                for (var i = 0; i < notesJson.Count; i++)
                {
                    var note = Activator.CreateInstance(noteType);
                    SetField(note, "title", (string)notesJson[i]["title"]);
                    SetField(note, "contents", (string)notesJson[i]["contents"]);
                    SetField(note, "position", ReadRect(notesJson[i]["position"] as JObject));
                    SetField(note, "theme", (string)notesJson[i]["theme"]);
                    SetField(note, "textSize", (string)notesJson[i]["textSize"]);
                    SetField(note, "colorTheme", notesJson[i]["colorTheme"]?.Value<int>() ?? 0);
                    notes.SetValue(note, i);
                }
                Set(ui, "stickyNoteInfos", notes);

                var groupsJson = Objects(json["groups"], "ui.groups").ToList();
                var groups = Array.CreateInstance(groupType, groupsJson.Count);
                for (var i = 0; i < groupsJson.Count; i++)
                {
                    var group = Activator.CreateInstance(groupType);
                    SetField(group, "title", (string)groupsJson[i]["title"]);
                    SetField(group, "position", ReadRect(groupsJson[i]["position"] as JObject));
                    var contentJson = Objects(groupsJson[i]["contents"], $"ui.groups[{i}].contents").ToList();
                    var contents = Array.CreateInstance(nodeIdType, contentJson.Count);
                    for (var j = 0; j < contentJson.Count; j++)
                    {
                        object nodeId;
                        if (contentJson[j]["note"] != null)
                        {
                            var noteIndex = contentJson[j]["note"].Value<int>();
                            if (noteIndex < 0 || noteIndex >= notesJson.Count) throw Validation($"Group note index {noteIndex} is out of range.");
                            nodeId = Activator.CreateInstance(nodeIdType, _all, null, new object[] { noteIndex }, CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            var id = RequiredString(contentJson[j], "node");
                            if (!models.TryGetValue(id, out var model)) throw Validation($"Group references unknown node '{id}'.");
                            var referencedNodeId = contentJson[j]["nodeId"]?.Value<int>() ?? 0;
                            if (_parameter.IsInstanceOfType(model) &&
                                !GetEnumerable(model, "nodes").Cast<object>().Any(node => Get<int>(node, "id") == referencedNodeId))
                                throw Validation($"Group references missing parameter node id {referencedNodeId} on '{id}'.");
                            nodeId = Activator.CreateInstance(nodeIdType, _all, null, new object[] { model, referencedNodeId }, CultureInfo.InvariantCulture);
                        }
                        contents.SetValue(nodeId, j);
                    }
                    SetField(group, "contents", contents);
                    groups.SetValue(group, i);
                }
                Set(ui, "groupInfos", groups);
            }

            private string FindDescriptorId(object model)
            {
                if ((_subgraphBlock != null && _subgraphBlock.IsInstanceOfType(model)) ||
                    (_subgraphOperator != null && _subgraphOperator.IsInstanceOfType(model)))
                {
                    var asset = Get(model, "subgraph") as Object;
                    var path = asset == null ? null : AssetDatabase.GetAssetPath(asset);
                    var guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
                    if (!string.IsNullOrEmpty(guid))
                        return $"Subgraph/{(_subgraphBlock.IsInstanceOfType(model) ? "Block" : "Operator")}/{guid}";
                }

                var kind = _context.IsInstanceOfType(model) ? "context" : _block.IsInstanceOfType(model) ? "block" : "operator";
                object best = null;
                var bestSpecificity = -1;
                foreach (var descriptor in Descriptors(kind).Where(candidate => Get<Type>(candidate, "modelType") == model.GetType()))
                {
                    var settings = Get(Get(descriptor, "variant"), "settings") as IEnumerable;
                    var matches = true;
                    var specificity = 0;
                    if (settings != null)
                    {
                        foreach (var pair in settings)
                        {
                            specificity++;
                            var name = Get<string>(pair, "Key");
                            var expected = Get(pair, "Value");
                            var actual = Invoke(model, "GetSettingValue", name);
                            if (!Equals(expected, actual)) { matches = false; break; }
                        }
                    }
                    if (matches && specificity > bestSpecificity)
                    {
                        best = descriptor;
                        bestSpecificity = specificity;
                    }
                }
                return best == null ? $"type:{model.GetType().FullName}" : DescriptorId(best);
            }

            private IEnumerable<JObject> SubgraphCatalog(string kind)
            {
                var modelType = kind == "block" ? _subgraphBlock : kind == "operator" ? _subgraphOperator : null;
                var assetType = kind == "block" ? "VisualEffectSubgraphBlock" : kind == "operator" ? "VisualEffectSubgraphOperator" : null;
                if (modelType == null || assetType == null) yield break;

                foreach (var guid in AssetDatabase.FindAssets($"t:{assetType}").OrderBy(value => value, StringComparer.Ordinal))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset == null) continue;
                    var model = ScriptableObject.CreateInstance(modelType);
                    try
                    {
                        Invoke(model, "SetSettingValue", "m_Subgraph", asset);
                        yield return new JObject
                        {
                            ["kind"] = kind,
                            ["id"] = $"Subgraph/{(kind == "block" ? "Block" : "Operator")}/{guid}",
                            ["name"] = asset.name,
                            ["category"] = "Subgraph",
                            ["modelType"] = modelType.FullName,
                            ["asset"] = VfxValueCodec.Write(asset),
                            ["customHlsl"] = false,
                            ["settings"] = SerializeSettings(model, includeDefaults: true),
                            ["inputSlots"] = SerializeSlotSchema(GetEnumerable(model, "inputSlots")),
                            ["outputSlots"] = SerializeSlotSchema(GetEnumerable(model, "outputSlots")),
                        };
                    }
                    finally
                    {
                        Object.DestroyImmediate(model);
                    }
                }
            }

            private IEnumerable<object> Descriptors(string kind)
            {
                var method = kind switch
                {
                    "context" => "GetContexts",
                    "block" => "GetBlocks",
                    "operator" => "GetOperators",
                    "parameter" => "GetParameters",
                    _ => throw Validation($"Unknown descriptor kind '{kind}'."),
                };
                foreach (var root in ((IEnumerable)InvokeStatic(_library, method)).Cast<object>())
                foreach (var item in FlattenDescriptor(root))
                    yield return item;
            }

            private IEnumerable<object> FlattenDescriptor(object descriptor)
            {
                yield return descriptor;
                foreach (var child in GetEnumerable(descriptor, "subVariantDescriptors"))
                foreach (var descendant in FlattenDescriptor(child))
                    yield return descendant;
            }

            private string DescriptorId(object descriptor)
            {
                var variant = Get(descriptor, "variant");
                return (string)Invoke(variant, "GetUniqueIdentifier");
            }

            private object ResourceAt(string path) => InvokeStatic(_resource, "GetResourceAtPath", path);

            private Type ResolveAllowedType(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                var allowed = ((IEnumerable)InvokeStatic(_library, "GetSlotsType")).Cast<Type>()
                    .Concat(Descriptors("parameter").Select(descriptor => Get<Type>(descriptor, "modelType")));
                return allowed.FirstOrDefault(type => type?.FullName == name);
            }

            private IEnumerable<object> Settings(object model)
            {
                var method = model.GetType().GetMethods(_all).First(candidate => candidate.Name == "GetSettings");
                var args = method.GetParameters().Select((parameter, index) =>
                    index == 0 ? (object)true :
                    parameter.HasDefaultValue ? parameter.DefaultValue :
                    Activator.CreateInstance(parameter.ParameterType)).ToArray();
                return ((IEnumerable)method.Invoke(model, args)).Cast<object>();
            }

            private IEnumerable<(string Path, object Slot)> FlattenSlots(IEnumerable roots)
            {
                if (roots == null) yield break;
                var rootList = roots.Cast<object>().ToList();
                for (var i = 0; i < rootList.Count; i++)
                {
                    var rawName = Get<string>(rootList[i], "name");
                    var name = string.IsNullOrEmpty(rawName) ? $"slot{i}" : rawName;
                    if (!string.IsNullOrEmpty(rawName) && rootList.Count(slot => Get<string>(slot, "name") == rawName) > 1) name += $"[{i}]";
                    foreach (var item in FlattenSlot(rootList[i], name)) yield return item;
                }
            }

            private IEnumerable<(string Path, object Slot)> FlattenSlot(object slot, string path)
            {
                yield return (path, slot);
                var children = Children(slot).ToList();
                for (var i = 0; i < children.Count; i++)
                {
                    var rawName = Get<string>(children[i], "name");
                    var name = string.IsNullOrEmpty(rawName) ? $"slot{i}" : rawName;
                    if (!string.IsNullOrEmpty(rawName) && children.Count(child => Get<string>(child, "name") == rawName) > 1) name += $"[{i}]";
                    foreach (var item in FlattenSlot(children[i], $"{path}.{name}")) yield return item;
                }
            }

            private object ResolveSlot(object model, string direction, string path)
            {
                return FlattenSlots(GetEnumerable(model, direction)).FirstOrDefault(pair => pair.Path == path).Slot
                    ?? throw Validation($"Slot '{path}' does not exist on node '{model.GetType().FullName}'.");
            }

            private Type SlotType(object slot)
            {
                var property = Get(slot, "property");
                return property == null ? null : Get<Type>(property, "type");
            }

            private static void ValidateSize(JObject graph)
            {
                var contexts = graph["contexts"] as JArray;
                var blocks = contexts?.OfType<JObject>().Sum(context => (context["blocks"] as JArray)?.Count ?? 0) ?? 0;
                var models = (contexts?.Count ?? 0) + blocks + (graph["operators"] as JArray)?.Count + (graph["parameters"] as JArray)?.Count;
                if (models > MaxModels) throw Validation($"Graph contains {models} models; maximum is {MaxModels}.");
                var edges = ((graph["flowEdges"] as JArray)?.Count ?? 0) + ((graph["dataEdges"] as JArray)?.Count ?? 0);
                if (edges > MaxEdges) throw Validation($"Graph contains {edges} edges; maximum is {MaxEdges}.");
            }

            private static JObject Summarize(JObject graph)
            {
                var contexts = graph["contexts"] as JArray;
                return new JObject
                {
                    ["contexts"] = contexts?.Count ?? 0,
                    ["blocks"] = contexts?.OfType<JObject>().Sum(context => (context["blocks"] as JArray)?.Count ?? 0) ?? 0,
                    ["operators"] = (graph["operators"] as JArray)?.Count ?? 0,
                    ["parameters"] = (graph["parameters"] as JArray)?.Count ?? 0,
                    ["flowEdges"] = (graph["flowEdges"] as JArray)?.Count ?? 0,
                    ["dataEdges"] = (graph["dataEdges"] as JArray)?.Count ?? 0,
                };
            }

            private static IEnumerable<JObject> Objects(JToken token, string name)
            {
                if (token == null) yield break;
                if (token is not JArray array) throw Validation($"{name} must be an array.");
                foreach (var item in array)
                {
                    if (item is not JObject obj) throw Validation($"{name} must contain objects.");
                    yield return obj;
                }
            }

            private static void AddUnique(IDictionary<string, object> models, JObject item, object model)
            {
                var id = RequiredString(item, "id");
                if (!models.TryAdd(id, model)) throw Validation($"Duplicate node id: '{id}'.");
            }

            private static object Endpoint(JObject edge, string side, IReadOnlyDictionary<string, object> models, Type requiredType)
            {
                var endpoint = edge[side] as JObject ?? throw Validation($"{side} endpoint is required.");
                var id = RequiredString(endpoint, "node");
                if (!models.TryGetValue(id, out var model)) throw Validation($"Edge references unknown node '{id}'.");
                if (requiredType != null && !requiredType.IsInstanceOfType(model)) throw Validation($"Edge node '{id}' has the wrong kind.");
                return model;
            }

            private static string RequiredString(JObject item, string name)
            {
                var value = (string)item[name];
                if (string.IsNullOrWhiteSpace(value)) throw Validation($"{name} is required.");
                return value;
            }

            private static bool Matches(JObject item, string search)
                => string.IsNullOrEmpty(search) || item.ToString(Formatting.None).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

            private static bool IsCustomHlsl(object model)
                => model?.GetType().FullName?.IndexOf("HLSL", StringComparison.OrdinalIgnoreCase) >= 0;

            private static Vector2 ReadVector2(JObject value)
                => value == null ? Vector2.zero : new Vector2(value["x"]?.Value<float>() ?? 0, value["y"]?.Value<float>() ?? 0);

            private static Rect ReadRect(JObject value)
                => value == null
                    ? new Rect()
                    : new Rect(value["x"]?.Value<float>() ?? 0, value["y"]?.Value<float>() ?? 0, value["width"]?.Value<float>() ?? 0, value["height"]?.Value<float>() ?? 0);

            private static Type FindType(string fullName)
                => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(type => type != null);

            private MethodInfo RequireMethod(Type type, string name, bool isStatic = false)
                => type.GetMethods(_all).FirstOrDefault(method => method.Name == name && method.IsStatic == isStatic)
                   ?? throw new MissingMethodException(type.FullName, name);

            private object InvokeStatic(Type type, string name, params object[] args)
                => InvokeMember(type, null, name, args);

            private object Invoke(object target, string name, params object[] args)
                => InvokeMember(target.GetType(), target, name, args);

            private object InvokeMember(Type type, object target, string name, object[] args)
            {
                var methods = type.GetMethods(_all).Where(method => method.Name == name && method.IsStatic == (target == null)).ToList();
                var method = methods.FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    if (parameters.Length != args.Length) return false;
                    for (var i = 0; i < args.Length; i++)
                    {
                        if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i]) &&
                            !(parameters[i].ParameterType.IsValueType && args[i].GetType() == parameters[i].ParameterType))
                            return false;
                    }
                    return true;
                }) ?? throw new MissingMethodException(type.FullName, name);
                try { return method.Invoke(target, args); }
                catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
            }

            private object Get(object target, string name)
            {
                if (target == null || name == null) return target;
                var property = FindProperty(target.GetType(), name);
                if (property != null) return property.GetValue(target);
                var field = Field(target.GetType(), name);
                if (field != null) return field.GetValue(target);
                throw new MissingMemberException(target.GetType().FullName, name);
            }

            private T Get<T>(object target, string name)
            {
                var value = Get(target, name);
                return value == null ? default : (T)value;
            }

            private bool Has(object target, string name)
                => target != null && (FindProperty(target.GetType(), name) != null || Field(target.GetType(), name) != null);

            private PropertyInfo Property(object target, string name)
                => FindProperty(target.GetType(), name) ?? throw new MissingMemberException(target.GetType().FullName, name);

            private PropertyInfo FindProperty(Type type, string name)
            {
                for (var current = type; current != null; current = current.BaseType)
                {
                    var property = current.GetProperties(_all | BindingFlags.DeclaredOnly).FirstOrDefault(candidate => candidate.Name == name);
                    if (property != null) return property;
                }
                return null;
            }

            private FieldInfo Field(Type type, string name)
            {
                for (var current = type; current != null; current = current.BaseType)
                {
                    var field = current.GetField(name, _all | BindingFlags.DeclaredOnly);
                    if (field != null) return field;
                }
                return null;
            }

            private void Set(object target, string name, object value)
            {
                var property = FindProperty(target.GetType(), name);
                if (property?.CanWrite == true) { property.SetValue(target, value); return; }
                var field = Field(target.GetType(), name);
                if (field != null) { field.SetValue(target, value); return; }
                throw new MissingMemberException(target.GetType().FullName, name);
            }

            private void SetField(object target, string name, object value)
                => (Field(target.GetType(), name) ?? throw new MissingMemberException(target.GetType().FullName, name)).SetValue(target, value);

            private void SetIfPresent(object target, string name, JToken token, Func<JToken, object> convert)
            {
                if (token != null && Has(target, name)) Set(target, name, convert(token));
            }

            private IEnumerable GetEnumerable(object target, string name)
                => target == null || !Has(target, name) ? Array.Empty<object>() : Get(target, name) as IEnumerable ?? Array.Empty<object>();

            private IEnumerable<object> Children(object model)
                => GetEnumerable(model, "children").Cast<object>();
        }

        private static class VfxValueCodec
        {
            private const int MaxDepth = 12;

            public static JToken Write(object value, int depth = 0)
            {
                if (value == null) return JValue.CreateNull();
                if (depth > MaxDepth) return $"<{value.GetType().FullName}:max-depth>";
                var type = value.GetType();
                if (type.IsEnum) return value.ToString();
                if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                    return new JValue(value);
                if (value is Vector2 v2) return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                if (value is Vector3 v3) return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                if (value is Vector4 v4) return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                if (value is Quaternion q) return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
                if (value is Color c) return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                if (value is Rect rect) return new JObject { ["x"] = rect.x, ["y"] = rect.y, ["width"] = rect.width, ["height"] = rect.height };
                if (value is Bounds bounds) return new JObject { ["center"] = Write(bounds.center), ["size"] = Write(bounds.size) };
                if (value is Matrix4x4 matrix)
                {
                    var array = new JArray();
                    for (var row = 0; row < 4; row++)
                    for (var column = 0; column < 4; column++)
                        array.Add(matrix[row, column]);
                    return array;
                }
                if (value is AnimationCurve curve)
                {
                    return new JObject
                    {
                        ["preWrapMode"] = curve.preWrapMode.ToString(),
                        ["postWrapMode"] = curve.postWrapMode.ToString(),
                        ["keys"] = new JArray(curve.keys.Select(key => new JObject
                        {
                            ["time"] = key.time, ["value"] = key.value,
                            ["inTangent"] = key.inTangent, ["outTangent"] = key.outTangent,
                            ["inWeight"] = key.inWeight, ["outWeight"] = key.outWeight,
                            ["weightedMode"] = key.weightedMode.ToString(),
                        })),
                    };
                }
                if (value is Gradient gradient)
                {
                    return new JObject
                    {
                        ["mode"] = gradient.mode.ToString(),
                        ["colorKeys"] = new JArray(gradient.colorKeys.Select(key => new JObject { ["color"] = Write(key.color), ["time"] = key.time })),
                        ["alphaKeys"] = new JArray(gradient.alphaKeys.Select(key => new JObject { ["alpha"] = key.alpha, ["time"] = key.time })),
                    };
                }
                if (value is Object asset)
                {
                    var path = AssetDatabase.GetAssetPath(asset);
                    return string.IsNullOrEmpty(path)
                        ? JValue.CreateNull()
                        : new JObject
                        {
                            ["guid"] = AssetDatabase.AssetPathToGUID(path),
                            ["path"] = path,
                            ["name"] = asset.name,
                            ["type"] = asset.GetType().FullName,
                        };
                }
                if (value is Type reflectedType) return reflectedType.FullName;
                if (value is IEnumerable enumerable)
                    return new JArray(enumerable.Cast<object>().Take(4096).Select(item => Write(item, depth + 1)));

                if (value is ISerializationCallbackReceiver callback) callback.OnBeforeSerialize();
                var fields = SerializableFields(type);
                if (fields.Length == 0 && type.IsValueType)
                    return new JObject { ["$type"] = type.FullName };
                if (fields.Length == 0) return value.ToString();
                var result = new JObject { ["$type"] = type.FullName };
                foreach (var field in fields) result[field.Name] = Write(field.GetValue(value), depth + 1);
                return result;
            }

            public static object Read(JToken token, Type type, Func<string, Type> allowedType, int depth = 0)
            {
                if (depth > MaxDepth) throw Validation("Value nesting exceeds the supported depth.");
                if (token == null || token.Type == JTokenType.Null) return null;
                var nullable = Nullable.GetUnderlyingType(type);
                if (nullable != null) type = nullable;
                if (type == typeof(string)) return token.Value<string>();
                if (type == typeof(bool)) return token.Value<bool>();
                if (type.IsEnum) return Enum.Parse(type, token.Value<string>(), ignoreCase: true);
                if (type.IsPrimitive || type == typeof(decimal))
                    return Convert.ChangeType(((JValue)token).Value, type, CultureInfo.InvariantCulture);
                if (type == typeof(Vector2)) return new Vector2(F(token, "x"), F(token, "y"));
                if (type == typeof(Vector3)) return new Vector3(F(token, "x"), F(token, "y"), F(token, "z"));
                if (type == typeof(Vector4)) return new Vector4(F(token, "x"), F(token, "y"), F(token, "z"), F(token, "w"));
                if (type == typeof(Quaternion)) return new Quaternion(F(token, "x"), F(token, "y"), F(token, "z"), F(token, "w"));
                if (type == typeof(Color)) return new Color(F(token, "r"), F(token, "g"), F(token, "b"), F(token, "a", 1));
                if (type == typeof(Rect)) return new Rect(F(token, "x"), F(token, "y"), F(token, "width"), F(token, "height"));
                if (type == typeof(Bounds))
                    return new Bounds((Vector3)Read(token["center"], typeof(Vector3), allowedType, depth + 1), (Vector3)Read(token["size"], typeof(Vector3), allowedType, depth + 1));
                if (type == typeof(Matrix4x4))
                {
                    if (token is not JArray values || values.Count != 16) throw Validation("Matrix4x4 requires 16 row-major values.");
                    var result = new Matrix4x4();
                    for (var i = 0; i < 16; i++) result[i / 4, i % 4] = values[i].Value<float>();
                    return result;
                }
                if (type == typeof(AnimationCurve))
                {
                    var keys = (token["keys"] as JArray)?.OfType<JObject>().Select(item =>
                    {
                        var key = new Keyframe(F(item, "time"), F(item, "value"), F(item, "inTangent"), F(item, "outTangent"), F(item, "inWeight"), F(item, "outWeight"));
                        if (item["weightedMode"] != null) key.weightedMode = Enum.Parse<WeightedMode>((string)item["weightedMode"], true);
                        return key;
                    }).ToArray() ?? Array.Empty<Keyframe>();
                    return new AnimationCurve(keys)
                    {
                        preWrapMode = ParseEnum(token["preWrapMode"], WrapMode.Clamp),
                        postWrapMode = ParseEnum(token["postWrapMode"], WrapMode.Clamp),
                    };
                }
                if (type == typeof(Gradient))
                {
                    var gradient = new Gradient();
                    var colors = (token["colorKeys"] as JArray)?.OfType<JObject>().Select(item =>
                        new GradientColorKey((Color)Read(item["color"], typeof(Color), allowedType, depth + 1), F(item, "time"))).ToArray() ?? Array.Empty<GradientColorKey>();
                    var alphas = (token["alphaKeys"] as JArray)?.OfType<JObject>().Select(item =>
                        new GradientAlphaKey(F(item, "alpha"), F(item, "time"))).ToArray() ?? Array.Empty<GradientAlphaKey>();
                    gradient.SetKeys(colors, alphas);
                    if (token["mode"] != null) gradient.mode = Enum.Parse<GradientMode>((string)token["mode"], true);
                    return gradient;
                }
                if (typeof(Object).IsAssignableFrom(type))
                {
                    if (token is not JObject reference) throw Validation($"Asset reference for {type.FullName} must be an object.");
                    var path = (string)reference["path"];
                    var guid = (string)reference["guid"];
                    var name = (string)reference["name"];
                    if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(guid)) path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)) return null;
                    if (!string.IsNullOrEmpty(guid) && AssetDatabase.AssetPathToGUID(path) != guid)
                        throw Validation($"Asset reference GUID/path mismatch: '{path}'.");
                    var asset = string.IsNullOrEmpty(name)
                        ? AssetDatabase.LoadAssetAtPath(path, type)
                        : AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(candidate => candidate.name == name && type.IsInstanceOfType(candidate));
                    if (asset == null) throw new McpToolException(McpErrorCodes.NotFound, $"Asset '{path}'{(string.IsNullOrEmpty(name) ? "" : $" sub-asset '{name}'")} is not a {type.FullName}.");
                    return asset;
                }
                if (type == typeof(Type))
                    return allowedType(token.Value<string>()) ?? throw Validation($"Type '{token}' is not in the live VFX slot catalog.");
                if (type.IsArray)
                {
                    if (token is not JArray array) throw Validation($"{type.FullName} requires an array.");
                    var elementType = type.GetElementType();
                    var result = Array.CreateInstance(elementType, array.Count);
                    for (var i = 0; i < array.Count; i++) result.SetValue(Read(array[i], elementType, allowedType, depth + 1), i);
                    return result;
                }
                if (typeof(IList).IsAssignableFrom(type) && type.IsGenericType)
                {
                    if (token is not JArray listValues) throw Validation($"{type.FullName} requires an array.");
                    var elementType = type.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(type, nonPublic: true);
                    foreach (var value in listValues) list.Add(Read(value, elementType, allowedType, depth + 1));
                    return list;
                }

                if (token is not JObject obj) throw Validation($"{type.FullName} requires an object value.");
                var instance = Activator.CreateInstance(type, nonPublic: true);
                foreach (var field in SerializableFields(type))
                {
                    if (obj[field.Name] != null)
                        field.SetValue(instance, Read(obj[field.Name], field.FieldType, allowedType, depth + 1));
                }
                if (instance is ISerializationCallbackReceiver callback) callback.OnAfterDeserialize();
                return instance;
            }

            private static FieldInfo[] SerializableFields(Type type)
                => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(field => !field.IsStatic &&
                                    !field.IsNotSerialized &&
                                    (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null))
                    .ToArray();

            private static float F(JToken token, string name, float fallback = 0)
                => token?[name]?.Value<float>() ?? fallback;

            private static T ParseEnum<T>(JToken token, T fallback) where T : struct
                => token == null ? fallback : Enum.Parse<T>((string)token, true);
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
#endif
