using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Host;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Transport
{
    public sealed class JsonRpcRouter
    {
        private const string CatalogToolName = "unity.tools";
        private const string CallToolName = "unity.call";

        private static readonly JObject CatalogSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""additionalProperties"": false,
            ""properties"": {
                ""name"": { ""type"": ""string"", ""minLength"": 1 },
                ""query"": { ""type"": ""string"" },
                ""names"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 16, ""uniqueItems"": true, ""items"": { ""type"": ""string"", ""minLength"": 1 } },
                ""includeDescriptions"": { ""type"": ""boolean"" },
                ""availableOnly"": { ""type"": ""boolean"" },
                ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 128 }
            }
        }");

        private static readonly JObject CallSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""additionalProperties"": false,
            ""required"": [""tool""],
            ""properties"": {
                ""tool"": { ""type"": ""string"", ""minLength"": 1 },
                ""arguments"": { ""type"": ""object"" }
            }
        }");

        private readonly ToolRegistry _registry;
        private readonly ToolDispatcher _dispatcher;
        private readonly ILogSink _log;
        private readonly Func<(bool isPlaying, bool isCompiling)> _stateProbe;
        private readonly McpToolCallLogMode _toolCallLogMode;
        private readonly int _maxToolCallLogCharacters;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();
        private readonly object _writerLeaseGate = new();
        private readonly TimeSpan _writerLeaseDuration;
        private readonly TimeSpan _writerLeaseWaitTimeout;
        private readonly LinkedList<WriterLeaseWaiter> _writerLeaseWaiters = new();
        private string _writerLeaseOwner;
        private string _writerLeaseTool;
        private int _writerLeaseActiveCalls;
        private DateTime _writerLeaseExpiresUtc;
        private long _nextCorrelationId;
        private readonly Action<string, JObject, JObject, long, long, long, int> _recordUsage;

        public JsonRpcRouter(
            ToolRegistry registry,
            ToolDispatcher dispatcher,
            Func<(bool isPlaying, bool isCompiling)> stateProbe,
            ILogSink log = null,
            McpToolCallLogMode toolCallLogMode = McpToolCallLogMode.Summary,
            int maxToolCallLogCharacters = McpToolCallLogger.DefaultMaxCompactResponseCharacters,
            TimeSpan? writerLeaseDuration = null,
            TimeSpan? writerLeaseWaitTimeout = null,
            Action<string, JObject, JObject, long, long, long, int> recordUsage = null)
        {
            _recordUsage = recordUsage;
            _registry = registry;
            _dispatcher = dispatcher;
            _stateProbe = stateProbe;
            _log = log;
            _toolCallLogMode = toolCallLogMode;
            _maxToolCallLogCharacters = Math.Max(256, maxToolCallLogCharacters);
            _writerLeaseDuration = writerLeaseDuration ?? TimeSpan.Zero;
            _writerLeaseWaitTimeout = writerLeaseWaitTimeout ?? TimeSpan.FromMinutes(5);
        }

        internal string[] ReloadSafeToolNames()
            => _registry.Enumerate()
                .Where(tool => tool.ReloadSafe)
                .Select(tool => tool.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        public async Task<JObject> HandleAsync(JObject request, CancellationToken ct, string scope = null)
        {
            var id = request["id"];
            var method = (string)request["method"];
            var parameters = request["params"] as JObject ?? new JObject();

            if (string.IsNullOrEmpty(method))
                return JsonRpcEnvelope.Error(id, McpErrorCodes.InvalidRequest, "Missing method.");

            switch (method)
            {
                case "initialize":
                    return JsonRpcEnvelope.Success(id, Initialize());
                case "notifications/initialized":
                    return null;
                case "notifications/cancelled":
                    CancelInFlight(parameters, scope);
                    return null;
                case "ping":
                    return JsonRpcEnvelope.Success(id, new JObject());
                case "tools/list":
                    return JsonRpcEnvelope.Success(id, ListTools());
                case "tools/call":
                    return await CallToolAsync(id, parameters, ct, scope);
                case "logging/setLevel":
                    return LittleBrushGames.Mcp.Editor.Providers.LogsProvider.SetMinimumLevel((string)parameters["level"])
                        ? JsonRpcEnvelope.Success(id, new JObject())
                        : JsonRpcEnvelope.Error(id, McpErrorCodes.InvalidParams, "Invalid logging level.");
                default:
                    return JsonRpcEnvelope.Error(id, McpErrorCodes.MethodNotFound, $"Method '{method}' not found.");
            }
        }

        private static JObject Initialize() => new()
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JObject
            {
                ["tools"] = new JObject { ["listChanged"] = true },
                ["logging"] = new JObject(),
            },
            ["serverInfo"] = new JObject
            {
                ["name"] = "LittleBrushGames.UnityMCP",
                ["version"] = "0.1.0",
            },
            ["instructions"] = "Use unity.tools to discover a tool schema, then invoke it through unity.call.",
        };

        private JObject ListTools()
        {
            return new JObject
            {
                ["tools"] = new JArray
                {
                    GatewayEntry(
                        CatalogToolName,
                        "Search Unity tools, or pass name to fetch one full schema.",
                        CatalogSchema,
                        readOnly: true),
                    GatewayEntry(
                        CallToolName,
                        "Invoke a Unity tool by name after fetching its schema with unity.tools.",
                        CallSchema,
                        readOnly: false),
                },
            };
        }

        private static JObject GatewayEntry(string name, string description, JObject schema, bool readOnly)
        {
            var annotations = new JObject { ["readOnlyHint"] = readOnly };
            if (!readOnly) annotations["destructiveHint"] = true;
            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = schema,
                ["annotations"] = annotations,
                ["_meta"] = new JObject
                {
                    ["availability"] = ToolAvailability.Always.ToString(),
                    ["currentlyAvailable"] = true,
                    ["requiresMainThread"] = false,
                },
            };
        }

        private static string DescribePlayMode(ToolDescriptor descriptor)
        {
            var playMode = (descriptor.Availability & ToolAvailability.PlayMode) == 0
                ? "unavailable"
                : (descriptor.Availability & ToolAvailability.EditMode) == 0 ? "required" : "allowed";
            return playMode;
        }

        private static bool IsCurrentlyAvailable(ToolDescriptor descriptor, bool isPlaying, bool isCompiling)
        {
            if (isCompiling && (descriptor.Availability & ToolAvailability.Compiling) == 0)
                return false;

            return (isPlaying && (descriptor.Availability & ToolAvailability.PlayMode) != 0)
                   || (!isPlaying && (descriptor.Availability & ToolAvailability.EditMode) != 0);
        }

        private async Task<JObject> CallToolAsync(JToken id, JObject parameters, CancellationToken ct, string scope)
        {
            var name = (string)parameters["name"];
            var arguments = parameters["arguments"] as JObject ?? new JObject();
            if (name == CallToolName)
            {
                var validation = ValidateGateway(CallSchema, arguments);
                if (validation != null) return JsonRpcEnvelope.Error(id, validation.Code, validation.Message, validation.Data);
                name = (string)arguments["tool"];
                arguments = arguments["arguments"] as JObject ?? new JObject();
            }

            var requestId = RequestKey(scope, id ?? System.Guid.NewGuid().ToString());
            var correlationId = Interlocked.Increment(ref _nextCorrelationId);
            var writerSession = scope ?? "default";
            var writerLeaseAcquired = false;
            var dispatchStarted = false;
            JObject usageResponse = null;
            var usageWatch = Stopwatch.StartNew();
            long writerWaitMs = 0, dispatchMs = 0;
            var responseCharacters = -1;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _inFlight[requestId] = cts;
            try
            {
                if (name != CatalogToolName
                    && _registry.TryGet(name, out var descriptor)
                    && ((descriptor.RequiresMainThread && descriptor.RequiresWriterLease)
                        || McpTrustPolicy.ResolveCategory(descriptor, arguments) != ToolTrustCategory.Read))
                {
                    DispatchError waitError;
                    var waitWatch = Stopwatch.StartNew();
                    try { waitError = await AcquireWriterLeaseAsync(writerSession, name, cts.Token); }
                    finally { writerWaitMs = waitWatch.ElapsedMilliseconds; }
                    if (waitError != null)
                        return usageResponse = JsonRpcEnvelope.Error(id, waitError.Code, waitError.Message, waitError.Data);
                    writerLeaseAcquired = true;
                }

                var stopwatch = Stopwatch.StartNew();
                dispatchStarted = true;
                var progressToken = parameters["_meta"]?["progressToken"]?.ToString();
                var result = name == CatalogToolName
                    ? Catalog(arguments)
                    : await _dispatcher.DispatchAsync(name, arguments, requestId, cts.Token, progressToken);
                var response = result.Error != null
                    ? JsonRpcEnvelope.Error(id, result.Error.Code, result.Error.Message, result.Error.Data)
                    : JsonRpcEnvelope.Success(id, SerializeToolResult(result.Value));
                usageResponse = response;
                stopwatch.Stop();
                dispatchMs = stopwatch.ElapsedMilliseconds;
                responseCharacters = McpToolCallLogger.CountJsonCharacters(response);
                var explicitLargeResponse = arguments["detail"]?.Value<string>() is "all" or "full" or "diagnostics";
                var containsImage = result.Value?.Content.Any(block => block is ImageContent) == true;
                var warn = McpToolMetrics.Record(
                    name,
                    stopwatch.ElapsedMilliseconds,
                    responseCharacters,
                    result.Error != null || result.Value?.IsError == true,
                    responseCharacters > McpToolMetrics.LargeResponseWarningCharacters
                    && !explicitLargeResponse
                    && !containsImage);
                if (warn)
                    _log?.Log(LogLevel.Warn,
                        $"Tool '{name}' returned {responseCharacters} JSON characters without explicit full detail; consider a compact default or pagination.");
                var expectedFailure = result.Error?.IsExpected == true
                                      || (result.Value?.IsError == true && result.Value.IsExpectedFailure);
                McpToolCallLogger.Log(
                    _log,
                    name,
                    id,
                    correlationId,
                    response,
                    _toolCallLogMode,
                    _maxToolCallLogCharacters,
                    result.Error?.Exception,
                    expectedFailure,
                    arguments);
                return response;
            }
            catch (OperationCanceledException) when (!dispatchStarted)
            {
                return usageResponse = JsonRpcEnvelope.Error(id, McpErrorCodes.ToolUnavailable,
                    "Request was cancelled before tool dispatch; no tool handler ran. Retry when Unity is ready.",
                    new JObject { ["executionState"] = "not_started", ["tool"] = name });
            }
            finally
            {
                try { _recordUsage?.Invoke(name, arguments, usageResponse, usageWatch.ElapsedMilliseconds, writerWaitMs, dispatchMs, responseCharacters); }
                catch (Exception ex) { _log?.Log(LogLevel.Warn, "MCP usage recording failed: " + ex.Message); }
                _inFlight.TryRemove(requestId, out _);
                if (writerLeaseAcquired) ReleaseWriterLease(writerSession);
            }
        }

        private async Task<DispatchError> AcquireWriterLeaseAsync(
            string session,
            string tool,
            CancellationToken ct)
        {
            WriterLeaseWaiter waiter = null;
            var deadlineUtc = DateTime.UtcNow + _writerLeaseWaitTimeout;
            try
            {
                while (true)
                {
                    Task signal;
                    TimeSpan delay;
                    lock (_writerLeaseGate)
                    {
                        var now = DateTime.UtcNow;
                        var leaseAvailable = string.IsNullOrEmpty(_writerLeaseOwner)
                                             || (_writerLeaseActiveCalls == 0 && now >= _writerLeaseExpiresUtc);
                        var sameOwner = _writerLeaseOwner == session
                                        && (_writerLeaseActiveCalls > 0 || now < _writerLeaseExpiresUtc);
                        var firstInLine = waiter?.Node == _writerLeaseWaiters.First;
                        if ((sameOwner && waiter == null)
                            || (leaseAvailable && (firstInLine || (waiter == null && _writerLeaseWaiters.Count == 0))))
                        {
                            if (waiter != null)
                                _writerLeaseWaiters.Remove(waiter.Node);
                            _writerLeaseOwner = session;
                            _writerLeaseTool = tool;
                            _writerLeaseActiveCalls++;
                            _writerLeaseExpiresUtc = now + _writerLeaseDuration;
                            PulseWriterLeaseWaitersLocked();
                            return null;
                        }

                        if (now >= deadlineUtc)
                        {
                            RemoveWriterLeaseWaiterLocked(waiter);
                            return WriterLeaseWaitTimeout(tool, now);
                        }

                        if (waiter == null)
                        {
                            waiter = new WriterLeaseWaiter();
                            waiter.Node = _writerLeaseWaiters.AddLast(waiter);
                        }
                        else if (waiter.Signal.Task.IsCompleted)
                        {
                            waiter.Signal = NewWriterLeaseSignal();
                        }

                        signal = waiter.Signal.Task;
                        var nextCheckUtc = _writerLeaseActiveCalls == 0 && waiter.Node == _writerLeaseWaiters.First
                            ? Min(_writerLeaseExpiresUtc, deadlineUtc)
                            : deadlineUtc;
                        delay = nextCheckUtc <= now ? TimeSpan.Zero : nextCheckUtc - now;
                    }

                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    await Task.WhenAny(signal, Task.Delay(delay, wake.Token));
                    wake.Cancel(); // Do not retain a five-minute timer after an early signal.
                    ct.ThrowIfCancellationRequested();
                }
            }
            catch
            {
                lock (_writerLeaseGate)
                    RemoveWriterLeaseWaiterLocked(waiter);
                throw;
            }
        }

        private void ReleaseWriterLease(string session)
        {
            lock (_writerLeaseGate)
            {
                if (_writerLeaseOwner != session || _writerLeaseActiveCalls <= 0) return;
                _writerLeaseActiveCalls--;
                if (_writerLeaseActiveCalls == 0)
                    _writerLeaseExpiresUtc = DateTime.UtcNow + _writerLeaseDuration;
                PulseWriterLeaseWaitersLocked();
            }
        }

        internal JObject WriterStatus()
        {
            lock (_writerLeaseGate)
            {
                var idle = _writerLeaseActiveCalls == 0;
                var remaining = idle ? Math.Max(0L, (long)(_writerLeaseExpiresUtc - DateTime.UtcNow).TotalMilliseconds) : 0L;
                return new JObject
                {
                    ["state"] = !idle ? "active" : remaining > 0 ? "reserved" : "available",
                    ["ownerTool"] = !idle || remaining > 0 ? _writerLeaseTool : null,
                    ["activeCalls"] = _writerLeaseActiveCalls,
                    ["queuedCalls"] = _writerLeaseWaiters.Count,
                    ["reservationMs"] = remaining,
                };
            }
        }

        private DispatchError WriterLeaseWaitTimeout(string requestedTool, DateTime now)
        {
            var retryAfterMs = _writerLeaseActiveCalls > 0
                ? 1000L
                : Math.Max(1L, (long)(_writerLeaseExpiresUtc - now).TotalMilliseconds);
            return new DispatchError(
                McpErrorCodes.ExclusiveBusy,
                $"Timed out waiting for the Unity MCP writer held by '{_writerLeaseTool}'.",
                new JObject
                {
                    ["bridgeError"] = "UNITY_WRITER_WAIT_TIMEOUT",
                    ["retryable"] = true,
                    ["ownerSession"] = ShortSession(_writerLeaseOwner),
                    ["ownerTool"] = _writerLeaseTool,
                    ["requestedTool"] = requestedTool,
                    ["retryAfterMs"] = retryAfterMs,
                });
        }

        private void RemoveWriterLeaseWaiterLocked(WriterLeaseWaiter waiter)
        {
            if (waiter?.Node?.List == null) return;
            _writerLeaseWaiters.Remove(waiter.Node);
            PulseWriterLeaseWaitersLocked();
        }

        private void PulseWriterLeaseWaitersLocked()
        {
            foreach (var waiter in _writerLeaseWaiters)
                waiter.Signal.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewWriterLeaseSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;

        private sealed class WriterLeaseWaiter
        {
            public TaskCompletionSource<bool> Signal = NewWriterLeaseSignal();
            public LinkedListNode<WriterLeaseWaiter> Node;
        }

        private static string ShortSession(string session)
            => string.IsNullOrEmpty(session) || session.Length <= 8 ? session : session.Substring(0, 8);

        private DispatchResult Catalog(JObject arguments)
        {
            var validation = ValidateGateway(CatalogSchema, arguments);
            if (validation != null) return new DispatchResult(validation);

            var (isPlaying, isCompiling) = _stateProbe();
            var exactName = arguments.Value<string>("name");
            if (arguments["names"] is JArray names)
            {
                if (!string.IsNullOrEmpty(exactName)) return new DispatchResult(new DispatchError(McpErrorCodes.InvalidParams, "Use name or names, not both."));
                var batch = new JArray();
                foreach (var item in names)
                {
                    var name = (string)item;
                    batch.Add(_registry.TryGet(name, out var tool) ? DescribeTool(tool, isPlaying, isCompiling, true)
                        : new JObject { ["name"] = name, ["error"] = "not_found" });
                }
                return new DispatchResult(ToolResult.Ok(new JObject { ["registryRevision"] = _registry.Revision, ["tools"] = batch }));
            }
            if (!string.IsNullOrEmpty(exactName))
            {
                if (!_registry.TryGet(exactName, out var descriptor))
                    return new DispatchResult(new DispatchError(McpErrorCodes.MethodNotFound, $"Tool '{exactName}' not found."));
                return new DispatchResult(ToolResult.Ok(new JObject { ["registryRevision"] = _registry.Revision, ["tool"] = DescribeTool(descriptor, isPlaying, isCompiling, true) }));
            }

            var query = arguments.Value<string>("query") ?? string.Empty;
            var availableOnly = arguments.Value<bool?>("availableOnly") == true;
            var offset = arguments.Value<int?>("offset") ?? 0;
            var limit = arguments.Value<int?>("limit") ?? 25;
            var all = _registry.Enumerate().OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
            var matches = all.Where(tool =>
                    (!availableOnly || IsCurrentlyAvailable(tool, isPlaying, isCompiling))
                    && (query.Length == 0
                        || tool.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                        || (tool.Description?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0))
                .ToList();
            var tools = new JArray(matches.Skip(offset).Take(limit)
                .Select(tool => { var item = DescribeTool(tool, isPlaying, isCompiling, false);
                    if (arguments.Value<bool?>("includeDescriptions") == true) item["description"] = tool.Description;
                    return item; }));
            var result = new JObject
            {
                ["registryRevision"] = _registry.Revision,
                ["total"] = all.Count,
                ["filtered"] = matches.Count,
                ["offset"] = offset,
                ["returned"] = tools.Count,
                ["truncated"] = offset + tools.Count < matches.Count,
                ["tools"] = tools,
            };
            if (offset + tools.Count < matches.Count) result["nextOffset"] = offset + tools.Count;
            return new DispatchResult(ToolResult.Ok(result));
        }

        private static JObject DescribeTool(ToolDescriptor descriptor, bool isPlaying, bool isCompiling, bool full)
        {
            var result = new JObject
            {
                ["name"] = descriptor.Name,
                ["availability"] = descriptor.Availability.ToString(),
                ["currentlyAvailable"] = IsCurrentlyAvailable(descriptor, isPlaying, isCompiling),
            };
            if (!full) return result;

            result["description"] = descriptor.Description;
            result["playMode"] = DescribePlayMode(descriptor);
            result["inputSchema"] = descriptor.InputSchema ?? new JObject { ["type"] = "object" };
            if (descriptor.OutputSchema != null) result["outputSchema"] = descriptor.OutputSchema;
            if (descriptor.Annotations != null) result["annotations"] = descriptor.Annotations;
            result["requiresMainThread"] = descriptor.RequiresMainThread;
            result["reloadSafe"] = descriptor.ReloadSafe;
            result["execution"] = descriptor.Execution.ToString();
            var category = McpTrustPolicy.ResolveCategory(descriptor, new JObject());
            result["trustCategory"] = category.ToString();
            result["trustDecision"] = McpTrustPolicy.GetDecision(category).ToString();
            if (descriptor.Timeout != null) result["timeoutMs"] = (long)descriptor.Timeout.Value.TotalMilliseconds;
            if (!string.IsNullOrEmpty(descriptor.ExclusiveGroup)) result["exclusiveGroup"] = descriptor.ExclusiveGroup;
            return result;
        }

        private static DispatchError ValidateGateway(JObject schema, JObject arguments)
        {
            var errors = SchemaValidator.Validate(schema, arguments ?? new JObject()).ToArray();
            if (errors.Length == 0) return null;
            return new DispatchError(
                McpErrorCodes.ValidationFailed,
                "Arguments failed schema validation.",
                new JObject { ["errors"] = new JArray(errors.Select(error => new JObject { ["path"] = error.Path, ["message"] = error.Message })) });
        }

        private void CancelInFlight(JObject parameters, string scope)
        {
            var id = parameters["requestId"];
            var requestId = id == null ? null : RequestKey(scope, id);
            if (requestId != null && _inFlight.TryGetValue(requestId, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        private static string RequestKey(string scope, JToken id)
            => $"{scope ?? "default"}|{id.Type}:{id}";

        private static JObject SerializeToolResult(ToolResult result)
        {
            var content = new JArray();
            foreach (var block in result.Content)
            {
                switch (block)
                {
                    case TextContent text:
                        content.Add(new JObject { ["type"] = "text", ["text"] = text.Text });
                        break;
                    case ImageContent image:
                        content.Add(new JObject
                        {
                            ["type"] = "image",
                            ["data"] = System.Convert.ToBase64String(image.Data),
                            ["mimeType"] = image.MimeType,
                        });
                        break;
                    case ResourceLinkContent link:
                        content.Add(new JObject
                        {
                            ["type"] = "resource_link",
                            ["uri"] = link.Uri,
                            ["name"] = link.Name,
                            ["mimeType"] = link.MimeType,
                        });
                        break;
                }
            }

            var envelope = new JObject
            {
                ["content"] = content,
            };
            if (result.IsError)
                envelope["isError"] = true;
            if (result.StructuredContent != null)
                envelope["structuredContent"] = result.StructuredContent;
            return envelope;
        }
    }
}
