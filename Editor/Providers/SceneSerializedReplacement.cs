using System;
using System.Collections.Generic;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    public static class SceneSerializedReplacement
    {
        private const string UndoLabel = "MCP serialized ID replacement";

        public static JObject Replace(
            Scene scene,
            string oldId,
            string newId,
            bool dryRun,
            bool save,
            int maxResults)
        {
            if (!scene.IsValid())
                throw new McpToolException(McpErrorCodes.NotFound, "Scene is not valid.");

            var result = ReplaceObjects(
                EnumerateSceneObjects(scene),
                "scenePath",
                scene.path,
                oldId,
                newId,
                dryRun,
                maxResults);

            if (!dryRun && (int)result["changedCount"] > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (save)
                    result["saved"] = EditorSceneManager.SaveScene(scene);
            }

            return result;
        }

        public static JObject ReplacePrefab(
            GameObject root,
            string prefabPath,
            string oldId,
            string newId,
            bool dryRun,
            bool save,
            int maxResults)
        {
            if (root == null)
                throw new McpToolException(McpErrorCodes.NotFound, "Prefab contents root is null.");
            if (string.IsNullOrWhiteSpace(prefabPath))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "prefabPath must be a non-empty string.");

            var result = ReplaceObjects(
                EnumerateTree(root),
                "assetPath",
                prefabPath,
                oldId,
                newId,
                dryRun,
                maxResults);

            if (!dryRun && save && (int)result["changedCount"] > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out var success);
                if (!success)
                    throw new McpToolException(McpErrorCodes.ToolError, $"SaveAsPrefabAsset failed: '{prefabPath}'.");
                AssetDatabase.ImportAsset(prefabPath);
                result["saved"] = true;
            }

            return result;
        }

        private static JObject ReplaceObjects(
            IEnumerable<GameObject> objects,
            string pathProperty,
            string path,
            string oldId,
            string newId,
            bool dryRun,
            int maxResults)
        {
            ValidateIds(oldId, newId, maxResults);

            var matches = new JArray();
            var matchCount = 0;
            var stringMatches = 0;
            var objectReferenceMatches = 0;
            var scannedComponents = 0;
            var changedCount = 0;
            var undoGroup = -1;
            var targetObject = (UnityEngine.Object)null;
            var oldIdIsEntityId = UnitySerializer.TryParseEntityId(new JValue(oldId), out var oldEntityId);

            if (!dryRun)
            {
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(UndoLabel);
            }

            try
            {
                foreach (var go in objects)
                {
                    var objectPath = SceneSerializer.GetHierarchyPath(go);
                    foreach (var component in go.GetComponents<Component>())
                    {
                        if (component == null)
                            continue;

                        scannedComponents++;
                        using var serialized = new SerializedObject(component);
                        serialized.Update();
                        var iterator = serialized.GetIterator();
                        if (!iterator.Next(true))
                            continue;

                        var componentChanged = false;
                        do
                        {
                            var kind = string.Empty;
                            if (iterator.propertyType == SerializedPropertyType.String &&
                                iterator.propertyPath != "m_EditorClassIdentifier" &&
                                string.Equals(iterator.stringValue, oldId, StringComparison.Ordinal))
                            {
                                kind = "string";
                                stringMatches++;
                                if (!dryRun)
                                    iterator.stringValue = newId;
                            }
                            else if (!(component is Transform) &&
                                     IsReplaceableObjectReference(iterator) &&
                                     iterator.objectReferenceValue != null &&
                                     oldIdIsEntityId &&
                                     iterator.objectReferenceValue.GetEntityId().Equals(oldEntityId))
                            {
                                kind = "objectReference";
                                objectReferenceMatches++;
                                targetObject ??= ResolveEntityId(newId);
                                if (!iterator.objectReferenceValue.GetType().IsInstanceOfType(targetObject))
                                    throw new McpToolException(
                                        McpErrorCodes.InvalidParams,
                                        $"newId object type '{targetObject.GetType().FullName}' does not match serialized reference type '{iterator.objectReferenceValue.GetType().FullName}'.");
                                if (!dryRun)
                                    iterator.objectReferenceValue = targetObject;
                            }

                            if (kind.Length == 0)
                                continue;

                            matchCount++;
                            if (matches.Count < maxResults)
                            {
                                matches.Add(new JObject
                                {
                                    ["path"] = objectPath,
                                    ["componentType"] = component.GetType().FullName,
                                    ["componentEntityId"] = UnitySerializer.ToEntityIdString(component),
                                    ["property"] = iterator.propertyPath,
                                    ["kind"] = kind,
                                });
                            }

                            if (!dryRun)
                            {
                                componentChanged = true;
                                changedCount++;
                            }
                        }
                        while (iterator.Next(true));

                        if (!dryRun && componentChanged)
                        {
                            Undo.RecordObject(go, UndoLabel);
                            Undo.RecordObject(component, UndoLabel);
                            serialized.ApplyModifiedProperties();
                            EditorUtility.SetDirty(component);
                            if (PrefabUtility.IsPartOfPrefabInstance(component))
                                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                        }
                    }
                }

                if (!dryRun)
                    Undo.CollapseUndoOperations(undoGroup);
            }
            catch
            {
                if (!dryRun && undoGroup >= 0)
                    Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }

            return new JObject
            {
                [pathProperty] = path,
                ["oldId"] = oldId,
                ["newId"] = newId,
                ["dryRun"] = dryRun,
                ["scannedComponents"] = scannedComponents,
                ["matchCount"] = matchCount,
                ["changedCount"] = dryRun ? 0 : changedCount,
                ["stringMatches"] = stringMatches,
                ["objectReferenceMatches"] = objectReferenceMatches,
                ["truncated"] = matchCount > matches.Count,
                ["saved"] = false,
                ["matches"] = matches,
            };
        }

        private static void ValidateIds(string oldId, string newId, int maxResults)
        {
            if (string.IsNullOrWhiteSpace(oldId))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "oldId must be a non-empty string.");
            if (string.IsNullOrWhiteSpace(newId))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "newId must be a non-empty string.");
            if (string.Equals(oldId, newId, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.ValidationFailed, "oldId and newId must be different.");
            if (maxResults < 1)
                throw new McpToolException(McpErrorCodes.ValidationFailed, "maxResults must be at least 1.");
        }

        private static UnityEngine.Object ResolveEntityId(string value)
        {
            if (!UnitySerializer.TryParseEntityId(new JValue(value), out var entityId))
                throw new McpToolException(
                    McpErrorCodes.InvalidParams,
                    "newId must be an entityId string when replacing an object reference.");

            var target = EditorUtility.EntityIdToObject(entityId);
            if (target == null)
                throw new McpToolException(McpErrorCodes.NotFound, $"Object not found for newId {value}.");
            return target;
        }

        private static bool IsReplaceableObjectReference(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.ObjectReference)
                return false;

            return property.propertyPath != "m_GameObject" &&
                   property.propertyPath != "m_Script" &&
                   property.propertyPath != "m_CorrespondingSourceObject" &&
                   property.propertyPath != "m_PrefabInstance" &&
                   property.propertyPath != "m_PrefabAsset" &&
                   property.propertyPath != "m_PrefabParentObject";
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
    }
}
