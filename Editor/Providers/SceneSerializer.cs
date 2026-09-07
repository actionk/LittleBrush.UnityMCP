using System;
using System.Collections.Generic;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    public static class SceneSerializer
    {
        public sealed class Options
        {
            public int MaxDepth = 10;
            public int MaxNodes = 250;
            public bool IncludeComponents = true;
            public bool IncludeProperties;
            public bool IncludeCurveValues;
            public string Profile = "outline";
            public string[] ComponentTypes = Array.Empty<string>();
            public string PropertySearch;
            public string[] PropertyPaths = Array.Empty<string>();
            public int PropertyOffset;
            public int PropertyLimit = 100;
            public int MaxStringCharacters = 2000;
        }

        public static JObject Serialize(Scene scene, Options options)
        {
            var counter = new Counter { Remaining = options.MaxNodes };
            var roots = new JArray();
            foreach (var root in scene.GetRootGameObjects().OrderBy(g => g.name, StringComparer.Ordinal))
            {
                if (counter.Remaining <= 0) { counter.Truncated = true; break; }
                roots.Add(SerializeGo(root, 0, options, counter));
            }

            return new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["isDirty"] = scene.isDirty,
                ["profile"] = options.Profile,
                ["rootCount"] = roots.Count,
                ["returnedNodes"] = options.MaxNodes - counter.Remaining,
                ["roots"] = roots,
                ["truncated"] = counter.Truncated,
            };
        }

        /// <summary>
        /// Serialize a single root GameObject (e.g. the root of a prefab loaded via
        /// <see cref="UnityEditor.PrefabUtility.LoadPrefabContents"/>). Returns the same
        /// node shape as one entry in <see cref="Serialize(Scene, Options)"/>'s roots[].
        /// </summary>
        public static JObject SerializeRoot(GameObject root, Options options)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var counter = new Counter { Remaining = options.MaxNodes };
            var node = SerializeGo(root, 0, options, counter);
            node["profile"] = options.Profile;
            node["returnedNodes"] = options.MaxNodes - counter.Remaining;
            node["truncated"] = counter.Truncated;
            return node;
        }

        private sealed class Counter
        {
            public int Remaining;
            public bool Truncated;
        }

        private static JObject SerializeGo(GameObject go, int depth, Options options, Counter counter)
        {
            if (counter.Remaining <= 0)
            {
                counter.Truncated = true;
                return new JObject { ["$truncated"] = "maxNodes" };
            }
            counter.Remaining--;

            var node = new JObject { ["name"] = go.name };
            if (options.Profile == "full")
            {
                node["path"] = GetHierarchyPath(go);
                node["entityId"] = UnitySerializer.ToEntityIdString(go);
                node["activeSelf"] = go.activeSelf;
                node["tag"] = go.tag;
                node["layer"] = go.layer;
                node["localPosition"] = UnitySerializer.ToJson(go.transform.localPosition);
                node["localEulerAngles"] = UnitySerializer.ToJson(go.transform.localEulerAngles);
                node["localScale"] = UnitySerializer.ToJson(go.transform.localScale);
            }
            else
            {
                if (!go.activeSelf) node["activeSelf"] = false;
                if (go.tag != "Untagged") node["tag"] = go.tag;
                if (go.layer != 0) node["layer"] = go.layer;
            }

            if (options.IncludeComponents || options.IncludeProperties)
            {
                var components = new JArray();
                var indices = new Dictionary<Type, int>();
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var type = c.GetType();
                    if (options.ComponentTypes.Length > 0 && !options.ComponentTypes.Contains(type.FullName) && !options.ComponentTypes.Contains(type.Name))
                        continue;
                    indices.TryGetValue(type, out var index);
                    indices[type] = index + 1;
                    var component = new JObject
                    {
                        ["type"] = type.FullName,
                        ["componentIndex"] = index,
                        ["entityId"] = UnitySerializer.ToEntityIdString(c),
                    };
                    if (options.IncludeProperties)
                        AddPropertyPage(component, c, options);
                    components.Add(component);
                }
                node["components"] = components;
            }

            if (depth + 1 > options.MaxDepth)
            {
                node["$childrenTruncated"] = "maxDepth";
                return node;
            }

            var children = new JArray();
            foreach (Transform child in go.transform)
            {
                if (counter.Remaining <= 0)
                {
                    counter.Truncated = true;
                    break;
                }
                children.Add(SerializeGo(child.gameObject, depth + 1, options, counter));
            }
            if (options.Profile == "full" || children.Count > 0) node["children"] = children;
            return node;
        }

        private static void AddPropertyPage(JObject component, Component source, Options options)
        {
            var page = AssetsProvider.ReadPropertyPage(source, options.PropertyPaths, options.PropertySearch,
                options.PropertyOffset, options.PropertyLimit, options.IncludeCurveValues, options.MaxStringCharacters,
                out var count, out var filteredCount);
            component["propertyCount"] = count;
            component["filteredPropertyCount"] = filteredCount;
            component["propertyOffset"] = options.PropertyOffset;
            component["propertyReturned"] = page.Count;
            component["propertiesTruncated"] = options.PropertyOffset + page.Count < filteredCount;
            if (options.PropertyOffset + page.Count < filteredCount)
                component["nextPropertyOffset"] = options.PropertyOffset + page.Count;
            if (page.Count > 0) component["properties"] = page;
        }

        public static string GetHierarchyPath(GameObject go)
        {
            var parts = new Stack<string>();
            var t = go.transform;
            while (t != null)
            {
                parts.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }
    }
}
