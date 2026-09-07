using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace LittleBrushGames.Mcp.Editor.Host
{
    public enum McpToolCallLogMode
    {
        Compact = 0,
        Summary = 1,
        Full = 2,
        Disabled = 3,
    }

    /// <summary>
    /// Project settings for the MCP bridge. Resolved lazily — works with defaults
    /// if no asset exists. The asset is created on demand under
    /// <c>Assets/Settings/McpBridge.asset</c>.
    /// </summary>
    public sealed class McpBridgeSettings : ScriptableObject
    {
        public const string DefaultAssetPath = "Assets/Settings/McpBridge.asset";

        [FormerlySerializedAs("m_Enabled")]
        [SerializeField] private bool m_ServerEnabled = true;
        [SerializeField] private int m_Port = PortResolver.DefaultPort;
        [SerializeField] private bool m_AutoStart = true;
        [SerializeField, Tooltip("Show the MCP status toolbar in Scene views.")]
        private bool m_ShowSceneToolbar;
        [SerializeField] private bool m_DebugLogging;
        [SerializeField, Range(0, 64), Tooltip("Execution records to retain. Zero stops recording without deleting existing history. Statistics remain enabled. Apply & Restart to use changes.")]
        private int m_ExecutionHistoryRetention = 64;
        [SerializeField, Tooltip("Include full code in new execution records. Disable to retain only metadata. Existing stored code is unchanged. Apply & Restart to use changes.")]
        private bool m_RetainExecutionCode = true;
        [SerializeField, Tooltip("Per-call Unity Console logging: Summary shows bounded request arguments, lightweight result counts, and the outcome; Compact adds a bounded result without the JSON-RPC envelope; Full keeps the complete response.")]
        private McpToolCallLogMode m_ToolCallLogMode = McpToolCallLogMode.Summary;
        [SerializeField, Min(256), Tooltip("Maximum compact response characters written to the Unity Console.")]
        private int m_MaxToolCallLogCharacters = 2000;
        [SerializeField, Min(64)] private int m_LogBufferSize = 1000;
        [SerializeField, Min(1)] private int m_DefaultToolTimeoutSeconds = 30;
        [SerializeField, Min(64)] private int m_MaxSceneNodes = 5000;
        [SerializeField, Min(1)] private int m_MaxSceneDepth = 10;
        [SerializeField, Tooltip("Use standalone bridge process for stable connections across domain reloads")]
        private bool m_UseBridge = true;
        [SerializeField] private int m_BridgeUnityPort = 48766;

        public bool Enabled => m_ServerEnabled;
        public int Port => m_Port;
        public bool AutoStart => m_AutoStart;
        public bool ShowSceneToolbar => m_ShowSceneToolbar;
        public bool DebugLogging => m_DebugLogging;
        public int ExecutionHistoryRetention => Mathf.Clamp(m_ExecutionHistoryRetention, 0, 64);
        public bool RetainExecutionCode => m_RetainExecutionCode;
        public McpToolCallLogMode ToolCallLogMode => m_ToolCallLogMode;
        public int MaxToolCallLogCharacters => m_MaxToolCallLogCharacters;
        public bool UseBridge => m_UseBridge;
        public int BridgeUnityPort => m_BridgeUnityPort;
        public int LogBufferSize => m_LogBufferSize;
        public int DefaultToolTimeoutSeconds => m_DefaultToolTimeoutSeconds;
        public int MaxSceneNodes => m_MaxSceneNodes;
        public int MaxSceneDepth => m_MaxSceneDepth;

        private static McpBridgeSettings s_cached;

        public static McpBridgeSettings GetOrLoad()
        {
            if (s_cached != null) return s_cached;
            s_cached = AssetDatabase.LoadAssetAtPath<McpBridgeSettings>(DefaultAssetPath);
            return s_cached;
        }

        public static McpBridgeSettings GetOrCreate()
        {
            var existing = GetOrLoad();
            if (existing != null) return existing;

            var dir = System.IO.Path.GetDirectoryName(DefaultAssetPath);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetFullPath(dir));
                AssetDatabase.Refresh();
            }
            var instance = CreateInstance<McpBridgeSettings>();
            AssetDatabase.CreateAsset(instance, DefaultAssetPath);
            AssetDatabase.SaveAssets();
            s_cached = instance;
            return instance;
        }
    }
}
