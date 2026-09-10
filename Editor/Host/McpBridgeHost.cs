using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Transport;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Host
{
    [InitializeOnLoad]
    public static class McpBridgeHost
    {
        // Transport - either HttpTransport (direct) or BridgeTransport (via bridge process)
        private static HttpTransport s_httpTransport;
        private static BridgeTransport s_bridgeTransport;
        private static bool s_usingBridge;

        private static ToolDispatcher s_dispatcher;
        private static ToolRegistry s_registry;
        private static MainThreadPump s_pump;
        private static EditorFrameWaiter s_frameWaiter;
        private static JsonRpcRouter s_router;
        internal static Newtonsoft.Json.Linq.JObject WriterStatus => s_router?.WriterStatus();
        private static McpUsageJournal s_usage;
        private static NotificationBroadcaster s_broadcaster;
        private static EditorLogSink s_log;
        private static int s_bindAttempts;
        private const int MaxBindAttempts = 5;

        // Editor state cached on the main-thread tick so background HTTP threads can read it
        // without crashing on EditorApplication.isPlaying / isCompiling main-thread checks.
        // editor.status is the diagnostic-of-last-resort: it MUST work even when the main
        // thread is stalled (modal dialog, frozen reload). All fields it needs come from
        // here so the tool itself can run without hopping to the main thread.
        private static volatile bool s_cachedIsPlaying;
        private static volatile bool s_cachedIsCompiling;
        private static volatile bool s_cachedIsPaused;
        private static volatile bool s_cachedIsUpdating;
        private static volatile string s_cachedActiveScenePath = "";
        private static volatile string s_cachedActiveSceneName = "";
        private static string[] s_registryErrors = System.Array.Empty<string>();

        // Flag set by ConnectionLost callback (background thread) to request restart on main thread.
        // Using update tick instead of delayCall because delayCall requires Unity to be focused.
        private static volatile bool s_needsRestart;

        // Flag to suppress reconnect attempts during domain reload
        private static volatile bool s_reloadInProgress;

        // Restart throttle: prevent infinite restart loops
        private static int s_restartCount;
        private static double s_lastRestartTime;
        private const int MaxRestartsBeforeCooldown = 3;
        private const double RestartCooldownSeconds = 30;

        // Grace period: ignore ConnectionLost shortly after Start succeeds.
        // Unity may do multiple rapid domain reloads; old transports' ReadLoops
        // fire ConnectionLost after the new domain's transport is already connected.
        private static double s_startedAt;
        private const double StartGraceSeconds = 2;

        public static bool IsRunning => s_usingBridge ? s_bridgeTransport?.IsRunning == true : s_httpTransport?.IsRunning == true;
        public static int Port { get; private set; }
        public static bool CachedIsPlaying => s_cachedIsPlaying;
        public static bool CachedIsCompiling => s_cachedIsCompiling;
        public static bool CachedIsPaused => s_cachedIsPaused;
        public static bool CachedIsUpdating => s_cachedIsUpdating;
        public static string CachedActiveScenePath => s_cachedActiveScenePath ?? "";
        public static string CachedActiveSceneName => s_cachedActiveSceneName ?? "";
        public static long MainThreadStalledMs => s_pump?.MillisecondsSinceLastTick ?? 0;
        public static INotificationSink Notifications => s_broadcaster;
        public static IEnumerable<ToolDescriptor> EnumerateTools() => s_registry?.Enumerate() ?? Array.Empty<ToolDescriptor>();
        public static IReadOnlyList<string> RegistryErrors => s_registryErrors;

        /// <summary>Alternate transports use the same gateway, policy, writer queue and logging.</summary>
        public static System.Threading.Tasks.Task<Newtonsoft.Json.Linq.JObject> InvokeGatewayAsync(
            string gateway, Newtonsoft.Json.Linq.JObject arguments, CancellationToken ct, string scope)
        {
            if (s_router == null) throw new InvalidOperationException("The LittleBrush MCP bridge is disabled or not started.");
            if (gateway != JsonRpcRouter.CatalogToolName && gateway != JsonRpcRouter.CallToolName)
                throw new ArgumentException("Only unity.tools and unity.call are gateway methods.", nameof(gateway));
            return s_router.HandleAsync(new Newtonsoft.Json.Linq.JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = Guid.NewGuid().ToString("N"), ["method"] = "tools/call",
                ["params"] = new Newtonsoft.Json.Linq.JObject { ["name"] = gateway, ["arguments"] = arguments },
            }, ct, scope);
        }

        static McpBridgeHost()
        {
            McpEditorActivity.Initialize();
            McpBatchWorker.Initialize();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            McpRuntimeBridge.Changed += OnRuntimeProvidersChanged;
            McpExecuteCodeConsent.StateChanged += OnExecuteCodeConsentChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished;

            // Start immediately from static constructor so the bridge is available
            // even when Unity is not focused. delayCall only fires when Unity pumps
            // its editor loop (requires focus), causing the bridge to stay down
            // until the user switches to the Unity window.
            MaybeAutoStart();
        }

        private static void OnBeforeAssemblyReload()
        {
            // Suppress reconnect attempts from background threads during reload
            s_reloadInProgress = true;

            // For bridge mode: send lifecycle event, then disconnect (bridge stays alive)
            // For direct mode: stop the HTTP server
            if (s_usingBridge)
            {
                s_bridgeTransport?.SendLifecycleEvent("reloading");
                s_bridgeTransport?.Dispose();
                s_bridgeTransport = null;
            }
            else
            {
                Stop();
            }
        }

        private static void OnEditorQuitting()
        {
            // For bridge mode: send shutdown event and kill bridge process
            // For direct mode: stop the HTTP server
            if (s_usingBridge)
            {
                s_bridgeTransport?.SendLifecycleEvent("shutdown");
                s_bridgeTransport?.KillBridge();
                s_bridgeTransport?.Dispose();
                s_bridgeTransport = null;
            }
            else
            {
                Stop();
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            RefreshRuntimeProviders();
        }

        private static void OnCompilationFinished(object _)
        {
            BroadcastToolsListChanged();
        }

        private static void BroadcastToolsListChanged()
        {
            s_broadcaster?.Send("notifications/tools/list_changed");
        }

        private static void MaybeAutoStart()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
            {
                Debug.Log("[MCP] AutoStart skipped in Asset Import Worker process.");
                return;
            }

            var settings = McpBridgeSettings.GetOrLoad();
            if (settings != null && !settings.AutoStart && !McpBatchWorker.IsOwned)
            {
                Debug.Log("[MCP] AutoStart disabled in McpBridgeSettings; use Tools/MCP/Restart Bridge to start.");
                return;
            }
            Start();
        }

        private static void TickEditorState()
        {
            s_cachedIsPlaying = EditorApplication.isPlaying;
            s_cachedIsCompiling = EditorApplication.isCompiling;
            s_cachedIsPaused = EditorApplication.isPaused;
            s_cachedIsUpdating = EditorApplication.isUpdating;
            // Only the active scene; cheap and main-thread-only API.
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            s_cachedActiveScenePath = scene.path ?? "";
            s_cachedActiveSceneName = scene.name ?? "";

            // Check if restart was requested (from background thread via ConnectionLost)
            if (s_needsRestart)
            {
                var now = EditorApplication.timeSinceStartup;

                // Grace period: ignore ConnectionLost shortly after Start succeeds.
                // Unity may do multiple rapid domain reloads; old transports' ReadLoops
                // fire ConnectionLost after the latest reload's transport is already connected.
                // Do NOT clear s_needsRestart here — keep it set so we retry after the grace window.
                if (now - s_startedAt < StartGraceSeconds)
                    return;

                s_needsRestart = false;

                if (!IsRunning)
                {
                    if (now - s_lastRestartTime > RestartCooldownSeconds)
                        s_restartCount = 0;

                    s_restartCount++;
                    if (s_restartCount > MaxRestartsBeforeCooldown)
                    {
                        Debug.LogWarning($"[MCP] Too many restarts ({s_restartCount}), waiting {RestartCooldownSeconds}s before retrying.");
                        return;
                    }

                    s_lastRestartTime = now;
                    Restart();
                }
            }

            // After cooldown expires, re-arm auto-restart so the bridge recovers
            // without requiring a manual click. Without this, hitting MaxRestartsBeforeCooldown
            // leaves the bridge stopped permanently since nothing sets s_needsRestart again.
            if (!IsRunning && !s_needsRestart && s_restartCount > 0 && !s_reloadInProgress)
            {
                var now = EditorApplication.timeSinceStartup;
                if (now - s_lastRestartTime > RestartCooldownSeconds)
                {
                    s_restartCount = 0;
                    s_needsRestart = true;
                }
            }
        }

        public static void Start()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            // Clear reload flag - we're starting fresh after domain reload
            s_reloadInProgress = false;

            if (IsRunning) return;

            var settings = McpBridgeSettings.GetOrLoad();
            if (settings != null && !settings.Enabled)
            {
                Debug.Log("[MCP] disabled via McpBridgeSettings; not starting.");
                return;
            }

            try
            {
                s_log = new EditorLogSink();
                TickEditorState();
                EditorApplication.update += TickEditorState;
                s_pump = new MainThreadPump(autoTick: true);
                s_frameWaiter = new EditorFrameWaiter();
                s_registry = new ToolRegistry();
                s_registry.SetEditorProviders(EditorProviderDiscovery.Discover().ToList());
                s_registry.SetRuntimeProviders(McpRuntimeBridge.Providers);
                UpdateRegistryDiagnostics();
                s_broadcaster = new NotificationBroadcaster(s_log);
                var defaultTimeout = settings != null
                    ? TimeSpan.FromSeconds(settings.DefaultToolTimeoutSeconds)
                    : TimeSpan.FromSeconds(30);
                s_dispatcher = new ToolDispatcher(
                    s_registry,
                    s_pump,
                    s_frameWaiter,
                    s_log,
                    () => (s_cachedIsPlaying, s_cachedIsCompiling),
                    s_broadcaster,
                    defaultTimeout,
                    new McpTrustAuthorizer(s_pump, s_frameWaiter));
                s_usage = new McpUsageJournal(
                    Path.Combine(Application.dataPath, "..", "Logs", "McpUsage"),
                    message => s_log?.Log(LogLevel.Warn, message),
                    executionRetention: settings != null ? settings.ExecutionHistoryRetention : 64,
                    retainCode: settings == null || settings.RetainExecutionCode);
                s_router = new JsonRpcRouter(
                    s_registry,
                    s_dispatcher,
                    () => (s_cachedIsPlaying, s_cachedIsCompiling),
                    s_log,
                    settings?.ToolCallLogMode ?? McpToolCallLogMode.Summary,
                    settings != null && settings.MaxToolCallLogCharacters > 0
                        ? settings.MaxToolCallLogCharacters
                        : McpToolCallLogger.DefaultMaxCompactResponseCharacters,
                    recordUsage: s_usage.Record);
                Port = PortResolver.Resolve();

                // Decide which transport to use
                var useBridge = settings?.UseBridge ?? true;
                var bridgeExists = BridgeExecutableExists();

                if (useBridge && bridgeExists)
                {
                    var bridgePort = Port;
                    var unityPort = PortResolver.ResolveUnityPort();
                    s_bridgeTransport = new BridgeTransport(s_router, s_log, bridgePort, unityPort);
                    s_bridgeTransport.ConnectionLost += OnBridgeConnectionLost;
                    // Forward notifications from broadcaster to the Go bridge so it can
                    // fan out to SSE clients. Without this, tools/list_changed is lost in bridge mode.
                    s_broadcaster.NotificationSent += OnBroadcasterNotification;
                    s_bridgeTransport.Start();
                    s_usingBridge = true;
                    s_log.Log(LogLevel.Info, $"bridge ready on :{bridgePort}");
                }
                else
                {
                    if (useBridge && !bridgeExists)
                    {
                    Debug.LogWarning($"[MCP] Bridge mode enabled but mcp-bridge executable not found. Falling back to direct HTTP mode. Build with: cd Bridge~ && go build -o {BridgeExecutableNames()[0]} .");
                    }
                    s_httpTransport = new HttpTransport(Port, s_router, s_broadcaster, s_log);
                    s_httpTransport.Start();
                    s_usingBridge = false;
                }

                s_bindAttempts = 0;
                s_restartCount = 0; // Successful start resets throttle
                s_startedAt = EditorApplication.timeSinceStartup;
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException || ex is System.Net.Sockets.SocketException)
            {
                Stop();
                s_bindAttempts++;
                if (s_bindAttempts < MaxBindAttempts)
                {
                Debug.LogWarning($"[MCP] bind failed (attempt {s_bindAttempts}): {ex.Message}. Retrying...");
                    EditorApplication.delayCall += Start;
                }
                else
                {
                Debug.LogError($"[MCP] bind failed {s_bindAttempts} times, giving up: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Stop();
                Debug.LogError($"[MCP] failed to start: {ex}");
            }
        }

        private static bool BridgeExecutableExists()
            => FindBridgeExecutable() != null;

        internal static string FindBridgeExecutable()
        {
            var bridgeDir = Path.Combine(Application.dataPath, "Plugins", "LittleBrushGames", "Mcp", "Bridge~");
            foreach (var fileName in BridgeExecutableNames())
            {
                var path = Path.Combine(bridgeDir, fileName);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private static string[] BridgeExecutableNames()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new[] { "mcp-bridge.exe", "mcp-bridge" };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? new[] { "mcp-bridge-darwin-arm64", "mcp-bridge" }
                    : new[] { "mcp-bridge-darwin-amd64", "mcp-bridge" };
            return new[] { "mcp-bridge-linux", "mcp-bridge" };
        }

        public static void Stop()
        {
            s_needsRestart = false; // Cancel any pending auto-restart
            try { EditorApplication.update -= TickEditorState; } catch { }
            try { s_httpTransport?.Dispose(); } catch { }
            if (s_bridgeTransport != null)
            {
                try { s_bridgeTransport.ConnectionLost -= OnBridgeConnectionLost; } catch { }
                try { s_bridgeTransport.Dispose(); } catch { }
            }
            if (s_broadcaster != null)
            {
                try { s_broadcaster.NotificationSent -= OnBroadcasterNotification; } catch { }
            }
            try { s_pump?.Dispose(); } catch { }
            try { s_frameWaiter?.Dispose(); } catch { }
            s_httpTransport = null;
            s_bridgeTransport = null;
            s_pump = null;
            s_frameWaiter = null;
            s_dispatcher = null;
            s_registry = null;
            s_registryErrors = System.Array.Empty<string>();
            s_router = null;
            s_usage?.Dispose();
            s_usage = null;
            s_broadcaster = null;
            s_usingBridge = false;
        }

        public static void Restart() { Stop(); Start(); }

        private static void OnBroadcasterNotification(Newtonsoft.Json.Linq.JObject notification)
        {
            try { s_bridgeTransport?.SendNotification(notification); } catch { }
        }

        private static void OnBridgeConnectionLost()
        {
            // Ignore if domain reload is in progress - Start() will handle reconnection
            if (s_reloadInProgress) return;

            // Set flag for main thread to restart on next update tick.
            // Don't use delayCall - it only fires when Unity is focused.
            s_needsRestart = true;
        }

        private static void OnRuntimeProvidersChanged()
        {
            RefreshRuntimeProviders();
        }

        private static void OnExecuteCodeConsentChanged()
        {
            RefreshEditorProviders();
        }

        private static void RefreshEditorProviders()
        {
            if (s_registry == null) return;
            try
            {
                s_registry.SetEditorProviders(EditorProviderDiscovery.Discover().ToList());
                UpdateRegistryDiagnostics();
                BroadcastToolsListChanged();
            }
            catch (Exception ex)
            {
                s_registryErrors = new[] { $"Registry refresh failed: {ex.Message}" };
                s_log?.Log(LogLevel.Error, s_registryErrors[0], ex);
            }
        }

        private static void RefreshRuntimeProviders()
        {
            if (s_registry == null) return;
            try
            {
                s_registry.SetRuntimeProviders(McpRuntimeBridge.Providers);
                UpdateRegistryDiagnostics();
                BroadcastToolsListChanged();
            }
            catch (System.Exception ex)
            {
                s_registryErrors = new[] { $"Registry refresh failed: {ex.Message}" };
                s_log?.Log(LogLevel.Error, s_registryErrors[0], ex);
            }
        }

        private static void UpdateRegistryDiagnostics()
        {
            s_registryErrors = s_registry?.Errors.ToArray() ?? System.Array.Empty<string>();
            foreach (var error in s_registryErrors)
                s_log?.Log(LogLevel.Error, $"MCP provider rejected: {error}");
        }
    }

    /// <summary>
    /// Best-effort, session-scoped editor activity signal used to avoid surprising a
    /// human who is currently working in Unity. Unity does not expose a public
    /// global input event, so this combines focus, GUI control changes, SceneView
    /// input, undo/redo, and Play Mode transitions.
    /// </summary>
    public static class McpEditorActivity
    {
        public const long RecentActivityWindowMs = 30_000;

        private const string LastActivityMonotonicKey = "LittleBrushGames.Mcp.EditorActivity.LastMonotonicMs";
        private const string LastActivityUtcKey = "LittleBrushGames.Mcp.EditorActivity.LastUtcMs";
        private const string LastActivitySourceKey = "LittleBrushGames.Mcp.EditorActivity.LastSource";

        private static bool s_initialized;
        private static volatile bool s_editorFocused;
        private static long s_lastActivityMonotonicMs;
        private static long s_lastActivityUtcMs;
        private static long s_lastPersistedMonotonicMs;
        private static volatile string s_lastActivitySource = "unknown";
        private static string s_lastPersistedSource = "";
        private static bool s_guiStateInitialized;
        private static int s_lastKeyboardControl;
        private static int s_lastHotControl;
        private static bool s_lastEditingTextField;

        static McpEditorActivity() => Initialize();

        public static bool IsEditorFocused => s_editorFocused;
        public static long LastActivityAtUnixMs => Interlocked.Read(ref s_lastActivityUtcMs);
        public static string LastActivitySource => s_lastActivitySource;

        /// <summary>Cross-thread-safe idle value for editor.status.</summary>
        public static long IdleForMs
        {
            get
            {
                var last = LastActivityAtUnixMs;
                if (last <= 0) return -1;
                var idle = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last;
                return idle < 0 ? 0 : idle;
            }
        }

        /// <summary>Main-thread monotonic idle value for test preflight.</summary>
        public static long IdleForMsNow
        {
            get
            {
                var last = Interlocked.Read(ref s_lastActivityMonotonicMs);
                if (last <= 0) return -1;
                var idle = NowMonotonicMs() - last;
                return idle < 0 ? 0 : idle;
            }
        }

        public static bool HasRecentFocusedActivity(bool editorFocused, long idleForMs)
            => editorFocused && idleForMs >= 0 && idleForMs < RecentActivityWindowMs;

        public static void Initialize()
        {
            if (s_initialized) return;
            s_initialized = true;
            s_editorFocused = EditorApplication.isFocused;

            RestoreActivity();
            EditorApplication.focusChanged -= OnFocusChanged;
            EditorApplication.focusChanged += OnFocusChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        public static void Tick()
        {
            if (!s_initialized) Initialize();

            var focused = EditorApplication.isFocused;
            if (focused != s_editorFocused)
                OnFocusChanged(focused);

            if (!focused) return;

            try
            {
                var keyboardControl = GUIUtility.keyboardControl;
                var hotControl = GUIUtility.hotControl;
                var editingTextField = EditorGUIUtility.editingTextField;
                if (!s_guiStateInitialized)
                {
                    s_guiStateInitialized = true;
                    s_lastKeyboardControl = keyboardControl;
                    s_lastHotControl = hotControl;
                    s_lastEditingTextField = editingTextField;
                }
                else if (keyboardControl != s_lastKeyboardControl
                         || hotControl != s_lastHotControl
                         || editingTextField != s_lastEditingTextField)
                {
                    MarkActivity("gui_input");
                    s_lastKeyboardControl = keyboardControl;
                    s_lastHotControl = hotControl;
                    s_lastEditingTextField = editingTextField;
                }
            }
            catch
            {
                // GUI state is not guaranteed to be readable outside an IMGUI pass.
            }
        }

        private static void OnFocusChanged(bool focused)
        {
            s_editorFocused = focused;
            s_guiStateInitialized = false;
        }

        private static void OnSceneGui(SceneView _)
        {
            if (!s_editorFocused || Event.current == null) return;

            switch (Event.current.type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseDrag:
                case EventType.ScrollWheel:
                case EventType.KeyDown:
                case EventType.KeyUp:
                case EventType.ContextClick:
                case EventType.DragUpdated:
                case EventType.DragPerform:
                case EventType.DragExited:
                case EventType.ValidateCommand:
                case EventType.ExecuteCommand:
                    MarkActivity("scene_view_input");
                    break;
            }
        }

        private static void OnUndoRedo() => MarkActivity("undo_redo");
        private static void OnPlayModeChanged(PlayModeStateChange _) => MarkActivity("play_mode");

        private static void RestoreActivity()
        {
            try
            {
                var persistedMonotonic = ParseLong(SessionState.GetString(LastActivityMonotonicKey, ""));
                var persistedUtc = ParseLong(SessionState.GetString(LastActivityUtcKey, ""));
                if (persistedMonotonic > 0 && persistedMonotonic <= NowMonotonicMs())
                {
                    Interlocked.Exchange(ref s_lastActivityMonotonicMs, persistedMonotonic);
                    Interlocked.Exchange(ref s_lastActivityUtcMs, persistedUtc);
                    s_lastPersistedMonotonicMs = persistedMonotonic;
                    s_lastPersistedSource = SessionState.GetString(LastActivitySourceKey, "");
                    s_lastActivitySource = s_lastPersistedSource;
                }
            }
            catch
            {
                // SessionState is only a convenience; the live signal still works.
            }
        }

        private static void MarkActivity(string source)
        {
            var monotonic = NowMonotonicMs();
            var utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.Exchange(ref s_lastActivityMonotonicMs, monotonic);
            Interlocked.Exchange(ref s_lastActivityUtcMs, utc);
            s_lastActivitySource = source;

            if (monotonic - s_lastPersistedMonotonicMs < 250
                && string.Equals(source, s_lastPersistedSource, StringComparison.Ordinal))
                return;

            try
            {
                SessionState.SetString(LastActivityMonotonicKey, monotonic.ToString());
                SessionState.SetString(LastActivityUtcKey, utc.ToString());
                SessionState.SetString(LastActivitySourceKey, source);
                s_lastPersistedMonotonicMs = monotonic;
                s_lastPersistedSource = source;
            }
            catch
            {
                // SessionState failure must not block editor tools.
            }
        }

        private static long ParseLong(string value) => long.TryParse(value, out var result) ? result : 0;

        private static long NowMonotonicMs() => (long)(EditorApplication.timeSinceStartup * 1000.0);
    }
}
