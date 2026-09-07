using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    /// <summary>
    /// Pushes <c>notifications/progress</c> events to connected SSE clients.
    /// Created per tool call when an SSE session is active.
    /// </summary>
    public sealed class SseProgressReporter : IProgressReporter
    {
        private readonly INotificationSink _sink;
        private readonly string _progressToken;

        public SseProgressReporter(INotificationSink sink, string progressToken)
        {
            _sink = sink;
            _progressToken = progressToken;
        }

        public void Report(double progress, string message = null)
        {
            var parameters = new JObject
            {
                ["progressToken"] = _progressToken,
                ["progress"] = progress,
            };
            if (message != null)
                parameters["message"] = message;

            _sink.Send("notifications/progress", parameters);
        }
    }
}
