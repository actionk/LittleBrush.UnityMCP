using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public static class EditorProviderDiscovery
    {
        public static IReadOnlyList<IToolProvider> Discover()
        {
            var providers = new List<IToolProvider>();
            var attributeType = typeof(McpToolProviderAttribute);
            var providerInterface = typeof(IToolProvider);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                    var details = string.Join("; ", ex.LoaderExceptions
                        .Where(error => error != null)
                        .Take(3)
                        .Select(error => error.Message));
                    UnityEngine.Debug.LogWarning(
                        $"[MCP] partial provider discovery in assembly {assembly.GetName().Name}: {details}");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[MCP] provider discovery skipped assembly {assembly.GetName().Name}: {ex.Message}");
                    continue;
                }

                foreach (var type in types)
                {
                    if (type is { IsClass: true, IsAbstract: false } &&
                        type.GetCustomAttribute(attributeType) != null &&
                        providerInterface.IsAssignableFrom(type))
                    {
                        try { providers.Add((IToolProvider)Activator.CreateInstance(type)); }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogError($"[MCP] failed to instantiate provider {type.FullName}: {ex}");
                        }
                    }
                }
            }

            return providers;
        }
    }
}
