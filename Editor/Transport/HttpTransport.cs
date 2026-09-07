using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Transport
{
    public sealed class HttpTransport : IDisposable
    {
        internal const int MaxRequestBodyBytes = 1 << 20;
        private const int MaxConcurrentRequests = 32;
        private const int RequestsPerSecond = 60;
        private const string ProtocolVersion = "2025-06-18";

        private readonly int _port;
        private readonly JsonRpcRouter _router;
        private readonly NotificationBroadcaster _broadcaster;
        private readonly ILogSink _log;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _requestSlots = new(MaxConcurrentRequests, MaxConcurrentRequests);
        private readonly object _rateSync = new();
        private double _rateTokens = RequestsPerSecond;
        private DateTime _rateLast = DateTime.UtcNow;
        private HttpListener _listener;
        private Thread _acceptThread;

        public bool IsRunning { get; private set; }

        public HttpTransport(int port, JsonRpcRouter router, NotificationBroadcaster broadcaster, ILogSink log)
        {
            _port = port;
            _router = router;
            _broadcaster = broadcaster;
            _log = log;
        }

        public void Start()
        {
            if (IsRunning) return;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            IsRunning = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "McpHttpAccept" };
            _acceptThread.Start();
            _log.Log(LogLevel.Info, $"MCP HTTP listening on http://127.0.0.1:{_port}/");
        }

        public void Dispose()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts.Cancel(); } catch { }
            try { _broadcaster?.DisposeAll(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        private void AcceptLoop()
        {
            while (!_cts.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Error, "accept failed", ex);
                    return;
                }

                if (!_requestSlots.Wait(0))
                {
                    try
                    {
                        context.Response.Headers.Set("Retry-After", "1");
                        WriteJsonAsync(context, 429, SessionError("Too many concurrent requests")).GetAwaiter().GetResult();
                    }
                    catch { }
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(context); }
                    finally { _requestSlots.Release(); }
                });
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "";
                var method = context.Request.HttpMethod;

                if (!IsAllowedOrigin(context.Request.Headers["Origin"]))
                {
                    await WriteJsonAsync(context, 403, JsonRpcEnvelope.Error(null, McpErrorCodes.ToolError, "Forbidden Origin"));
                    return;
                }

                if (path.Equals("/mcp/health", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    _broadcaster?.SweepExpired();
                    await WriteJsonAsync(context, 200, new JObject
                    {
                        ["ok"] = true,
                        ["port"] = _port,
                        ["clientSessions"] = _broadcaster?.SessionCount ?? 0,
                    });
                    return;
                }

                if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(context, 404, new JObject { ["error"] = "not found" });
                    return;
                }

                _broadcaster?.SweepExpired();

                var version = context.Request.Headers["MCP-Protocol-Version"];
                if (method != "POST" && !string.IsNullOrEmpty(version) && version != ProtocolVersion)
                {
                    await WriteJsonAsync(context, 400, SessionError("Unsupported MCP-Protocol-Version"));
                    return;
                }

                if (method == "GET")
                {
                    var accept = context.Request.Headers["Accept"] ?? "";
                    if (!accept.Contains("text/event-stream"))
                    {
                        await WriteJsonAsync(context, 405, new JObject { ["error"] = "Use POST for JSON-RPC or GET with Accept: text/event-stream for SSE." });
                        return;
                    }
                    HandleSseConnect(context);
                    return;
                }

                if (method == "DELETE")
                {
                    var sessionId = context.Request.Headers["Mcp-Session-Id"];
                    if (string.IsNullOrEmpty(sessionId))
                    {
                        await WriteJsonAsync(context, 400, SessionError("Missing Mcp-Session-Id header"));
                        return;
                    }
                    if (_broadcaster == null || !_broadcaster.RemoveSession(sessionId))
                    {
                        await WriteJsonAsync(context, 404, SessionError("Session not found"));
                        return;
                    }
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    return;
                }

                if (method != "POST")
                {
                    await WriteJsonAsync(context, 405, new JObject { ["error"] = "method not allowed" });
                    return;
                }

                if (!AllowRequest())
                {
                    context.Response.Headers.Set("Retry-After", "1");
                    await WriteJsonAsync(context, 429, SessionError("Rate limit exceeded"));
                    return;
                }

                var body = await ReadBodyAsync(context.Request);
                if (body == null)
                {
                    await WriteJsonAsync(context, 413, SessionError($"Request body too large (max {MaxRequestBodyBytes} bytes)"));
                    return;
                }
                if (!string.IsNullOrEmpty(version) && version != ProtocolVersion)
                {
                    await WriteJsonAsync(context, 400, SessionError("Unsupported MCP-Protocol-Version"));
                    return;
                }

                JObject request;
                try { request = JObject.Parse(body); }
                catch (Exception)
                {
                    await WriteJsonAsync(context, 400, JsonRpcEnvelope.Error(null, McpErrorCodes.ParseError, "Invalid JSON"));
                    return;
                }

                var sessionIdHeader = context.Request.Headers["Mcp-Session-Id"];
                var isInitialize = string.Equals((string)request["method"], "initialize", StringComparison.Ordinal);
                if (isInitialize)
                {
                    if (!string.IsNullOrEmpty(sessionIdHeader))
                    {
                        await WriteJsonAsync(context, 400, SessionError("Initialize must not include Mcp-Session-Id"));
                        return;
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(sessionIdHeader))
                    {
                        await WriteJsonAsync(context, 400, SessionError("Missing Mcp-Session-Id header"));
                        return;
                    }
                    if (_broadcaster == null || !_broadcaster.TryTouchSession(sessionIdHeader))
                    {
                        await WriteJsonAsync(context, 404, SessionError("Session not found"));
                        return;
                    }
                }

                var response = await _router.HandleAsync(request, _cts.Token, sessionIdHeader);
                if (response == null)
                {
                    context.Response.StatusCode = string.IsNullOrEmpty(sessionIdHeader) ? 204 : 202;
                    context.Response.Close();
                    return;
                }

                var responseSessionId = sessionIdHeader;
                if (isInitialize && response["result"] != null && response["error"] == null)
                {
                    if (_broadcaster == null || !_broadcaster.TryCreateSession(out responseSessionId))
                    {
                        await WriteJsonAsync(context, 503, SessionError("Session capacity reached"));
                        return;
                    }
                }
                if (!string.IsNullOrEmpty(responseSessionId))
                    context.Response.Headers.Set("Mcp-Session-Id", responseSessionId);

                await WriteJsonAsync(context, 200, response);
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Error, "request handler failed", ex);
                try { await WriteJsonAsync(context, 500, JsonRpcEnvelope.Error(null, McpErrorCodes.InternalError, ex.Message)); }
                catch { }
            }
        }

        private void HandleSseConnect(HttpListenerContext context)
        {
            var sessionId = context.Request.Headers["Mcp-Session-Id"];
            if (string.IsNullOrEmpty(sessionId))
            {
                WriteJsonAsync(context, 400, SessionError("Missing Mcp-Session-Id header")).GetAwaiter().GetResult();
                return;
            }

            var result = _broadcaster?.AttachSession(sessionId, context.Response) ?? SseAttachResult.NotFound;
            if (result == SseAttachResult.Attached)
                return;
            var status = result == SseAttachResult.AlreadyAttached ? 409 : 404;
            var message = result == SseAttachResult.AlreadyAttached ? "Session already has an active SSE stream" : "Session not found";
            WriteJsonAsync(context, status, SessionError(message)).GetAwaiter().GetResult();
        }

        private bool AllowRequest()
        {
            lock (_rateSync)
            {
                var now = DateTime.UtcNow;
                _rateTokens = Math.Min(RequestsPerSecond, _rateTokens + (now - _rateLast).TotalSeconds * RequestsPerSecond);
                _rateLast = now;
                if (_rateTokens < 1) return false;
                _rateTokens--;
                return true;
            }
        }

        // Bound the drain below: an oversized POST must not become a time or memory sink just because
        // we want its sender to be able to read our 413.
        private const long MaxDrainBytes = 8L * 1024 * 1024;

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
        {
            if (request.ContentLength64 > MaxRequestBodyBytes)
            {
                await DrainAsync(request);
                return null;
            }
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length);
                if (read == 0) break;
                if (buffer.Length + read > MaxRequestBodyBytes)
                {
                    await DrainAsync(request);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            return (request.ContentEncoding ?? Encoding.UTF8).GetString(buffer.ToArray());
        }

        /// <summary>
        /// Read and discard whatever is left of the request body, so the rejection we are about to write
        /// is actually READABLE by the client.
        ///
        /// Without this, a rejection answered while the request body is still in flight closes the
        /// connection from our side, and the sender fails on its own send — "The socket has been shut
        /// down" — instead of reading the status we sent. That is what made
        /// <c>LiveBridge_RejectsOversizedRequestBody</c> fail: it asserts 413 and never got to read one.
        ///
        /// Bounded by <see cref="MaxDrainBytes"/>, and best-effort: if the client disappears mid-drain
        /// there is nothing left to answer, so the exception is swallowed rather than logged as a fault.
        /// </summary>
        private static async Task DrainAsync(HttpListenerRequest request)
        {
            var sink = new byte[8192];
            long drained = 0;
            try
            {
                while (drained < MaxDrainBytes)
                {
                    var read = await request.InputStream.ReadAsync(sink, 0, sink.Length);
                    if (read == 0) break;
                    drained += read;
                }
            }
            catch (Exception)
            {
                // The sender went away; the rejection is best-effort from here.
            }
        }

        internal static bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "localhost" || host == "127.0.0.1" || host == "::1";
        }

        private static JObject SessionError(string message)
            => JsonRpcEnvelope.Error(null, McpErrorCodes.ToolError, message);

        private static async Task WriteJsonAsync(HttpListenerContext context, int status, JObject body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }
    }
}
