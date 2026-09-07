using LittleBrushGames.Mcp.Editor.Host;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace LittleBrushGames.Mcp.Editor.Diagnostics
{
    /// <summary>
    /// Toolbar overlay showing MCP bridge status in the Scene view.
    /// </summary>
    [Overlay(typeof(SceneView), "MCP Status", id = Id, defaultDisplay = false)]
    [Icon("d_NetworkAnimator Icon")]
    public sealed class McpToolbarOverlay : ToolbarOverlay
    {
        public const string Id = "LittleBrushGames/McpToolbar";

        public McpToolbarOverlay() : base(McpStatusButton.Id, McpRestartButton.Id)
        {
            displayed = IsConfiguredVisible;
        }

        [InitializeOnLoadMethod]
        private static void InitializeVisibility()
        {
            EditorApplication.delayCall += ApplyConfiguredVisibility;
        }

        public static void ApplyConfiguredVisibility()
        {
            foreach (SceneView sceneView in SceneView.sceneViews)
            {
                if (sceneView.TryGetOverlay(Id, out var overlay))
                    overlay.displayed = IsConfiguredVisible;
            }
            SceneView.RepaintAll();
        }

        private static bool IsConfiguredVisible
            => McpBridgeSettings.GetOrLoad()?.ShowSceneToolbar ?? false;
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    public sealed class McpRestartButton : EditorToolbarButton
    {
        public const string Id = "LittleBrushGames/McpRestart";

        public McpRestartButton()
        {
            icon = (Texture2D)EditorGUIUtility.IconContent("d_Refresh").image;
            tooltip = "Restart MCP bridge";
            clicked += McpBridgeHost.Restart;
            style.paddingLeft = 4;
            style.paddingRight = 4;
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    public sealed class McpStatusButton : EditorToolbarButton
    {
        public const string Id = "LittleBrushGames/McpStatus";

        private readonly VisualElement _dot;
        private readonly Label _label;
        private double _lastUpdate;

        public McpStatusButton()
        {
            tooltip = "LittleBrush Unity MCP status - click to open";
            clicked += OnClicked;

            // Build UI: [dot] MCP
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.paddingLeft = 4;
            style.paddingRight = 6;

            _dot = new VisualElement();
            _dot.style.width = 8;
            _dot.style.height = 8;
            _dot.style.borderTopLeftRadius = 4;
            _dot.style.borderTopRightRadius = 4;
            _dot.style.borderBottomLeftRadius = 4;
            _dot.style.borderBottomRightRadius = 4;
            _dot.style.marginRight = 4;
            Add(_dot);

            _label = new Label("MCP");
            _label.style.unityFontStyleAndWeight = FontStyle.Normal;
            _label.style.fontSize = 11;
            Add(_label);

            EditorApplication.update += OnUpdate;
            UpdateStatus();
        }

        ~McpStatusButton()
        {
            EditorApplication.update -= OnUpdate;
        }

        private void OnUpdate()
        {
            // Throttle updates to 2x per second
            if (EditorApplication.timeSinceStartup - _lastUpdate < 0.5) return;
            _lastUpdate = EditorApplication.timeSinceStartup;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var running = McpBridgeHost.IsRunning;
            var color = running ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.8f, 0.3f, 0.3f);
            _dot.style.backgroundColor = color;
            tooltip = running
                ? $"LittleBrush Unity MCP running on port {McpBridgeHost.Port}"
                : "LittleBrush Unity MCP stopped - click to open";
        }

        private static void OnClicked()
        {
            McpBridgeWindow.ShowWindow();
        }
    }
}
