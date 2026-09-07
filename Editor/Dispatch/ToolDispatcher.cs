using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public readonly struct DispatchResult
    {
        public ToolResult Value { get; }
        public DispatchError Error { get; }

        public DispatchResult(ToolResult value)
        {
            Value = value;
            Error = null;
        }

        public DispatchResult(DispatchError error)
        {
            Value = null;
            Error = error;
        }
    }

    public sealed class DispatchError
    {
        public int Code { get; }
        public string Message { get; }
        public JObject Data { get; }
        public Exception Exception { get; }
        public bool IsExpected { get; }

        public DispatchError(
            int code,
            string message,
            JObject data = null,
            Exception exception = null,
            bool? isExpected = null)
        {
            Code = code;
            Message = message;
            Data = data;
            Exception = exception;
            IsExpected = isExpected ?? exception == null;
        }
    }

    public sealed class ToolDispatcher
    {
        private readonly ToolRegistry _registry;
        private readonly IMainThreadScheduler _mainThread;
        private readonly IFrameWaiter _frames;
        private readonly ILogSink _log;
        private readonly Func<(bool isPlaying, bool isCompiling)> _stateProbe;
        private readonly INotificationSink _notifications;
        private readonly TimeSpan _defaultTimeout;
        private readonly IToolAuthorizer _authorizer;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _exclusiveLocks = new();

        public ToolDispatcher(
            ToolRegistry registry,
            IMainThreadScheduler mainThread,
            IFrameWaiter frames,
            ILogSink log,
            Func<(bool isPlaying, bool isCompiling)> stateProbe,
            INotificationSink notifications = null,
            TimeSpan? defaultTimeout = null,
            IToolAuthorizer authorizer = null)
        {
            _registry = registry;
            _mainThread = mainThread;
            _frames = frames;
            _log = log;
            _stateProbe = stateProbe;
            _notifications = notifications;
            _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
            _authorizer = authorizer;
        }

        private DispatchError BackgroundOperationError(ToolDescriptor tool, JObject arguments)
        {
            if (!tool.RequiresMainThread && Host.McpTrustPolicy.ResolveCategory(tool, arguments) == ToolTrustCategory.Read)
                return null;
            foreach (var owner in _registry.Enumerate())
                if (owner.BackgroundOperationActive?.Invoke() == true
                    && (owner.ExclusiveGroup == null || owner.ExclusiveGroup != tool.ExclusiveGroup))
                    return new DispatchError(McpErrorCodes.ExclusiveBusy,
                        $"'{owner.Name}' is still running or restoring Editor state. Poll its result before other Editor operations.",
                        new JObject { ["ownerTool"] = owner.Name, ["retryAfterMs"] = 500 });
            return null;
        }

        private void ThrowIfBackgroundOperationActive(ToolDescriptor tool, JObject arguments)
        {
            var error = BackgroundOperationError(tool, arguments);
            if (error != null) throw new McpToolException(error.Code, error.Message, error.Data);
        }

        public async Task<DispatchResult> DispatchAsync(string name, JObject arguments, string requestId, CancellationToken ct, string progressToken = null)
        {
            if (!_registry.TryGet(name, out var tool))
                return new DispatchResult(new DispatchError(McpErrorCodes.MethodNotFound, $"Tool '{name}' not found."));

            var backgroundError = BackgroundOperationError(tool, arguments);
            if (backgroundError != null) return new DispatchResult(backgroundError);

            var (isPlaying, isCompiling) = _stateProbe();

            if (isCompiling && (tool.Availability & ToolAvailability.Compiling) == 0)
                return new DispatchResult(new DispatchError(McpErrorCodes.Compiling, "Editor is compiling.", new JObject { ["retryAfterMs"] = 500 }));

            if (tool.InputSchema != null)
            {
                var errors = SchemaValidator.Validate(tool.InputSchema, arguments ?? new JObject()).ToArray();
                if (errors.Length > 0)
                {
                    var data = new JObject
                    {
                        ["errors"] = new JArray(errors.Select(e => new JObject { ["path"] = e.Path, ["message"] = e.Message })),
                    };
                    return new DispatchResult(new DispatchError(McpErrorCodes.ValidationFailed, "Arguments failed schema validation.", data));
                }
            }

            var modeOk = (isPlaying && (tool.Availability & ToolAvailability.PlayMode) != 0)
                      || (!isPlaying && (tool.Availability & ToolAvailability.EditMode) != 0);
            if (!modeOk)
                return new DispatchResult(new DispatchError(
                    McpErrorCodes.ToolUnavailable,
                    $"Tool '{name}' is not available in the current editor state.",
                    new JObject { ["requiredMode"] = isPlaying ? "EditMode" : "PlayMode", ["hint"] = "Change Play Mode explicitly through the dedicated editor tools, then retry." }));

            if (_authorizer != null)
            {
                try
                {
                    await _authorizer.AuthorizeAsync(tool, arguments ?? new JObject(), ct);
                }
                catch (McpToolException mex)
                {
                    return new DispatchResult(new DispatchError(mex.Code, mex.Message, mex.Data));
                }
                catch (OperationCanceledException)
                {
                    return new DispatchResult(new DispatchError(McpErrorCodes.Cancelled, "Request was cancelled."));
                }
            }

            IProgressReporter progress = _notifications != null
                ? new SseProgressReporter(_notifications, progressToken ?? requestId)
                : (IProgressReporter)new NoopProgressReporter();

            var ctx = new ToolContext(
                arguments ?? new JObject(),
                progress,
                _mainThread,
                _frames,
                McpRuntimeBridge.Services,
                _log,
                requestId);

            // Fail fast if the main thread has been stalled (modal dialog, frozen
            // domain reload, focus-loss starvation). Without this check, the tool
            // queues into MainThreadPump and the client just sees "timeout" with
            // no diagnostic — and worse, every retry queues another stuck item.
            // Threshold is generous (10s) so a long-running prior tool doesn't
            // trip it; we only want to catch true stalls.
            const long mainThreadStallThresholdMs = 10_000;
            if (tool.RequiresMainThread)
            {
                var stalledMs = _mainThread.MillisecondsSinceLastTick;
                if (stalledMs > mainThreadStallThresholdMs)
                {
                    return new DispatchResult(new DispatchError(
                        McpErrorCodes.MainThreadBusy,
                        $"Unity main thread has not ticked for {stalledMs}ms — likely a modal dialog, frozen domain reload, or focus-loss starvation. Bring the Editor to the foreground or dismiss any open dialog.",
                        new JObject { ["stalledForMs"] = stalledMs }));
                }
            }

            // Enforce timeout: per-tool Timeout, or default
            var timeout = tool.Timeout ?? _defaultTimeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            // Acquire exclusive lock if this tool belongs to a group.
            SemaphoreSlim exclusiveLock = null;
            var lockAcquired = false;
            var exclusiveState = 0; // 0 = dispatcher-owned, 1 = handler-owned, 2 = released
            try
            {
                if (tool.ExclusiveGroup != null)
                {
                    exclusiveLock = _exclusiveLocks.GetOrAdd(tool.ExclusiveGroup, _ => new SemaphoreSlim(1, 1));
                    if (!await exclusiveLock.WaitAsync(0, timeoutCts.Token))
                    {
                        _log.Log(LogLevel.Warn, $"Tool '{name}' waiting on exclusive group '{tool.ExclusiveGroup}'");
                        try
                        {
                            await exclusiveLock.WaitAsync(timeoutCts.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            return new DispatchResult(new DispatchError(
                                McpErrorCodes.ExclusiveBusy,
                                $"Tool '{name}' timed out waiting for exclusive group '{tool.ExclusiveGroup}'. Another tool in this group is still running.",
                                new JObject { ["exclusiveGroup"] = tool.ExclusiveGroup }));
                        }
                    }
                    lockAcquired = true;
                }

                async ValueTask<ToolResult> Work(CancellationToken token)
                {
                    if (lockAcquired && Interlocked.CompareExchange(ref exclusiveState, 1, 0) != 0)
                        throw new OperationCanceledException(token);

                    try
                    {
                        ThrowIfBackgroundOperationActive(tool, arguments);
                        return await tool.Handler(ctx, token);
                    }
                    finally
                    {
                        if (lockAcquired && Interlocked.Exchange(ref exclusiveState, 2) != 2)
                            exclusiveLock.Release();
                    }
                }

                var result = tool.RequiresMainThread
                    ? await _mainThread.RunAsync(Work, timeoutCts.Token)
                    : await Work(timeoutCts.Token);
                return new DispatchResult(result);
            }
            catch (McpToolException mex)
            {
                return new DispatchResult(new DispatchError(mex.Code, mex.Message, mex.Data));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new DispatchResult(new DispatchError(McpErrorCodes.Cancelled, "Request was cancelled."));
            }
            catch (OperationCanceledException)
            {
                return new DispatchResult(new DispatchError(
                    McpErrorCodes.ToolError,
                    $"Tool '{name}' timed out after {timeout.TotalSeconds:0}s.",
                    BuildTimeoutData(name, tool, timeout)));
            }
            catch (Exception ex)
            {
                return new DispatchResult(new DispatchError(McpErrorCodes.ToolError, ex.Message, exception: ex));
            }
            finally
            {
                // Cancellation may complete MainThreadPump's proxy task while the actual
                // Unity handler is still running. Release here only if that handler never
                // started; otherwise Work owns the lease until the real operation settles.
                if (lockAcquired && Interlocked.CompareExchange(ref exclusiveState, 2, 0) == 0)
                    exclusiveLock.Release();
            }
        }

        private sealed class NoopProgressReporter : IProgressReporter
        {
            public void Report(double progress, string message = null) { }
        }

        private JObject BuildTimeoutData(string name, ToolDescriptor tool, TimeSpan timeout)
        {
            var (isPlaying, isCompiling) = _stateProbe();
            var stalledMs = _mainThread.MillisecondsSinceLastTick;
            return new JObject
            {
                ["tool"] = name,
                ["timeoutMs"] = (long)timeout.TotalMilliseconds,
                ["isPlaying"] = isPlaying,
                ["isCompiling"] = isCompiling,
                ["mainThreadStalledMs"] = stalledMs,
                ["requiresMainThread"] = tool.RequiresMainThread,
                ["reloadSafe"] = tool.ReloadSafe,
                ["exclusiveGroup"] = tool.ExclusiveGroup,
                ["hint"] = stalledMs > 0
                    ? "Unity main thread is not ticking fast enough. Bring the Editor to the foreground, dismiss modal dialogs, or set Preferences > General > Interaction Mode to No Throttling while running MCP automation."
                    : "Tool exceeded its timeout. Check editor.status and editor.compile.errors before retrying."
            };
        }
    }
}
