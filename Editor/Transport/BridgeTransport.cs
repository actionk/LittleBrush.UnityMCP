using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Transport
{
    /// <summary>
    /// Connects to the MCP bridge process as a backend.
    /// The bridge handles HTTP clients; Unity handles tool dispatch.
    /// </summary>
    public sealed class BridgeTransport : IDisposable
    {
        #region Windows P/Invoke for process creation with job breakaway

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars, dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint DETACHED_PROCESS = 0x00000008;
        private const string BridgeBufferTimeout = "60s";
        private const string BridgeRequestTimeout = "360s";

        #endregion

        private readonly JsonRpcRouter _router;
        private readonly ILogSink _log;
        private readonly int _bridgePort;
        private readonly int _unityPort;

        private CancellationTokenSource _cts = new();
        private TcpClient _client;
        private StreamWriter _writer;
        private StreamReader _reader;
        private Thread _readThread;
        private volatile bool _disposed;
        private int _bridgeProcessId = -1;
        private string _bridgeExecutablePath;

        // Separate lock object — don't lock on _writer since it can be nulled during Dispose.
        private readonly object _sendLock = new();

        // Per-bridge-request CTS so the bridge can cancel in-flight tool handlers
        // when the HTTP client disconnects or the bridge's own timeout fires. Keyed
        // by the bridge's internal request id (not the client's JSON-RPC id), so
        // collisions between concurrent clients are impossible.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();

        // Tracks running HandleRequest tasks so Dispose can drain briefly before
        // the AppDomain tears down. Without this, a tool handler mid-flight can
        // keep executing into the next domain, touch Unity APIs off-thread, and
        // write responses to a socket the new domain already owns.
        private int _inFlightCount;

        public bool IsRunning { get; private set; }

        /// <summary>
        /// Fired when connection to bridge is lost unexpectedly.
        /// Subscribe to trigger auto-restart.
        /// </summary>
        public event Action ConnectionLost;

        public BridgeTransport(JsonRpcRouter router, ILogSink log, int bridgePort = 48765, int unityPort = 48766)
        {
            _router = router;
            _log = log;
            _bridgePort = bridgePort;
            _unityPort = unityPort;
        }

        public void Start()
        {
            if (IsRunning) return;

            EnsureBridgeRunning();
            ConnectToBridge();

            IsRunning = true;
        }

        private void EnsureBridgeRunning()
        {
            // Check if bridge is already running and healthy
            if (IsBridgeHealthy())
            {
                return;
            }

            // Find bridge executable
            var bridgePath = FindBridgeExecutable();
            if (bridgePath == null)
            {
                throw new FileNotFoundException(
                    $"MCP bridge executable not found. Build it with: cd Bridge~ && go build -o {BridgeExecutableNames()[0]} .");
            }
            _bridgeExecutablePath = bridgePath;

            // Kill any stale bridge process for this project that isn't responding.
            KillBridge();

            _log.Log(LogLevel.Info, $"Starting MCP bridge: {bridgePath}");

            StartBridgeProcess(bridgePath);

            // Wait for bridge to be ready
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < timeout)
            {
                Thread.Sleep(100);
                if (IsBridgeHealthy())
                {
                    _log.Log(LogLevel.Info, $"MCP bridge started (PID: {_bridgeProcessId})");
                    return;
                }
            }

            // Include a tail of the bridge log in the exception so callers don't
            // have to go spelunking to find out why startup failed.
            var logTail = TailBridgeLog(Path.GetDirectoryName(bridgePath), 2048);
            var msg = "MCP bridge failed to start within 5 seconds";
            if (!string.IsNullOrEmpty(logTail))
                msg += $". Last log output:\n{logTail}";
            throw new TimeoutException(msg);
        }

        private static string TailBridgeLog(string bridgeDir, int maxBytes)
        {
            try
            {
                var logPath = Path.Combine(bridgeDir, "mcp-bridge.log");
                if (!File.Exists(logPath)) return null;
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var len = fs.Length;
                var start = Math.Max(0, len - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[len - start];
                var read = fs.Read(buf, 0, buf.Length);
                return Encoding.UTF8.GetString(buf, 0, read);
            }
            catch
            {
                return null;
            }
        }

        private void StartBridgeProcess(string bridgePath)
        {
            var args = $"--port {_bridgePort} --unity-port {_unityPort} --buffer-timeout {BridgeBufferTimeout} --request-timeout {BridgeRequestTimeout}";

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = bridgePath,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(bridgePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }) ?? throw new InvalidOperationException("Process.Start returned null for MCP bridge.");
                _bridgeProcessId = proc.Id;
                proc.Dispose();
                _log.Log(LogLevel.Debug, $"Process.Start succeeded, PID: {_bridgeProcessId}");
                return;
            }

            // Windows path uses CREATE_BREAKAWAY_FROM_JOB so the bridge survives domain reload.
            var commandLine = $"\"{bridgePath}\" {args}";
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var flags = CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW | DETACHED_PROCESS;

            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, null, ref si, out var pi))
            {
                var error = Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(error, $"Failed to start MCP bridge (error {error})");
            }

            _bridgeProcessId = pi.dwProcessId;
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            _log.Log(LogLevel.Debug, $"CreateProcess succeeded, PID: {pi.dwProcessId}");
        }

        private bool IsBridgeHealthy()
        {
            var health = GetBridgeHealth();
            if (health == null)
                return false;

            ValidateBridgeOwner(health);
            return true;
        }

        private void ValidateBridgeOwner(JObject health)
        {
            if (!HealthMatchesCurrentProject(health))
                throw new InvalidOperationException(
                    $"MCP bridge on :{_bridgePort} belongs to {health["projectPath"]}; " +
                    "choose separate MCP HTTP and Unity transport ports. The existing bridge was left running.");
        }

        private JObject GetBridgeHealth()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = client.GetAsync($"http://127.0.0.1:{_bridgePort}/mcp/health").Result;
                if (!response.IsSuccessStatusCode)
                    return null;
                return JObject.Parse(response.Content.ReadAsStringAsync().Result);
            }
            catch
            {
                return null;
            }
        }

        private static bool HealthMatchesCurrentProject(JObject health)
        {
            var projectHash = health["projectHash"]?.ToString();
            var projectPath = health["projectPath"]?.ToString();
            if (string.IsNullOrEmpty(projectHash) && string.IsNullOrEmpty(projectPath))
                return true;
            if (!string.IsNullOrEmpty(projectHash) && string.Equals(projectHash, ComputeProjectHash(), StringComparison.OrdinalIgnoreCase))
                return true;
            return PathsEqual(projectPath, Application.dataPath);
        }

        private string FindBridgeExecutable()
        {
            // Look in Bridge~ folder relative to this assembly
            var assemblyPath = typeof(BridgeTransport).Assembly.Location;
            var mcpRoot = Path.GetDirectoryName(Path.GetDirectoryName(assemblyPath));

            var roots = new[]
            {
                Path.Combine(mcpRoot, "Bridge~"),
                Path.Combine(Application.dataPath, "Plugins", "LittleBrushGames", "Mcp", "Bridge~"),
            };

            foreach (var root in roots)
                foreach (var fileName in BridgeExecutableNames())
                {
                    var path = Path.Combine(root, fileName);
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

        private void ConnectToBridge()
        {
            _client = new TcpClient();

            // Bounded connect: the default Windows Connect can block ~21s on SYN
            // retransmit if the bridge isn't listening yet. Give up early so the
            // Editor's Start() caller can retry cleanly.
            var connectTask = _client.ConnectAsync("127.0.0.1", _unityPort);
            if (!connectTask.Wait(TimeSpan.FromSeconds(5)))
            {
                try { _client.Close(); } catch { }
                throw new TimeoutException($"Failed to connect to MCP bridge at 127.0.0.1:{_unityPort} within 5 seconds");
            }

            // Enable TCP keepalive to prevent idle disconnects
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            var stream = _client.GetStream();
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _reader = new StreamReader(stream, Encoding.UTF8);

            // Send hello
            var hello = new JObject
            {
                ["type"] = "hello",
                ["version"] = "1.0",
                ["projectPath"] = Application.dataPath,
                ["projectHash"] = ComputeProjectHash(),
                ["processId"] = Process.GetCurrentProcess().Id,
                ["reloadSafeTools"] = new JArray(_router.ReloadSafeToolNames())
            };
            _writer.WriteLine(hello.ToString(Formatting.None));

            // Read welcome with a socket-level timeout. Previous implementation used
            // Task.Run + Task.Wait(5s), but if the wait fired the underlying ReadLine
            // stayed parked on the thread-pool thread forever — leaking one worker
            // per failed handshake. ReceiveTimeout makes the blocking read itself
            // give up by throwing IOException.
            var socket = _client.Client;
            var prevReceiveTimeout = socket.ReceiveTimeout;
            socket.ReceiveTimeout = 5000;
            string welcomeJson;
            try
            {
                welcomeJson = _reader.ReadLine();
            }
            catch (IOException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut)
            {
                throw new TimeoutException("Bridge did not respond to hello within 5 seconds", ex);
            }
            finally
            {
                socket.ReceiveTimeout = prevReceiveTimeout; // clear for the long-running ReadLoop
            }

            if (string.IsNullOrEmpty(welcomeJson))
            {
                throw new IOException("Bridge closed connection after hello");
            }

            var welcome = JObject.Parse(welcomeJson);
            if (welcome["type"]?.ToString() == "error")
            {
                throw new Exception($"Bridge rejected connection: {welcome["message"]}");
            }

            // Start read loop
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "McpBridgeRead" };
            _readThread.Start();
        }

        private static string ComputeProjectHash()
        {
            // string.GetHashCode() is randomized per-process in modern .NET, so the
            // old implementation produced a different hash on every Unity start and
            // defeated any "same project" correlation on the bridge side. Use a
            // stable MD5 prefix of the UTF-8 path bytes instead.
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(Application.dataPath));
            var sb = new StringBuilder(8);
            for (var i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        private void ReadLoop()
        {
            var myCts = _cts;
            var myClient = _client;
            var myReader = _reader;

            try
            {
                while (!myCts.IsCancellationRequested && myClient?.Connected == true)
                {
                    string line;
                    try
                    {
                        line = myReader.ReadLine();
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (line == null)
                        break;

                    try
                    {
                        var msg = JObject.Parse(line);
                        HandleBridgeMessage(msg);
                    }
                    catch (Exception ex)
                    {
                        _log.Log(LogLevel.Error, $"Failed to handle bridge message: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Error, $"Bridge read loop error: {ex.Message}");
            }
            finally
            {
                IsRunning = false;

                if (!_disposed)
                    ConnectionLost?.Invoke();
            }
        }

        private void HandleBridgeMessage(JObject msg)
        {
            var type = msg["type"]?.ToString();

            switch (type)
            {
                case "request":
                    HandleRequest(msg);
                    break;

                case "cancel":
                    HandleCancel(msg);
                    break;

                case "welcome":
                    // Already handled in ConnectToBridge
                    break;

                default:
                    _log.Log(LogLevel.Warn, $"Unknown bridge message type: {type}");
                    break;
            }
        }

        private void HandleCancel(JObject msg)
        {
            var id = msg["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) return;
            if (_inFlight.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        private void HandleRequest(JObject msg)
        {
            if (_disposed) return;
            var id = msg["id"]?.ToString();
            var payload = msg["payload"] as JObject;

            if (string.IsNullOrEmpty(id) || payload == null)
            {
                _log.Log(LogLevel.Warn, "Invalid request from bridge: missing id or payload");
                return;
            }

            // Linked CTS so a bridge cancel message OR a transport Dispose both
            // cancel the tool handler. Stored by bridge reqID so HandleCancel can
            // find it.
            var reqCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _inFlight[id] = reqCts;
            Interlocked.Increment(ref _inFlightCount);

            Task.Run(async () =>
            {
                try
                {
                    var clientSession = payload["params"]?["_meta"]?["bridgeSessionId"]?.ToString();
                    var response = await _router.HandleAsync(payload, reqCts.Token, clientSession ?? id);

                    var responseMsg = new JObject
                    {
                        ["type"] = "response",
                        ["id"] = id,
                        ["payload"] = response ?? new JObject()
                    };

                    SendToBridge(responseMsg);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled (either by bridge cancel or Dispose). No response
                    // needed — the bridge already evicted the pending entry.
                }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Error, $"Request {id} failed: {ex.Message}");

                    var errorResponse = new JObject
                    {
                        ["type"] = "response",
                        ["id"] = id,
                        ["payload"] = JsonRpcEnvelope.Error(null, McpErrorCodes.InternalError, ex.Message)
                    };
                    SendToBridge(errorResponse);
                }
                finally
                {
                    _inFlight.TryRemove(id, out _);
                    try { reqCts.Dispose(); } catch { }
                    Interlocked.Decrement(ref _inFlightCount);
                }
            });
        }

        private void SendToBridge(JObject msg)
        {
            try
            {
                lock (_sendLock)
                {
                    var writer = _writer;
                    // During shutdown, let already completed responses drain before
                    // closing the socket. New work and notifications stay disabled.
                    if (writer == null || (_disposed && (string)msg["type"] != "response")) return;
                    writer.WriteLine(msg.ToString(Formatting.None));
                }
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Error, $"Failed to send to bridge: {ex.Message}");
                // A failed write means the socket is dead — the read loop may not
                // have noticed yet. Mark the transport as down and request restart
                // so subsequent responses don't silently disappear.
                IsRunning = false;
                if (!_disposed)
                    ConnectionLost?.Invoke();
            }
        }

        /// <summary>
        /// Forward a JSON-RPC notification to the bridge for fan-out to SSE clients.
        /// </summary>
        public void SendNotification(JObject notification)
        {
            if (!IsRunning || _disposed) return;

            var msg = new JObject
            {
                ["type"] = "notification",
                ["payload"] = notification,
            };
            SendToBridge(msg);
        }

        /// <summary>
        /// Send lifecycle event to bridge (e.g., before domain reload).
        /// </summary>
        public void SendLifecycleEvent(string eventName)
        {
            if (!IsRunning) return;

            var msg = new JObject
            {
                ["type"] = "lifecycle",
                ["event"] = eventName
            };
            SendToBridge(msg);
        }

        public void Dispose()
        {
            _disposed = true; // Prevent ConnectionLost event
            IsRunning = false;

            // Cancel the shared CTS first — linked per-request CTSs inherit the
            // cancel so every in-flight tool handler sees OperationCanceled.
            try { _cts?.Cancel(); } catch { }

            // Brief drain so async continuations complete before the AppDomain
            // goes away. Without this, a mid-flight handler keeps running into
            // the next domain, potentially touching Unity APIs off-thread and
            // writing to a socket the new domain now owns. Bounded to 500ms —
            // enough for cooperative cancellation to unwind, short enough to
            // not noticeably delay domain reload.
            var deadline = DateTime.UtcNow.AddMilliseconds(500);
            var sw = new SpinWait();
            while (Volatile.Read(ref _inFlightCount) > 0 && DateTime.UtcNow < deadline)
            {
                sw.SpinOnce();
            }

            // Always close socket even if IsRunning was already false
            // (ReadLoop may have set it false, but socket might still be open)
            lock (_sendLock)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
            try { _reader?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }

            _reader = null;
            _client = null;

            // Clean up any remaining CTS entries. Handlers that outlived the
            // drain will find _inFlight empty and skip cleanup; that's fine.
            foreach (var kvp in _inFlight)
            {
                try { kvp.Value.Dispose(); } catch { }
            }
            _inFlight.Clear();
        }

        /// <summary>
        /// Kill the bridge process (call on editor quit).
        /// </summary>
        public void KillBridge()
        {
            var bridgePath = _bridgeExecutablePath;
            if (string.IsNullOrEmpty(bridgePath))
            {
                bridgePath = FindBridgeExecutable();
                _bridgeExecutablePath = bridgePath;
            }

            KillBridgeAtPath(bridgePath);
        }

        private void KillBridgeAtPath(string bridgePath)
        {
            var processName = string.IsNullOrEmpty(bridgePath)
                ? "mcp-bridge"
                : Path.GetFileNameWithoutExtension(bridgePath);
            if (string.IsNullOrEmpty(processName))
                return;

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warn, $"Failed to enumerate bridge processes: {ex.Message}");
                return;
            }

            foreach (var proc in processes)
            {
                try
                {
                    if (!IsOwnedBridgeProcess(proc, bridgePath))
                        continue;

                    proc.Kill();
                    _log.Log(LogLevel.Info, $"Killed MCP bridge process (PID: {proc.Id})");
                    if (proc.Id == _bridgeProcessId)
                        _bridgeProcessId = -1;
                }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Warn, $"Failed to kill bridge process {proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        private bool IsOwnedBridgeProcess(Process proc, string bridgePath)
        {
            if (_bridgeProcessId > 0 && proc.Id == _bridgeProcessId)
            {
                if (string.IsNullOrEmpty(bridgePath)) return true;
                return ProcessPathMatches(proc, bridgePath);
            }

            if (string.IsNullOrEmpty(bridgePath))
                return false;

            return ProcessPathMatches(proc, bridgePath);
        }

        private static bool ProcessPathMatches(Process proc, string expectedPath)
        {
            try
            {
                var actualPath = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(actualPath)) return false;
                return string.Equals(
                    Path.GetFullPath(actualPath),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;

            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
