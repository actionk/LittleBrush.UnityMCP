using LittleBrushGames.Mcp.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Host
{
    public sealed class McpBridgeWindow : EditorWindow
    {
        [MenuItem("Window/LittleBrushGames/Unity MCP", priority = 2051)]
        public static void ShowWindow()
        {
            var dashboardType = System.Type.GetType(
                "LittleBrushGames.Mcp.Editor.Diagnostics.LbgPluginsWindow, LittleBrushGames.LbgPluginsDashboard.Editor");
            var showDashboard = dashboardType?.GetMethod(
                "ShowMcpTab",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (showDashboard != null)
            {
                showDashboard.Invoke(null, null);
                return;
            }

            var window = GetWindow<McpBridgeWindow>();
            window.Show();
        }

        internal static void ShowGettingStarted()
        {
            McpBridgePanel.RequestGettingStarted();
            ShowWindow();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(
                "LittleBrush Unity MCP", EditorGUIUtility.IconContent("d_NetworkAnimator Icon").image);
            minSize = new Vector2(860f, 520f);
        }

        private void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.Add(new McpBridgePanel());
        }
    }

    [InitializeOnLoad]
    internal static class McpGettingStartedLauncher
    {
        static McpGettingStartedLauncher()
        {
            EditorApplication.delayCall += OpenOnce;
        }

        private static void OpenOnce()
        {
            if (Application.isBatchMode || McpExecuteCodeConsent.HasShownGettingStarted)
                return;

            McpExecuteCodeConsent.SetGettingStartedShown(true);
            McpBridgeWindow.ShowGettingStarted();
        }
    }
}
