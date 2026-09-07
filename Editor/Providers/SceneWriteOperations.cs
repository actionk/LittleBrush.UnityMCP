using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    public static class SceneWriteOperations
    {
        public const int MaxOperations = 256;

        public static JObject Apply(Scene scene, JArray operations)
        {
            var result = ApplyInternal(() => scene.GetRootGameObjects(), scene, operations);
            if (scene.IsValid() && result["failed"]?.Value<int>() == 0)
                EditorSceneManager.MarkSceneDirty(scene);
            return result;
        }

        /// <summary>
        /// Apply operations to a single root GameObject (e.g. a prefab loaded via
        /// PrefabUtility.LoadPrefabContents). The caller is responsible for saving the
        /// resulting hierarchy.
        /// </summary>
        public static JObject ApplyToRoot(GameObject root, JArray operations)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            return ApplyInternal(() => new[] { root }, default, operations);
        }

        private static JObject ApplyInternal(Func<GameObject[]> roots, Scene contextScene, JArray operations)
        {
            if (operations == null || operations.Count is < 1 or > MaxOperations)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    $"operations must contain 1 to {MaxOperations} items.");
            var results = new JArray();
            int succeeded = 0, failed = 0;
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var rolledBack = false;
            Undo.SetCurrentGroupName("MCP scene.write");
            try
            {
                foreach (var op in operations.OfType<JObject>())
                {
                    try
                    {
                        results.Add(ApplyOne(roots, contextScene, op));
                        succeeded++;
                    }
                    catch (McpToolException ex)
                    {
                        failed++;
                        results.Add(new JObject
                        {
                            ["type"] = (string)op["type"],
                            ["error"] = ex.Message,
                            ["errorCode"] = ex.Code,
                        });
                        Undo.RevertAllDownToGroup(group);
                        rolledBack = true;
                        succeeded = 0;
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        results.Add(new JObject
                        {
                            ["type"] = (string)op["type"],
                            ["error"] = ex.Message,
                            ["errorCode"] = McpErrorCodes.ToolError,
                        });
                        Undo.RevertAllDownToGroup(group);
                        rolledBack = true;
                        succeeded = 0;
                        break;
                    }
                }
            }
            finally
            {
                if (!rolledBack)
                    Undo.CollapseUndoOperations(group);
            }
            return new JObject
            {
                ["results"] = results,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["rolledBack"] = rolledBack,
            };
        }

        private static JObject ApplyOne(Func<GameObject[]> roots, Scene contextScene, JObject op)
        {
            var type = (string)op["type"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "Operation missing 'type'.");
            return type switch
            {
                "create_gameobject" => CreateGameObject(roots, contextScene, op),
                "delete_gameobject" => DeleteGameObject(roots, op),
                "rename_gameobject" => RenameGameObject(roots, op),
                "set_active" => SetActive(roots, op),
                "set_transform" => SetTransform(roots, op),
                "reparent" => Reparent(roots, op),
                "add_component" => AddComponent(roots, op),
                "remove_component" => RemoveComponent(roots, op),
                "set_property" => SetProperty(roots, op),
                "instantiate_prefab" => InstantiatePrefab(roots, contextScene, op),
                _ => throw new McpToolException(McpErrorCodes.InvalidParams, $"Unsupported operation '{type}'."),
            };
        }

        private static JObject Reparent(Func<GameObject[]> roots, JObject op)
        {
            var go = FindGameObject(roots, op);
            var newParentPath = (string)op["newParentPath"];
            var keepLocal = op["worldPositionStays"]?.Value<bool>() == false;
            var localPosition = go.transform.localPosition;
            var localRotation = go.transform.localRotation;
            var localScale = go.transform.localScale;
            Undo.SetTransformParent(go.transform,
                string.IsNullOrWhiteSpace(newParentPath) ? null : FindGameObject(roots, newParentPath).transform,
                "MCP reparent");
            if (keepLocal)
            {
                Undo.RecordObject(go.transform, "MCP reparent local transform");
                go.transform.SetLocalPositionAndRotation(localPosition, localRotation);
                go.transform.localScale = localScale;
            }
            return new JObject { ["type"] = "reparent", ["path"] = SceneSerializer.GetHierarchyPath(go) };
        }

        private static JObject AddComponent(Func<GameObject[]> roots, JObject op)
        {
            var go = FindGameObject(roots, op);
            var typeName = (string)op["componentType"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "add_component requires 'componentType'.");
            var type = ResolveComponentType(typeName);
            var component = Undo.AddComponent(go, type);
            if (component == null)
                throw new McpToolException(McpErrorCodes.ToolError, $"Failed to add component '{typeName}' to '{go.name}'.");
            return new JObject
            {
                ["type"] = "add_component",
                ["path"] = SceneSerializer.GetHierarchyPath(go),
                ["componentType"] = type.FullName,
            };
        }

        private static JObject RemoveComponent(Func<GameObject[]> roots, JObject op)
        {
            var go = FindGameObject(roots, op);
            var typeName = (string)op["componentType"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "remove_component requires 'componentType'.");
            var type = ResolveComponentType(typeName);
            var component = FindComponent(go, type, op);
            if (component is Transform)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Cannot remove Transform component.");
            Undo.DestroyObjectImmediate(component);
            return new JObject
            {
                ["type"] = "remove_component",
                ["path"] = SceneSerializer.GetHierarchyPath(go),
                ["componentType"] = type.FullName,
                ["componentIndex"] = op["componentIndex"]?.Value<int>() ?? 0,
            };
        }

        private static JObject SetProperty(Func<GameObject[]> roots, JObject op)
        {
            var go = FindGameObject(roots, op);
            var typeName = (string)op["componentType"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "set_property requires 'componentType'.");
            var propPath = (string)op["property"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "set_property requires 'property'.");
            var value = op["value"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "set_property requires 'value'.");

            var type = ResolveComponentType(typeName);
            var component = FindComponent(go, type, op);

            using var so = new SerializedObject(component);
            var sp = so.FindProperty(propPath)
                ?? throw new McpToolException(McpErrorCodes.NotFound, $"SerializedProperty '{propPath}' not found on {type.FullName}.");

            if (sp.propertyType == SerializedPropertyType.ObjectReference &&
                value is JObject referenceValue &&
                referenceValue["hierarchyPath"] != null)
            {
                ApplyHierarchyReference(sp, roots, referenceValue);
            }
            else
            {
                ApplySerializedValue(sp, value);
            }
            so.ApplyModifiedProperties();
            return new JObject
            {
                ["type"] = "set_property",
                ["path"] = SceneSerializer.GetHierarchyPath(go),
                ["componentType"] = type.FullName,
                ["componentIndex"] = op["componentIndex"]?.Value<int>() ?? 0,
                ["property"] = propPath,
            };
        }

        internal static void ApplySerializedValue(SerializedProperty sp, JToken value)
        {
            if (sp.isArray && sp.propertyType != SerializedPropertyType.String && value is JArray array)
            {
                sp.arraySize = array.Count;
                for (var i = 0; i < array.Count; i++)
                    ApplySerializedValue(sp.GetArrayElementAtIndex(i), array[i]);
                return;
            }

            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                    sp.intValue = value.Value<int>();
                    break;
                case SerializedPropertyType.Boolean: sp.boolValue = value.Value<bool>(); break;
                case SerializedPropertyType.Float: sp.floatValue = value.Value<float>(); break;
                case SerializedPropertyType.String: sp.stringValue = value.Value<string>(); break;
                case SerializedPropertyType.Color when value is JObject jc:
                    sp.colorValue = new Color((float)jc["r"], (float)jc["g"], (float)jc["b"], (float)(jc["a"] ?? 1f)); break;
                case SerializedPropertyType.Vector2 when value is JObject jv2:
                    sp.vector2Value = new Vector2((float)jv2["x"], (float)jv2["y"]); break;
                case SerializedPropertyType.Vector3 when value is JObject jv3:
                    sp.vector3Value = new Vector3((float)jv3["x"], (float)jv3["y"], (float)jv3["z"]); break;
                case SerializedPropertyType.Vector4 when value is JObject jv4:
                    sp.vector4Value = new Vector4((float)jv4["x"], (float)jv4["y"], (float)jv4["z"], (float)jv4["w"]); break;
                case SerializedPropertyType.Quaternion when value is JObject jq:
                    sp.quaternionValue = new Quaternion((float)jq["x"], (float)jq["y"], (float)jq["z"], (float)jq["w"]); break;
                case SerializedPropertyType.AnimationCurve:
                    sp.animationCurveValue = ParseAnimationCurve(value);
                    break;
                case SerializedPropertyType.Gradient:
                    sp.gradientValue = ParseGradient(value);
                    break;
                case SerializedPropertyType.Enum:
                    sp.enumValueIndex = ReadEnumIndex(sp, value);
                    break;
                case SerializedPropertyType.LayerMask: sp.intValue = value.Value<int>(); break;
                case SerializedPropertyType.ObjectReference:
                {
                    var reference = ReadObjectReference(value);
                    sp.objectReferenceValue = reference;
                    if (reference != null && sp.objectReferenceValue != reference)
                        throw new McpToolException(McpErrorCodes.InvalidParams,
                            $"Object reference type '{reference.GetType().FullName}' is not compatible with '{sp.propertyPath}'.");
                    break;
                }
                default:
                    throw new McpToolException(McpErrorCodes.InvalidParams,
                        $"SerializedProperty type {sp.propertyType} is not yet supported by set_property.");
            }
        }

        private static AnimationCurve ParseAnimationCurve(JToken value)
        {
            if (value is not JObject obj)
                throw new McpToolException(McpErrorCodes.InvalidParams, "AnimationCurve value must be an object.");

            var curve = new AnimationCurve();
            if (obj["keys"] is JArray keys)
            {
                foreach (var token in keys)
                {
                    if (token is not JObject key)
                        throw new McpToolException(McpErrorCodes.InvalidParams, "AnimationCurve keys must be objects.");

                    var frame = new Keyframe(
                        key["time"]?.Value<float>() ?? 0f,
                        key["value"]?.Value<float>() ?? 0f,
                        key["inTangent"]?.Value<float>() ?? 0f,
                        key["outTangent"]?.Value<float>() ?? 0f,
                        key["inWeight"]?.Value<float>() ?? 0f,
                        key["outWeight"]?.Value<float>() ?? 0f)
                    {
                        weightedMode = ParseEnumValue(key["weightedMode"], WeightedMode.None),
                    };
                    curve.AddKey(frame);
                }
            }

            curve.preWrapMode = ParseEnumValue(obj["preWrapMode"], WrapMode.Default);
            curve.postWrapMode = ParseEnumValue(obj["postWrapMode"], WrapMode.Default);
            return curve;
        }

        private static Gradient ParseGradient(JToken value)
        {
            if (value is not JObject obj)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Gradient value must be an object.");

            var gradient = new Gradient();
            var colorTokens = obj["colorKeys"] as JArray;
            var alphaTokens = obj["alphaKeys"] as JArray;
            if (colorTokens != null || alphaTokens != null)
            {
                var colors = colorTokens?.OfType<JObject>().Select(key =>
                {
                    var color = key["color"] as JObject;
                    if (color == null)
                        throw new McpToolException(McpErrorCodes.InvalidParams, "Gradient colorKeys require a color object.");
                    return new GradientColorKey(
                        new Color(
                            color["r"]?.Value<float>() ?? 0f,
                            color["g"]?.Value<float>() ?? 0f,
                            color["b"]?.Value<float>() ?? 0f,
                            color["a"]?.Value<float>() ?? 1f),
                        key["time"]?.Value<float>() ?? 0f);
                }).ToArray() ?? gradient.colorKeys;
                var alphas = alphaTokens?.OfType<JObject>().Select(key => new GradientAlphaKey(
                    key["alpha"]?.Value<float>() ?? 0f,
                    key["time"]?.Value<float>() ?? 0f)).ToArray() ?? gradient.alphaKeys;
                gradient.SetKeys(colors, alphas);
            }

            gradient.mode = ParseEnumValue(obj["mode"], gradient.mode);
            return gradient;
        }

        private static T ParseEnumValue<T>(JToken token, T fallback) where T : struct
        {
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            if (token.Type == JTokenType.Integer)
                return (T)Enum.ToObject(typeof(T), token.Value<int>());
            return Enum.Parse<T>(token.Value<string>(), true);
        }

        private static void ApplyHierarchyReference(
            SerializedProperty property,
            Func<GameObject[]> roots,
            JObject value)
        {
            var path = (string)value["hierarchyPath"];
            if (string.IsNullOrWhiteSpace(path))
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "Object reference hierarchyPath must be a non-empty string.");

            var target = FindGameObject(roots(), path, rejectAmbiguous: true);
            UnityEngine.Object reference = target;
            var componentTypeName = (string)value["componentType"];
            if (!string.IsNullOrWhiteSpace(componentTypeName))
            {
                var componentType = ResolveComponentType(componentTypeName);
                var componentIndex = value["componentIndex"]?.Value<int>() ?? 0;
                reference = FindComponent(target, componentType, componentIndex);
            }

            property.objectReferenceValue = reference;
            if (property.objectReferenceValue != reference)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    $"Object reference type '{reference.GetType().FullName}' is not compatible with '{property.propertyPath}'.");
        }

        private static int ReadEnumIndex(SerializedProperty sp, JToken value)
        {
            if (value.Type == JTokenType.Integer)
                return value.Value<int>();
            var name = value.Value<string>();
            for (var i = 0; i < sp.enumNames.Length; i++)
                if (string.Equals(sp.enumNames[i], name, StringComparison.Ordinal))
                    return i;
            throw new McpToolException(McpErrorCodes.InvalidParams, $"Enum value '{name}' is not valid for {sp.propertyPath}.");
        }

        private static UnityEngine.Object ReadObjectReference(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null)
                return null;

            if (value.Type == JTokenType.Object)
            {
                var obj = (JObject)value;
                var entityIdToken = obj["entityId"];
                if (entityIdToken != null)
                {
                    if (!UnitySerializer.TryParseEntityId(entityIdToken, out EntityId entityId))
                        throw new McpToolException(McpErrorCodes.InvalidParams, "entityId must be an unsigned 64-bit integer string.");

                    var referencedObject = EditorUtility.EntityIdToObject(entityId);
                    if (referencedObject == null)
                        throw new McpToolException(McpErrorCodes.NotFound, $"Object reference entityId not found: {entityId}.");
                    return referencedObject;
                }

                var localIdToken = obj["localId"];
                var guid = (string)obj["guid"];
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path))
                        throw new McpToolException(McpErrorCodes.NotFound, $"Object reference GUID not found: '{guid}'.");
                    value = path;
                }
                else
                    value = obj["assetPath"] ?? obj["path"] ?? JValue.CreateNull();

                if (localIdToken != null)
                {
                    var referencedPath = value.Value<string>();
                    if (string.IsNullOrWhiteSpace(referencedPath))
                        throw new McpToolException(McpErrorCodes.InvalidParams,
                            "Object reference localId requires guid, assetPath, or path.");
                    var normalizedPath = AssetProvider.NormalizeAssetPath(referencedPath);
                    var localId = ReadLocalId(localIdToken);
                    var subAsset = AssetDatabase.LoadAllAssetsAtPath(normalizedPath)
                        .FirstOrDefault(candidate =>
                            candidate != null &&
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out _, out long candidateLocalId) &&
                            candidateLocalId == localId);
                    if (subAsset == null)
                        throw new McpToolException(McpErrorCodes.NotFound,
                            $"Object reference localId '{localId}' not found at '{normalizedPath}'.");
                    return subAsset;
                }
            }

            var assetPath = value.Value<string>();
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var normalized = AssetProvider.NormalizeAssetPath(assetPath);
            var referencedAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized);
            if (referencedAsset == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Object reference asset not found: '{normalized}'.");
            return referencedAsset;
        }

        private static long ReadLocalId(JToken value)
        {
            if (value.Type == JTokenType.Integer)
                return value.Value<long>();
            if (value.Type == JTokenType.String &&
                long.TryParse(value.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var localId))
                return localId;
            throw new McpToolException(McpErrorCodes.InvalidParams,
                "Object reference localId must be a 64-bit integer or decimal string.");
        }

        private static Type ResolveComponentType(string name)
        {
            var t = Type.GetType(name, throwOnError: false);
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
                catch { continue; }

                foreach (var candidate in types)
                {
                    if (candidate == null) continue;
                    if (!typeof(Component).IsAssignableFrom(candidate)) continue;
                    if (candidate.FullName == name || candidate.Name == name)
                        return candidate;
                }
            }
            throw new McpToolException(McpErrorCodes.NotFound, $"Component type not found: '{name}'.");
        }

        private static JObject CreateGameObject(Func<GameObject[]> roots, Scene contextScene, JObject op)
        {
            var name = (string)op["name"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "create_gameobject requires 'name'.");

            var parentPath = (string)op["parentPath"];
            var hasParent = !string.IsNullOrWhiteSpace(parentPath);

            // In a root-only context (prefab), every new GameObject must live under the
            // existing root — there is no scene to host it as a sibling.
            if (!contextScene.IsValid() && !hasParent)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "create_gameobject requires 'parentPath' when applied to a single root (e.g. a prefab).");

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "MCP create");

            if (contextScene.IsValid())
                SceneManager.MoveGameObjectToScene(go, contextScene);

            if (hasParent)
            {
                var parent = FindGameObject(roots, parentPath);
                go.transform.SetParent(parent.transform, false);
            }
            ApplyTransform(go.transform, op);
            return new JObject { ["type"] = "create_gameobject", ["path"] = SceneSerializer.GetHierarchyPath(go) };
        }

        private static JObject DeleteGameObject(Func<GameObject[]> roots, JObject op)
        {
            var go = FindGameObject(roots, op);
            var path = SceneSerializer.GetHierarchyPath(go);
            Undo.DestroyObjectImmediate(go);
            return new JObject { ["type"] = "delete_gameobject", ["path"] = path };
        }

        private static JObject RenameGameObject(Func<GameObject[]> roots, JObject op)
        {
            var go = FindGameObject(roots, op);
            Undo.RecordObject(go, "MCP rename");
            go.name = (string)op["newName"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "rename_gameobject requires 'newName'.");
            return new JObject { ["type"] = "rename_gameobject", ["path"] = SceneSerializer.GetHierarchyPath(go) };
        }

        private static JObject SetActive(Func<GameObject[]> roots, JObject op)
        {
            var go = FindGameObject(roots, op);
            Undo.RecordObject(go, "MCP set_active");
            go.SetActive(op["active"]?.Value<bool>() ?? true);
            return new JObject { ["type"] = "set_active", ["path"] = SceneSerializer.GetHierarchyPath(go), ["active"] = go.activeSelf };
        }

        private static JObject SetTransform(Func<GameObject[]> roots, JObject op)
        {
            var go = FindGameObject(roots, op);
            Undo.RecordObject(go.transform, "MCP set_transform");
            ApplyTransform(go.transform, op);
            return new JObject { ["type"] = "set_transform", ["path"] = SceneSerializer.GetHierarchyPath(go) };
        }

        private static void ApplyTransform(Transform t, JObject op)
        {
            if (op["localPosition"] is JObject lp) t.localPosition = ReadV3(lp);
            if (op["localEulerAngles"] is JObject le) t.localEulerAngles = ReadV3(le);
            if (op["localScale"] is JObject ls) t.localScale = ReadV3(ls);
        }

        private static Vector3 ReadV3(JObject o)
            => new((float)(o["x"] ?? 0), (float)(o["y"] ?? 0), (float)(o["z"] ?? 0));

        private static string RequirePath(JObject op)
            => (string)op["path"]
               ?? throw new McpToolException(McpErrorCodes.InvalidParams, "Operation requires 'path'.");

        private static JObject InstantiatePrefab(Func<GameObject[]> roots, Scene contextScene, JObject op)
        {
            // Nested-prefab insertion inside a prefab asset is out of scope for v1 — it
            // produces serialized prefab linkage that needs apply/revert plumbing we don't
            // expose yet. Allow only in a real scene context.
            if (!contextScene.IsValid())
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "instantiate_prefab is not supported when applied to a single root (e.g. a prefab). Use scene.write in a scene context, or add a typed prefab.add_nested_prefab tool.");

            var assetPath = (string)op["assetPath"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "instantiate_prefab requires 'assetPath'.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Prefab not found: '{assetPath}'.");

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, contextScene);
            if (go == null)
                throw new McpToolException(McpErrorCodes.ToolError, $"Failed to instantiate prefab '{assetPath}'.");
            Undo.RegisterCreatedObjectUndo(go, "MCP instantiate_prefab");

            if ((string)op["parentPath"] is { } parentPath && !string.IsNullOrWhiteSpace(parentPath))
            {
                var parent = FindGameObject(roots, parentPath);
                Undo.SetTransformParent(go.transform, parent.transform, "MCP instantiate_prefab reparent");
            }

            if ((string)op["name"] is { } newName && !string.IsNullOrWhiteSpace(newName))
            {
                Undo.RecordObject(go, "MCP instantiate_prefab rename");
                go.name = newName;
            }

            ApplyTransform(go.transform, op);

            return new JObject
            {
                ["type"] = "instantiate_prefab",
                ["path"] = SceneSerializer.GetHierarchyPath(go),
                ["prefabAssetPath"] = assetPath,
            };
        }

        private static GameObject FindGameObject(Func<GameObject[]> roots, JObject op)
        {
            var entityIdToken = op["entityId"];
            if (entityIdToken != null)
            {
                if (!UnitySerializer.TryParseEntityId(entityIdToken, out EntityId entityId))
                    throw new McpToolException(McpErrorCodes.InvalidParams, "entityId must be an unsigned 64-bit integer string.");

                if (EditorUtility.EntityIdToObject(entityId) is GameObject go && IsUnderRoots(go, roots()))
                    return go;
                throw new McpToolException(McpErrorCodes.NotFound, $"GameObject not found for entityId {entityId}.");
            }

            return FindGameObject(roots, RequirePath(op));
        }

        private static Component FindComponent(GameObject go, Type type, JObject op)
        {
            var index = op["componentIndex"]?.Value<int>() ?? 0;
            return FindComponent(go, type, index);
        }

        private static Component FindComponent(GameObject go, Type type, int index)
        {
            var components = go.GetComponents(type);
            if (index < 0 || index >= components.Length)
                throw new McpToolException(McpErrorCodes.NotFound, $"Component '{type.FullName}' index {index} not found on '{go.name}'.");
            return components[index];
        }

        private static GameObject FindGameObject(Func<GameObject[]> roots, string path)
            => FindGameObject(roots(), path);

        private static GameObject FindGameObject(IList<GameObject> roots, string path)
            => FindGameObject(roots, path, rejectAmbiguous: false);

        private static GameObject FindGameObject(
            IList<GameObject> roots,
            string path,
            bool rejectAmbiguous)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                throw new McpToolException(McpErrorCodes.NotFound, $"Invalid path '{path}'.");

            Transform current = null;
            foreach (var segment in segments)
            {
                if (current == null)
                {
                    foreach (var r in roots)
                    {
                        if (r == null || r.name != segment) continue;
                        if (current != null && rejectAmbiguous)
                            throw new McpToolException(McpErrorCodes.InvalidParams,
                                $"GameObject path is ambiguous: {path}");
                        current = r.transform;
                    }
                }
                else
                {
                    Transform next = null;
                    foreach (Transform child in current)
                    {
                        if (child.name != segment) continue;
                        if (next != null && rejectAmbiguous)
                            throw new McpToolException(McpErrorCodes.InvalidParams,
                                $"GameObject path is ambiguous: {path}");
                        next = child;
                    }
                    current = next;
                }
                if (current == null)
                    throw new McpToolException(McpErrorCodes.NotFound, $"GameObject not found: {path}");
            }
            return current.gameObject;
        }

        private static bool IsUnderRoots(GameObject go, IList<GameObject> roots)
        {
            foreach (var root in roots)
            {
                if (root == null) continue;
                if (go == root || go.transform.IsChildOf(root.transform))
                    return true;
            }
            return false;
        }
    }
}
