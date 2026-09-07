using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Transport
{
    public enum SseAttachResult
    {
        Attached,
        NotFound,
        AlreadyAttached,
    }

    /// <summary>
    /// Owns initialized client sessions and fans JSON-RPC notifications out to
    /// their bounded queues. Sessions survive SSE disconnects until DELETE or expiry.
    /// </summary>
    public sealed class NotificationBroadcaster : INotificationSink
    {
        public const int DefaultMaxSessions = 64;
        public const int MaxPendingNotifications = 64;
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

        private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
        private readonly object _sessionSync = new();
        private readonly ILogSink _log;
        private readonly int _maxSessions;
        private readonly TimeSpan _idleTimeout;
        private readonly Func<DateTime> _utcNow;

        public int SessionCount => _sessions.Count;

        /// <summary>
        /// Raised whenever a notification is sent. Allows external transports
        /// (e.g., BridgeTransport) to forward notifications to remote clients.
        /// </summary>
        public event Action<JObject> NotificationSent;

        public NotificationBroadcaster(
            ILogSink log,
            int maxSessions = DefaultMaxSessions,
            TimeSpan? idleTimeout = null,
            Func<DateTime> utcNow = null)
        {
            _log = log;
            _maxSessions = Math.Max(1, maxSessions);
            _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public bool TryCreateSession(out string sessionId)
        {
            SweepExpired();
            sessionId = null;
            lock (_sessionSync)
            {
                if (_sessions.Count >= _maxSessions)
                    return false;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var candidate = Guid.NewGuid().ToString("N");
                    if (!_sessions.TryAdd(candidate, new SessionState(_utcNow())))
                        continue;
                    sessionId = candidate;
                    _log?.Log(LogLevel.Info, $"MCP session initialized: {ShortId(candidate)} (total: {_sessions.Count})");
                    return true;
                }
            }
            return false;
        }

        public bool TryTouchSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var state))
                return false;
            lock (state.Sync)
            {
                if (!_sessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, state))
                    return false;
                state.LastSeen = _utcNow();
            }
            return true;
        }

        public SseAttachResult AttachSession(string sessionId, HttpListenerResponse response)
        {
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var state))
                return SseAttachResult.NotFound;

            lock (state.Sync)
            {
                if (!_sessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, state))
                    return SseAttachResult.NotFound;
                if (state.Active?.IsAlive == true)
                    return SseAttachResult.AlreadyAttached;

                state.Active?.Dispose();
                var session = new SseSession(sessionId, response, _log, disconnected => OnSseDisconnected(state, disconnected));
                state.Active = session;
                state.LastSeen = _utcNow();
                while (state.Pending.Count > 0)
                    session.Enqueue(state.Pending.Dequeue());
            }

            _log?.Log(LogLevel.Info, $"SSE stream attached: {ShortId(sessionId)}");
            return SseAttachResult.Attached;
        }

        public bool RemoveSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || !_sessions.TryRemove(sessionId, out var state))
                return false;
            lock (state.Sync)
            {
                state.Active?.Dispose();
                state.Active = null;
                state.Pending.Clear();
            }
            _log?.Log(LogLevel.Info, $"MCP session removed: {ShortId(sessionId)} (total: {_sessions.Count})");
            return true;
        }

        /// <summary>
        /// Try to find the active SSE stream for diagnostics and tests.
        /// </summary>
        public bool TryGetSession(string sessionId, out SseSession session)
        {
            session = null;
            if (!_sessions.TryGetValue(sessionId, out var state))
                return false;
            lock (state.Sync)
            {
                session = state.Active;
                return session?.IsAlive == true;
            }
        }

        public int SweepExpired()
        {
            var now = _utcNow();
            var removed = 0;
            foreach (var pair in _sessions.ToArray())
            {
                var expired = false;
                lock (pair.Value.Sync)
                {
                    if (pair.Value.Active?.IsAlive == true || now - pair.Value.LastSeen < _idleTimeout)
                        continue;
                    expired = _sessions.TryRemove(pair.Key, out var removedState)
                              && ReferenceEquals(removedState, pair.Value);
                    if (expired)
                    {
                        pair.Value.Active?.Dispose();
                        pair.Value.Active = null;
                        pair.Value.Pending.Clear();
                    }
                }
                if (expired)
                {
                    _log?.Log(LogLevel.Info, $"MCP session expired: {ShortId(pair.Key)} (total: {_sessions.Count})");
                    removed++;
                }
            }
            return removed;
        }

        /// <inheritdoc/>
        public void Send(string method, JObject parameters = null)
        {
            var notification = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
            };
            if (parameters != null)
                notification["params"] = parameters;

            try { NotificationSent?.Invoke(notification); }
            catch (Exception ex)
            {
                _log?.Log(LogLevel.Error, $"NotificationSent subscriber threw: {ex.Message}");
            }

            SweepExpired();
            foreach (var state in _sessions.Values)
            {
                lock (state.Sync)
                {
                    if (state.Active?.IsAlive == true)
                    {
                        state.Active.Enqueue(notification);
                        continue;
                    }
                    while (state.Pending.Count >= MaxPendingNotifications)
                        state.Pending.Dequeue();
                    state.Pending.Enqueue(notification);
                }
            }
        }

        public void DisposeAll()
        {
            foreach (var id in _sessions.Keys)
                RemoveSession(id);
        }

        private void OnSseDisconnected(SessionState state, SseSession session)
        {
            lock (state.Sync)
            {
                if (!ReferenceEquals(state.Active, session))
                    return;
                state.Active = null;
                state.LastSeen = _utcNow();
            }
            session.Dispose();
        }

        private static string ShortId(string id) => id.Length <= 8 ? id : id.Substring(0, 8);

        private sealed class SessionState
        {
            public readonly object Sync = new();
            public readonly Queue<JObject> Pending = new();
            public DateTime LastSeen;
            public SseSession Active;

            public SessionState(DateTime lastSeen) => LastSeen = lastSeen;
        }
    }
}
