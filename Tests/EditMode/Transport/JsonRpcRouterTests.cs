using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Editor.Transport;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Transport
{
    public class JsonRpcRouterTests
    {
        private JsonRpcRouter _router;

        [SetUp]
        public void Setup()
        {
            var registry = new ToolRegistry();
            var dispatcher = new ToolDispatcher(
                registry,
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                new FakeLogSink(),
                () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false));
        }

        [Test]
        public async Task Initialize_ReturnsCapabilities()
        {
            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 1, ""method"": ""initialize"", ""params"": {} }");
            var response = await _router.HandleAsync(request, CancellationToken.None);
            Assert.That((int)response["id"], Is.EqualTo(1));
            Assert.That(response["result"]["capabilities"]["tools"]["listChanged"].Value<bool>(), Is.True);
            Assert.That(response["result"]["capabilities"]["logging"], Is.Not.Null);
        }

        [Test]
        public async Task Ping_ReturnsEmptyResult()
        {
            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 2, ""method"": ""ping"" }");
            var response = await _router.HandleAsync(request, CancellationToken.None);
            Assert.That(response["result"], Is.Not.Null);
        }

        [Test]
        public async Task UnknownMethod_ReturnsMethodNotFound()
        {
            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 3, ""method"": ""does.not.exist"" }");
            var response = await _router.HandleAsync(request, CancellationToken.None);
            Assert.That((int)response["error"]["code"], Is.EqualTo(McpErrorCodes.MethodNotFound));
        }

        [Test]
        public async Task ToolsList_ReturnsOnlyGatewayTools()
        {
            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 4, ""method"": ""tools/list"" }");
            var response = await _router.HandleAsync(request, CancellationToken.None);
            var tools = (JArray)response["result"]["tools"];
            Assert.That(tools.Select(tool => (string)tool["name"]), Is.EqualTo(new[] { "unity.tools", "unity.call" }));
            Assert.That(response.ToString(Newtonsoft.Json.Formatting.None).Length, Is.LessThan(1500));
        }

        [Test]
        public async Task GatewayCatalog_AcceptsPageSize128()
        {
            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 5, ""method"": ""tools/call"", ""params"": { ""name"": ""unity.tools"", ""arguments"": { ""availableOnly"": true, ""limit"": 128 } } }");
            var response = await _router.HandleAsync(request, CancellationToken.None);

            Assert.That(response["error"], Is.Null);
            Assert.That((int)response["result"]["structuredContent"]["returned"], Is.Zero);
        }

        [Test]
        public void ReloadSafeToolNames_ComesFromRegisteredDescriptors()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.write", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok"))),
                new InlineProvider("probe.read", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")), reloadSafe: true),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                new FakeLogSink(), () => (false, false));
            var router = new JsonRpcRouter(registry, dispatcher, () => (false, false));

            Assert.That(router.ReloadSafeToolNames(), Is.EqualTo(new[] { "probe.read" }));
        }

        [Test]
        public async Task WriterLease_CachedResultPollingDoesNotWaitForAnotherSession()
        {
            var started = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.write", async (_, _) =>
                {
                    started.SetResult(true);
                    await release.Task;
                    return ToolResult.Text("ok");
                }),
                new InlineProvider("tests.result", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("restoring")),
                    requiresWriterLease: false),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                new FakeLogSink(), () => (false, false));
            var router = new JsonRpcRouter(registry, dispatcher, () => (false, false));
            var write = router.HandleAsync(JObject.Parse(@"{ ""id"": 1, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.write"" } }"), CancellationToken.None, "writer");
            await started.Task;
            try
            {
                var poll = router.HandleAsync(JObject.Parse(@"{ ""id"": 2, ""method"": ""tools/call"", ""params"": { ""name"": ""tests.result"" } }"), CancellationToken.None, "reader");
                Assert.That(poll.IsCompleted, Is.True, "Read-only polling must not queue behind the writer.");
                Assert.That((await poll)["error"], Is.Null);
            }
            finally
            {
                release.TrySetResult(true);
                await write;
            }
        }

        [Test]
        public async Task WriterLease_QueuesAnotherSessionUntilActiveCallCompletes()
        {
            var started = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.write", async (_, _) =>
                {
                    started.TrySetResult(true);
                    await release.Task;
                    return ToolResult.Text("ok");
                }),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                new FakeLogSink(), () => (false, false));
            var router = new JsonRpcRouter(registry, dispatcher, () => (false, false),
                writerLeaseWaitTimeout: TimeSpan.FromSeconds(1));
            var firstRequest = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 1, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.write"" } }");
            var secondRequest = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 2, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.write"" } }");

            var first = router.HandleAsync(firstRequest, CancellationToken.None, "session-a");
            await started.Task;
            var queued = router.HandleAsync(secondRequest, CancellationToken.None, "session-b");

            Assert.That(queued.IsCompleted, Is.False);

            Assert.That((string)router.WriterStatus()["state"], Is.EqualTo("active"));
            Assert.That((int)router.WriterStatus()["queuedCalls"], Is.EqualTo(1));

            release.SetResult(true);
            await first;
            var accepted = await queued;
            Assert.That(accepted["error"], Is.Null);
            Assert.That((string)router.WriterStatus()["state"], Is.EqualTo("available"));
            Assert.That((int)router.WriterStatus()["queuedCalls"], Is.Zero);
        }

        [Test]
        public async Task GatewayCatalog_SearchIncludesTransientlyUnavailableTools()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.play_only", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Text("ok")), ToolAvailability.PlayMode),
            });
            var dispatcher = new ToolDispatcher(
                registry,
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                new FakeLogSink(),
                () => (false, true));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, true));

            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 5, ""method"": ""tools/call"", ""params"": { ""name"": ""unity.tools"", ""arguments"": { ""query"": ""probe."" } } }");
            var response = await _router.HandleAsync(request, CancellationToken.None);

            var tools = (JArray)response["result"]["structuredContent"]["tools"];
            Assert.That(tools.Select(t => (string)t["name"]), Contains.Item("probe.play_only"));
            var entry = (JObject)tools.First(t => (string)t["name"] == "probe.play_only");
            Assert.That((bool)entry["currentlyAvailable"], Is.False);
            Assert.That((string)entry["availability"], Is.EqualTo("PlayMode"));

            request["params"]["arguments"]["availableOnly"] = true;
            response = await _router.HandleAsync(request, CancellationToken.None);
            Assert.That((JArray)response["result"]["structuredContent"]["tools"], Is.Empty);
        }

        [Test]
        public async Task WriterLease_CancelledQueuedRequestReportsNotStarted()
        {
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            var calls = 0;
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.write", async (_, _) =>
                {
                    calls++;
                    entered.TrySetResult(true);
                    await release.Task;
                    return ToolResult.Text("ok");
                }),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                new FakeLogSink(), () => (false, false));
            var router = new JsonRpcRouter(registry, dispatcher, () => (false, false));
            var request = JObject.Parse(@"{'id':1,'method':'tools/call','params':{'name':'probe.write'}}");
            var first = router.HandleAsync(request, CancellationToken.None, "first");
            await entered.Task;
            using var cancellation = new CancellationTokenSource();
            try
            {
                request = (JObject)request.DeepClone();
                request["id"] = 2;
                var queued = router.HandleAsync(request, cancellation.Token, "second");
                Assert.That(queued.IsCompleted, Is.False);
                cancellation.Cancel();
                var response = await queued;
                Assert.That((string)response["error"]?["data"]?["executionState"], Is.EqualTo("not_started"));
                Assert.That(calls, Is.EqualTo(1));
            }
            finally
            {
                release.TrySetResult(true);
                await first;
            }
        }

        [Test]
        public async Task GatewayCatalog_GetReturnsSchemaAndPlayModeMetadata()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.allowed", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok"))),
                new InlineProvider("probe.unavailable", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")), ToolAvailability.EditMode),
                new InlineProvider("probe.required", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")), ToolAvailability.PlayMode),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                new FakeLogSink(), () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false));

            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 6, ""method"": ""tools/call"", ""params"": { ""name"": ""unity.tools"", ""arguments"": { ""name"": ""probe.required"" } } }");
            var response = await _router.HandleAsync(request, CancellationToken.None);
            var tool = response["result"]["structuredContent"]["tool"];

            Assert.That((string)tool["name"], Is.EqualTo("probe.required"));
            Assert.That((string)tool["playMode"], Is.EqualTo("required"));
            Assert.That((string)tool["inputSchema"]["type"], Is.EqualTo("object"));
        }

        [Test]
        public async Task ToolCall_WithStructuredResult_DoesNotDuplicateJsonAsText()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.status", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                    {
                        ["success"] = true,
                        ["errorCount"] = 0,
                    }))),
            });
            var dispatcher = new ToolDispatcher(
                registry,
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                new FakeLogSink(),
                () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false));

            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 5, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.status"", ""arguments"": {} } }");
            var response = await _router.HandleAsync(request, CancellationToken.None);

            var content = (JArray)response["result"]["content"];
            Assert.That(content, Is.Empty);
            Assert.That(response["result"]["isError"], Is.Null);
            Assert.That((bool)response["result"]["structuredContent"]["success"], Is.True);
        }

        [Test]
        public async Task GatewayCall_DispatchesThroughTargetValidation()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider(
                    "probe.required",
                    (ctx, _) => new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["value"] = ctx.Arguments["value"] })),
                    inputSchema: JObject.Parse(@"{ ""type"": ""object"", ""required"": [""value""], ""properties"": { ""value"": { ""type"": ""integer"" } } }") ),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                new FakeLogSink(), () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false));

            var valid = await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 7, ""method"": ""tools/call"", ""params"": { ""name"": ""unity.call"", ""arguments"": { ""tool"": ""probe.required"", ""arguments"": { ""value"": 42 } } } }"), CancellationToken.None);
            Assert.That((int)valid["result"]["structuredContent"]["value"], Is.EqualTo(42));

            var invalid = await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 8, ""method"": ""tools/call"", ""params"": { ""name"": ""unity.call"", ""arguments"": { ""tool"": ""probe.required"", ""arguments"": {} } } }"), CancellationToken.None);
            Assert.That((int)invalid["error"]["code"], Is.EqualTo(McpErrorCodes.ValidationFailed));
        }

        [Test]
        public async Task ToolCall_LogsFunctionAndResponseInOneEntry()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.status", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["success"] = true }))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(
                registry,
                dispatcher,
                () => (false, false),
                log,
                LittleBrushGames.Mcp.Editor.Host.McpToolCallLogMode.Compact);

            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 11, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.status"" } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].level, Is.EqualTo(LogLevel.Info));
            Assert.That(log.Entries[0].message, Does.StartWith("'probe.status' [cid=1, id=11] - ok\nResponse: {"));
            Assert.That(log.Entries[0].message, Does.Not.Contain("\"jsonrpc\""));
            Assert.That(log.Entries[0].message, Does.Contain(@"""structuredKeys"""));
            Assert.That(log.Entries[0].message, Does.Not.Contain(@"""text"""));
        }

        [Test]
        public async Task ToolCall_DefaultLogMode_SummaryOmitsResponseBody()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.status", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["success"] = true }))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 14, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.status"" } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].message, Is.EqualTo("'probe.status' - ok"));
            Assert.That(log.Entries[0].message, Does.Not.Contain("Response:"));
            Assert.That(log.Entries[0].message, Does.Not.Contain("cid="));
        }

        [Test]
        public async Task ToolCall_SummaryShowsArgumentsAndResultCounts()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.find", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                    {
                        ["count"] = 3,
                        ["returned"] = 2,
                        ["truncated"] = true,
                        ["matches"] = new JArray(1, 2),
                    }))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 21, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.find"", ""arguments"": { ""name"": ""Crate"", ""includeInactive"": true, ""limit"": 2 } } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].message, Does.Contain("args={\"name\":\"Crate\",\"includeInactive\":true,\"limit\":2}"));
            Assert.That(log.Entries[0].message, Does.Contain("(count=3, returned=2, truncated=true)"));
            Assert.That(log.Entries[0].message, Does.Not.Contain("Response:"));
        }

        [Test]
        public async Task ToolCall_SummaryBoundsPayloadArgumentsAndRedactsTokens()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.write", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Text("ok"))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 22,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "probe.write",
                    ["arguments"] = new JObject
                    {
                        ["source"] = new string('x', 200),
                        ["token"] = "secret-value",
                        ["operations"] = new JArray(new JObject
                        {
                            ["type"] = "rename_gameobject",
                            ["path"] = "Root",
                        }),
                    },
                },
            };

            await _router.HandleAsync(request, CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].message, Does.Contain("\"source\":{\"length\":200"));
            Assert.That(log.Entries[0].message, Does.Contain("\"token\":\"<redacted>\""));
            Assert.That(log.Entries[0].message, Does.Contain("\"operations\":{\"count\":1,\"preview\":[\"rename_gameobject\"]}"));
            Assert.That(log.Entries[0].message, Does.Not.Contain("secret-value"));
        }

        [Test]
        public async Task ToolCallLogMode_DisabledSuppressesEntry()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.status", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Text("ok"))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(
                registry,
                dispatcher,
                () => (false, false),
                log,
                LittleBrushGames.Mcp.Editor.Host.McpToolCallLogMode.Disabled);

            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 16, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.status"" } }"), CancellationToken.None);

            Assert.That(log.Entries, Is.Empty);
        }

        [Test]
        public async Task ToolCall_UnexpectedException_IsOneErrorEntry()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.failure", (_, _) =>
                    throw new System.InvalidOperationException("boom")),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 15, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.failure"" } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].level, Is.EqualTo(LogLevel.Error));
            Assert.That(log.Entries[0].message, Does.Contain("Exception: InvalidOperationException: boom"));
        }

        [Test]
        public async Task ToolCall_ValidationFailure_IsOneWarningEntry()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider(
                    "probe.validation",
                    (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")),
                    ToolAvailability.Either,
                    inputSchema: JObject.Parse(@"{ ""type"": ""object"", ""required"": [""name""] }")),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            var response = await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 17, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.validation"", ""arguments"": {} } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].level, Is.EqualTo(LogLevel.Warn));
            Assert.That(log.Entries[0].message, Does.Contain("- rejected"));
            Assert.That(log.Entries[0].message, Does.Not.Contain("- error"));
            Assert.That((int)response["error"]["code"], Is.EqualTo(McpErrorCodes.ValidationFailed));
        }

        [Test]
        public async Task RoslynToolCall_IsWarningOnSuccessAndExpectedFailure()
        {
            var log = new FakeLogSink();
            var shouldFail = false;
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("editor.execute_code", (_, _) =>
                    new ValueTask<ToolResult>(shouldFail
                        ? ToolResult.Error("compile failed")
                        : ToolResult.Ok(new JObject { ["success"] = true }))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 12, ""method"": ""tools/call"", ""params"": { ""name"": ""editor.execute_code"" } }"), CancellationToken.None);
            shouldFail = true;
            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 13, ""method"": ""tools/call"", ""params"": { ""name"": ""editor.execute_code"" } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(2));
            Assert.That(log.Entries[0].level, Is.EqualTo(LogLevel.Warn));
            Assert.That(log.Entries[1].level, Is.EqualTo(LogLevel.Warn));
            Assert.That(log.Entries[1].message, Does.Contain("- rejected"));
            Assert.That(log.Entries[1].message, Does.Not.Contain("- error"));
        }

        [Test]
        public async Task ToolCall_UnexpectedDiagnosticResult_IsErrorEntry()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.internal", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.ErrorWithData(
                        "internal invariant failed",
                        new JObject { ["success"] = false },
                        expectedFailure: false))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 20, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.internal"" } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].level, Is.EqualTo(LogLevel.Error));
            Assert.That(log.Entries[0].message, Does.Contain("- error"));
        }

        [Test]
        public async Task ToolCall_CompilingRejection_IsInfoEntry()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.status", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Text("ok"))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, true));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, true), log);

            var response = await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 18, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.status"" } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].level, Is.EqualTo(LogLevel.Info));
            Assert.That(log.Entries[0].message, Does.Contain("- rejected"));
            Assert.That(log.Entries[0].message, Does.Not.Contain("- error"));
            Assert.That((int)response["error"]["code"], Is.EqualTo(McpErrorCodes.Compiling));
        }

        [Test]
        public async Task ToolCall_StructuredFailure_IsWarningEntry()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.compile", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                    {
                        ["success"] = false,
                        ["errorCount"] = 1,
                    }))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 19, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.compile"" } }"), CancellationToken.None);

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].level, Is.EqualTo(LogLevel.Warn));
            Assert.That(log.Entries[0].message, Does.Contain("- warning"));
            Assert.That(log.Entries[0].message, Does.Contain("diagnostic: 1 error"));
            Assert.That(log.Entries[0].message, Does.Not.Contain("- error"));
        }

        [Test]
        public async Task ToolCall_NestedStructuredStatus_DoesNotBreakLogging()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.ready", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                    {
                        ["ready"] = true,
                        ["status"] = new JObject { ["isPlaying"] = false },
                    }))),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            var response = await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 20, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.ready"" } }"), CancellationToken.None);

            Assert.That(response["result"], Is.Not.Null);
            Assert.That(log.Entries, Has.Count.EqualTo(1));
        }

        [Test]
        public void FutureRoslynToolNames_AreWarnings()
        {
            Assert.That(McpToolCallLogger.IsRoslynTool("editor.roslyn_compile"), Is.True);
            Assert.That(McpToolCallLogger.IsRoslynTool("editor.execute_code"), Is.True);
            Assert.That(McpToolCallLogger.IsRoslynTool("editor.scene_read"), Is.False);
        }

        [Test]
        public async Task ToolCall_UsesCallerProgressToken()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.progress", (ctx, _) =>
                {
                    ctx.Progress.Report(0.5, "half");
                    return new ValueTask<ToolResult>(ToolResult.Text("ok"));
                }),
            });
            var notifications = new NotificationBroadcaster(new FakeLogSink());
            JObject captured = null;
            notifications.NotificationSent += value => captured = value;
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                new FakeLogSink(), () => (false, false), notifications);
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false));

            var request = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 9, ""method"": ""tools/call"", ""params"": { ""name"": ""probe.progress"", ""arguments"": {}, ""_meta"": { ""progressToken"": ""caller-token"" } } }");
            await _router.HandleAsync(request, CancellationToken.None);

            Assert.That((string)captured?["params"]?["progressToken"], Is.EqualTo("caller-token"));
        }

        [Test]
        public async Task GatewayCatalog_GetPublishesOutputSchemaAndAnnotations()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.described", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")),
                    outputSchema: new JObject { ["type"] = "object" },
                    annotations: new JObject { ["readOnlyHint"] = true }),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                new FakeLogSink(), () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false));

            var response = await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 10, ""method"": ""tools/call"", ""params"": { ""name"": ""unity.tools"", ""arguments"": { ""name"": ""probe.described"" } } }"), CancellationToken.None);
            var tool = response["result"]["structuredContent"]["tool"];
            Assert.That((string)tool["outputSchema"]["type"], Is.EqualTo("object"));
            Assert.That((bool)tool["annotations"]["readOnlyHint"], Is.True);
        }

        [Test]
        public async Task ToolCall_RecordsResponseMetricsAndWarnsOnceForLargeDefaults()
        {
            var log = new FakeLogSink();
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider("probe.large-default", (_, _) =>
                    new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                    {
                        ["payload"] = new string('x', McpToolMetrics.LargeResponseWarningCharacters),
                    }))),
                new EditorStatusProvider(),
            });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(),
                log, () => (false, false));
            _router = new JsonRpcRouter(registry, dispatcher, () => (false, false), log);

            var call = JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 30, ""method"": ""tools/call"", ""params"": { ""name"": ""unity.call"", ""arguments"": { ""tool"": ""probe.large-default"", ""arguments"": {} } } }");
            await _router.HandleAsync(call, CancellationToken.None);
            await _router.HandleAsync(call, CancellationToken.None);

            Assert.That(log.Entries.Count(entry => entry.level == LogLevel.Warn
                                                   && entry.message.Contains("probe.large-default")), Is.EqualTo(1));

            var metrics = await _router.HandleAsync(JObject.Parse(@"{ ""jsonrpc"": ""2.0"", ""id"": 31, ""method"": ""tools/call"", ""params"": { ""name"": ""unity.call"", ""arguments"": { ""tool"": ""editor.metrics"", ""arguments"": { ""query"": ""probe.large-default"" } } } }"), CancellationToken.None);
            var entry = metrics["result"]["structuredContent"]["tools"][0];

            Assert.That((long)entry["calls"], Is.EqualTo(2));
            Assert.That((long)entry["maxResponseCharacters"], Is.GreaterThan(McpToolMetrics.LargeResponseWarningCharacters));
            Assert.That((long)entry["maxDurationMs"], Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public async Task UsageCallbackRecordsResolvedToolWithConsoleDisabled()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[] { new InlineProvider("probe.usage", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok"))) });
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(), new FakeLogSink(), () => (false, false));
            string recordedName = null;
            JObject recordedResponse = null;
            var router = new JsonRpcRouter(registry, dispatcher, () => (false, false), toolCallLogMode: LittleBrushGames.Mcp.Editor.Host.McpToolCallLogMode.Disabled,
                recordUsage: (name, args, response, duration, writerWait, dispatch, responseCharacters) => { recordedName = name; recordedResponse = response; });
            await router.HandleAsync(JObject.Parse(@"{ 'id': 1, 'method': 'tools/call', 'params': { 'name': 'unity.call', 'arguments': { 'tool': 'probe.usage' } } }"), CancellationToken.None);
            Assert.That(recordedName, Is.EqualTo("probe.usage"));
            Assert.That(recordedResponse["result"], Is.Not.Null);
        }
        [Test]
        public async Task GatewayCatalogBatchesSchemasAndChangesRevisionOnRebuild()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[] { new InlineProvider("probe.batch", (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok"))) });
            var original = registry.Revision;
            var dispatcher = new ToolDispatcher(registry, new FakeMainThreadPump(), new FakeFrameWaiter(), new FakeLogSink(), () => (false, false));
            var router = new JsonRpcRouter(registry, dispatcher, () => (false, false));
            var response = await router.HandleAsync(JObject.Parse(@"{'id':1,'method':'tools/call','params':{'name':'unity.tools','arguments':{'names':['probe.batch','missing.tool']}}}"), CancellationToken.None);
            var content = response["result"]["structuredContent"];
            Assert.That((string)content["registryRevision"], Is.EqualTo(original));
            Assert.That(content["tools"][0]["inputSchema"], Is.Not.Null);
            Assert.That((string)content["tools"][1]["error"], Is.EqualTo("not_found"));
            registry.SetRuntimeProviders(Array.Empty<IToolProvider>());
            Assert.That(registry.Revision, Is.Not.EqualTo(original));
        }

        private sealed class InlineProvider : IToolProvider
        {
            private readonly ToolDescriptor _desc;

            public InlineProvider(
                string name,
                System.Func<ToolContext, CancellationToken, ValueTask<ToolResult>> handler,
                ToolAvailability availability = ToolAvailability.Either,
                JObject outputSchema = null,
                JObject annotations = null,
                JObject inputSchema = null,
                bool reloadSafe = false,
                bool requiresWriterLease = true)
            {
                _desc = new ToolDescriptor
                {
                    Name = name,
                    Description = "",
                    InputSchema = inputSchema ?? new JObject { ["type"] = "object" },
                    Availability = availability,
                    OutputSchema = outputSchema,
                    Annotations = annotations,
                    ReloadSafe = reloadSafe,
                    RequiresWriterLease = requiresWriterLease,
                    Handler = handler,
                };
            }

            public string Namespace => "inline";
            public void RegisterTools(IToolRegistration reg) => reg.Register(_desc);
        }
    }
}
