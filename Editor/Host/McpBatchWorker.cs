using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Editor.Host
{
    // Initialized by the host on Unity's main thread; readers use cached environment values.
    internal static class McpBatchWorker
    {
        internal static bool IsBatchMode { get; private set; }
        internal static bool HasGraphics { get; private set; } = true;
        internal static bool IsOwned { get; private set; }
        private static int s_active;
        private static readonly object s_lifecycleGate = new();
        private static bool s_stopping;
        private static long s_lastActivity = Stopwatch.GetTimestamp();
        private static double s_idleSeconds = 300;

        internal static void Initialize()
        {
            IsBatchMode = Application.isBatchMode;
            HasGraphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            var args = Environment.GetCommandLineArgs();
            IsOwned = IsBatchMode && args.Contains("-littlebrushMcpWorker") && !AssetDatabase.IsAssetImportWorkerProcess();
            var index = Array.IndexOf(args, "-littlebrushMcpIdleSeconds");
            if (index >= 0 && index + 1 < args.Length && double.TryParse(args[index + 1],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 1 && !double.IsInfinity(seconds))
                s_idleSeconds = seconds;
            if (IsOwned) EditorApplication.update += Tick;
        }

        internal static string UnavailableReason(ToolDescriptor tool)
            => EnvironmentUnavailableReason(tool, IsBatchMode, HasGraphics);

        internal static string EnvironmentUnavailableReason(ToolDescriptor tool, bool batch, bool graphics)
            => tool.RequiresInteractiveEditor && batch ? "interactive_editor_required"
                : tool.RequiresGraphics && !graphics ? "graphics_device_required" : null;

        internal static IDisposable TrackActivity() => new Activity();

        private sealed class Activity : IDisposable
        {
            private int _disposed;
            public Activity()
            {
                lock (s_lifecycleGate)
                {
                    if (s_stopping)
                        throw new McpToolException(McpErrorCodes.ToolUnavailable, "Managed worker is shutting down; retry after it exits.");
                    Interlocked.Increment(ref s_active);
                    Touch();
                }
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Touch();
                Interlocked.Decrement(ref s_active);
            }
        }

        private static void Touch() => Interlocked.Exchange(ref s_lastActivity, Stopwatch.GetTimestamp());

        private static void Tick()
        {
            if (Volatile.Read(ref s_active) != 0 || EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode) { Touch(); return; }
            // Never discard a user's unsaved scene or a provider's deferred work.
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) { Touch(); return; }
            try
            {
                if (McpBridgeHost.EnumerateTools().Any(tool => tool.BackgroundOperationActive?.Invoke() == true))
                { Touch(); return; }
            }
            catch (Exception) { Touch(); return; } // An uncertain busy probe must not terminate work.
            // Admission and shutdown must be atomic: a background request can arrive
            // between the busy probes above and this final idle check.
            lock (s_lifecycleGate)
            {
                if (Volatile.Read(ref s_active) != 0
                    || (Stopwatch.GetTimestamp() - Interlocked.Read(ref s_lastActivity)) / (double)Stopwatch.Frequency < s_idleSeconds) return;
                s_stopping = true;
            }
            UnityEngine.Debug.Log("[MCP] Managed worker idle; exiting.");
            EditorApplication.Exit(0);
        }
    }
}
