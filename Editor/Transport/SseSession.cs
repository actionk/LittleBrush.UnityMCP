using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Transport
{
    /// <summary>
    /// Represents one active SSE stream and drains a bounded outbound queue.
    /// </summary>
    public sealed class SseSession : IDisposable
    {
        private const int MaxOutboundNotifications = 64;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

        public string Id { get; }
        public DateTime ConnectedAt { get; }
        public bool IsAlive => Volatile.Read(ref _alive) != 0;

        private readonly HttpListenerResponse _response;
        private readonly StreamWriter _writer;
        private readonly object _queueSync = new();
        private readonly Queue<JObject> _outbound = new();
        private readonly Thread _writeThread;
        private readonly ManualResetEventSlim _signal = new(false);
        private readonly CancellationTokenSource _cts = new();
        private readonly ILogSink _log;
        private readonly Action<SseSession> _disconnected;
        private int _alive = 1;
        private int _disposed;

        public SseSession(string id, HttpListenerResponse response, ILogSink log, Action<SseSession> disconnected = null)
        {
            Id = id;
            ConnectedAt = DateTime.UtcNow;
            _response = response;
            _log = log;
            _disconnected = disconnected;

            _response.StatusCode = 200;
            _response.ContentType = "text/event-stream";
            _response.Headers.Set("Cache-Control", "no-cache");
            _response.Headers.Set("Connection", "keep-alive");
            _response.Headers.Set("Mcp-Session-Id", id);
            _response.SendChunked = true;

            _writer = new StreamWriter(_response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            _writeThread = new Thread(DrainLoop) { IsBackground = true, Name = $"McpSse-{ShortId(id)}" };
            _writeThread.Start();
        }

        public void Enqueue(JObject notification)
        {
            if (!IsAlive) return;
            lock (_queueSync)
            {
                if (!IsAlive) return;
                while (_outbound.Count >= MaxOutboundNotifications)
                    _outbound.Dequeue();
                _outbound.Enqueue(notification);
            }
            _signal.Set();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _alive, 0);
            try { _cts.Cancel(); } catch { }
            try { _signal.Set(); } catch { }
            try { _writer.Close(); } catch { }
            try { _response.Close(); } catch { }
        }

        private void DrainLoop()
        {
            try
            {
                WriteEvent("endpoint", $"/mcp?sessionId={Id}");
                while (!_cts.IsCancellationRequested)
                {
                    if (!_signal.Wait(HeartbeatInterval, _cts.Token))
                    {
                        WriteComment("keep-alive");
                        continue;
                    }
                    _signal.Reset();
                    while (TryDequeue(out var msg))
                    {
                        WriteEvent("message", msg.ToString(Formatting.None));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _log?.Log(LogLevel.Error, $"SSE session {ShortId(Id)} write error", ex);
            }
            finally
            {
                Volatile.Write(ref _alive, 0);
                try { _disconnected?.Invoke(this); }
                catch (Exception ex) { _log?.Log(LogLevel.Error, $"SSE disconnect callback failed: {ex.Message}"); }
            }
        }

        private void WriteEvent(string eventType, string data)
        {
            _writer.Write($"event: {eventType}\ndata: {data}\n\n");
            _writer.Flush();
            _response.OutputStream.Flush();
        }

        private void WriteComment(string text)
        {
            _writer.Write($": {text}\n\n");
            _writer.Flush();
            _response.OutputStream.Flush();
        }

        private bool TryDequeue(out JObject notification)
        {
            lock (_queueSync)
            {
                if (_outbound.Count == 0)
                {
                    notification = null;
                    return false;
                }
                notification = _outbound.Dequeue();
                return true;
            }
        }

        private static string ShortId(string id) => id.Length <= 8 ? id : id.Substring(0, 8);
    }
}
