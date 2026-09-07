using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp
{
    /// <summary>
    /// Outbound channel for server-initiated notifications (tools/list_changed,
    /// progress, resource updates). Implementations fan out to active SSE sessions.
    /// </summary>
    public interface INotificationSink
    {
        /// <summary>
        /// Send a JSON-RPC notification to all connected clients.
        /// </summary>
        /// <param name="method">Notification method name (e.g. "notifications/tools/list_changed").</param>
        /// <param name="parameters">Optional params object. Null for parameterless notifications.</param>
        void Send(string method, JObject parameters = null);

        /// <summary>
        /// Number of active SSE sessions. Zero means no clients are listening for push.
        /// </summary>
        int SessionCount { get; }
    }
}
