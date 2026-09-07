using System;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp
{
    public readonly struct ToolContext
    {
        public JObject Arguments { get; }
        public IProgressReporter Progress { get; }
        public IMainThreadScheduler MainThread { get; }
        public IFrameWaiter Frames { get; }
        public IServiceProvider RuntimeServices { get; }
        public ILogSink Logger { get; }
        public string RequestId { get; }

        public ToolContext(
            JObject arguments,
            IProgressReporter progress,
            IMainThreadScheduler mainThread,
            IFrameWaiter frames,
            IServiceProvider runtimeServices,
            ILogSink logger,
            string requestId)
        {
            Arguments = arguments;
            Progress = progress;
            MainThread = mainThread;
            Frames = frames;
            RuntimeServices = runtimeServices;
            Logger = logger;
            RequestId = requestId;
        }
    }
}
