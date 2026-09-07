using LittleBrushGames.Mcp.Editor.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Host
{
    /// <summary>
    /// Project Settings entry under <c>LittleBrushGames → MCP</c>. Lazily creates the
    /// settings asset on first edit so the plugin works with defaults out of the box.
    /// </summary>
    public static class McpSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/LittleBrushGames/Unity MCP", SettingsScope.Project)
            {
                label = "LittleBrush Unity MCP",
                guiHandler = _ => DrawGui(),
                keywords = new[] { "MCP", "LittleBrushGames", "bridge", "Claude", "Codex", "tool" },
            };
        }

        internal static void DrawGui()
        {
            var settings = McpBridgeSettings.GetOrLoad();
            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "No McpBridgeSettings asset exists. Defaults are in effect. " +
                    "Click \"Create asset\" to override.",
                    MessageType.Info);
                if (GUILayout.Button("Create asset"))
                {
                    McpBridgeSettings.GetOrCreate();
                    Selection.activeObject = McpBridgeSettings.GetOrLoad();
                }
                EditorGUILayout.Space();
                DrawStatus();
                return;
            }

            var so = new SerializedObject(settings);
            so.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Settings asset", settings, typeof(McpBridgeSettings), false);

            DrawProp(so, "m_ServerEnabled", "Enabled");
            DrawProp(so, "m_AutoStart", "Auto-start");
            DrawProp(so, "m_ShowSceneToolbar", "Show Scene toolbar");
            DrawProp(so, "m_Port", "Port");
            DrawProp(so, "m_UseBridge", "Use standalone bridge");
            DrawProp(so, "m_BridgeUnityPort", "Unity transport port");
            DrawProp(so, "m_DebugLogging", "Debug logging");
            DrawProp(so, "m_ToolCallLogMode", "Tool-call logging");
            DrawProp(so, "m_MaxToolCallLogCharacters", "Max compact response characters");
            DrawProp(so, "m_LogBufferSize", "Log buffer size");
            DrawProp(so, "m_DefaultToolTimeoutSeconds", "Default tool timeout (s)");
            DrawProp(so, "m_MaxSceneNodes", "Max scene nodes");
            DrawProp(so, "m_MaxSceneDepth", "Max scene depth");
            if (so.ApplyModifiedProperties())
                McpToolbarOverlay.ApplyConfiguredVisibility();

            EditorGUILayout.Space();
            DrawStatus();
            EditorGUILayout.Space();
            if (GUILayout.Button("Restart bridge")) McpBridgeHost.Restart();
            if (GUILayout.Button("Copy .mcp.json snippet"))
            {
                EditorGUIUtility.systemCopyBuffer = McpClientConfigUtility.BuildClaudeCodeConfig(McpBridgeHost.Port);
            }

            if (GUILayout.Button("Copy Codex snippet"))
            {
                EditorGUIUtility.systemCopyBuffer = McpClientConfigUtility.BuildCodexBlock(McpBridgeHost.Port);
            }
        }

        private static void DrawProp(SerializedObject so, string field, string label)
        {
            var prop = so.FindProperty(field);
            if (prop != null) EditorGUILayout.PropertyField(prop, new GUIContent(label));
        }

        private static void DrawStatus()
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Running", McpBridgeHost.IsRunning ? "yes" : "no");
            EditorGUILayout.LabelField("Port", McpBridgeHost.Port.ToString());
        }
    }
}
