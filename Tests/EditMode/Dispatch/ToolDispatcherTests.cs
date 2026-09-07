using System;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class ToolDispatcherTests
    {
        private ToolDispatcher _dispatcher;
        private ToolRegistry _registry;

        [SetUp]
        public void Setup()
        {
            _registry = new ToolRegistry();
            _dispatcher = new ToolDispatcher(
                _registry,
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                new FakeLogSink(),
                () => (isPlaying: false, isCompiling: false));
        }

        private void RegisterTool(string name, ToolAvailability availability,
            System.Func<ToolContext, CancellationToken, ValueTask<ToolResult>> handler,
            JObject schema = null, TimeSpan? timeout = null, bool requiresMainThread = true,
            string exclusiveGroup = null)
        {
            _registry.SetEditorProviders(new IToolProvider[] { new InlineProvider(name, availability, handler, schema, timeout, requiresMainThread, exclusiveGroup) });
        }

        [Test]
        public async Task EditOnlyToolDoesNotRequestAnImplicitPlayModeTransition()
        {
            RegisterTool("probe.edit", ToolAvailability.EditMode, (_, _) => throw new Exception("Must not execute"));
            var authorizer = new TransitionProbeAuthorizer();
            var dispatcher = new ToolDispatcher(_registry, new FakeMainThreadPump(), new FakeFrameWaiter(), new FakeLogSink(), () => (true, false), authorizer: authorizer);
            var result = await dispatcher.DispatchAsync("probe.edit", new JObject(), "mode-check", CancellationToken.None);
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.ToolUnavailable));
            Assert.That(authorizer.Calls, Is.Zero);
        }

        private sealed class TransitionProbeAuthorizer : IToolAuthorizer
        {
            public int Calls;
            public ValueTask AuthorizeAsync(ToolDescriptor tool, JObject args, CancellationToken ct) { Calls++; return default; }
        }

        private sealed class InlineProvider : IToolProvider
        {
            private readonly ToolDescriptor _desc;

            public InlineProvider(string name, ToolAvailability availability,
                System.Func<ToolContext, CancellationToken, ValueTask<ToolResult>> handler, JObject schema,
                TimeSpan? timeout, bool requiresMainThread, string exclusiveGroup)
            {
                _desc = new ToolDescriptor
                {
                    Name = name,
                    Description = "",
                    InputSchema = schema ?? new JObject { ["type"] = "object" },
                    Availability = availability,
                    Timeout = timeout,
                    RequiresMainThread = requiresMainThread,
                    ExclusiveGroup = exclusiveGroup,
                    Handler = handler,
                };
            }

            public InlineProvider(ToolDescriptor descriptor) => _desc = descriptor;

            public string Namespace => "inline";
            public void RegisterTools(IToolRegistration reg) => reg.Register(_desc);
        }

        [Test]
        public async Task BackgroundRun_BlocksEditorWritesUntilCleanupButAllowsPolling()
        {
            var active = true;
            var writes = 0;
            var polls = 0;
            _registry.SetEditorProviders(new IToolProvider[]
            {
                new InlineProvider(new ToolDescriptor
                {
                    InputSchema = new JObject { ["type"] = "object" },
                    Name = "tests.run", ExclusiveGroup = "test", BackgroundOperationActive = () => active,
                    Handler = (_, _) => new ValueTask<ToolResult>(ToolResult.Text("started")),
                }),
                new InlineProvider(new ToolDescriptor
                {
                    InputSchema = new JObject { ["type"] = "object" },
                    Name = "tests.result", ExclusiveGroup = "test",
                    Handler = (_, _) => { polls++; return new ValueTask<ToolResult>(ToolResult.Text("restoring")); },
                }),
                new InlineProvider(new ToolDescriptor
                {
                    InputSchema = new JObject { ["type"] = "object" },
                    Name = "scene.write",
                    Handler = (_, _) => { writes++; return new ValueTask<ToolResult>(ToolResult.Text("ok")); },
                }),
                new InlineProvider(new ToolDescriptor
                {
                    InputSchema = new JObject { ["type"] = "object" },
                    Name = "editor.status", RequiresMainThread = false, TrustCategory = ToolTrustCategory.Read,
                    Handler = (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")),
                }),
            });
            var blocked = await _dispatcher.DispatchAsync("scene.write", new JObject(), "write", CancellationToken.None);
            Assert.That(blocked.Error.Code, Is.EqualTo(McpErrorCodes.ExclusiveBusy));
            Assert.That(writes, Is.Zero);
            Assert.That((await _dispatcher.DispatchAsync("tests.result", new JObject(), "poll", CancellationToken.None)).Error, Is.Null);
            Assert.That(polls, Is.EqualTo(1));
            Assert.That((await _dispatcher.DispatchAsync("editor.status", new JObject(), "status", CancellationToken.None)).Error, Is.Null);
            active = false;
            Assert.That((await _dispatcher.DispatchAsync("scene.write", new JObject(), "write", CancellationToken.None)).Error, Is.Null);
            Assert.That(writes, Is.EqualTo(1));
        }

        [Test]
        public async Task Dispatch_UnknownTool_ReturnsMethodNotFound()
        {
            var result = await _dispatcher.DispatchAsync("no.such.tool", new JObject(), "req-1", CancellationToken.None);
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.MethodNotFound));
        }

        [Test]
        public async Task Dispatch_ToolUnavailable_ReturnsToolUnavailable()
        {
            RegisterTool("play.only", ToolAvailability.PlayMode, (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")));
            var result = await _dispatcher.DispatchAsync("play.only", new JObject(), "req-1", CancellationToken.None);
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.ToolUnavailable));
        }

        [Test]
        public async Task Dispatch_ValidationFailure_ReturnsValidationFailed()
        {
            var schema = JObject.Parse(@"{ ""type"": ""object"", ""required"": [""name""], ""properties"": { ""name"": { ""type"": ""string"" } } }");
            RegisterTool("needs.name", ToolAvailability.Either, (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")), schema);
            var result = await _dispatcher.DispatchAsync("needs.name", new JObject(), "req-1", CancellationToken.None);
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.ValidationFailed));
        }

        [Test]
        public async Task Dispatch_Success_ReturnsResult()
        {
            RegisterTool("test.ok", ToolAvailability.Either, (_, _) => new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["hello"] = "world" })));
            var result = await _dispatcher.DispatchAsync("test.ok", new JObject(), "req-1", CancellationToken.None);
            Assert.That(result.Error, Is.Null);
            Assert.That((string)result.Value.StructuredContent["hello"], Is.EqualTo("world"));
        }

        [Test]
        public async Task Dispatch_AuthorizationFailure_PreventsHandlerExecution()
        {
            var invoked = false;
            RegisterTool("test.protected", ToolAvailability.Either, (_, _) =>
            {
                invoked = true;
                return new ValueTask<ToolResult>(ToolResult.Text("unexpected"));
            });
            _dispatcher = new ToolDispatcher(
                _registry,
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                new FakeLogSink(),
                () => (isPlaying: false, isCompiling: false),
                authorizer: new RejectingAuthorizer());

            var result = await _dispatcher.DispatchAsync(
                "test.protected", new JObject(), "req-auth", CancellationToken.None);

            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.ToolUnavailable));
            Assert.That(invoked, Is.False);
        }

        [Test]
        public async Task Dispatch_ToolException_MapsCode()
        {
            RegisterTool("test.boom", ToolAvailability.Either, (_, _) => throw new McpToolException(McpErrorCodes.NotFound, "missing"));
            var result = await _dispatcher.DispatchAsync("test.boom", new JObject(), "req-1", CancellationToken.None);
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.NotFound));
            Assert.That(result.Error.Message, Is.EqualTo("missing"));
        }

        [Test]
        public async Task Dispatch_UnexpectedException_MapsToToolError()
        {
            RegisterTool("test.oops", ToolAvailability.Either, (_, _) => throw new System.InvalidOperationException("oops"));
            var result = await _dispatcher.DispatchAsync("test.oops", new JObject(), "req-1", CancellationToken.None);
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.ToolError));
        }

        [Test]
        public async Task Dispatch_ToolTimeout_ReturnsTimeoutError()
        {
            // Tool sleeps longer than its 100ms timeout — dispatcher should cancel it.
            RegisterTool("test.slow", ToolAvailability.Either, async (_, ct) =>
            {
                await Task.Delay(1000, ct);
                return ToolResult.Text("never");
            }, timeout: TimeSpan.FromMilliseconds(100));

            var result = await _dispatcher.DispatchAsync("test.slow", new JObject(), "req-1", CancellationToken.None);
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.ToolError));
            Assert.That(result.Error.Message, Does.Contain("timed out"));
        }

        [Test]
        public async Task Dispatch_ToolTimeout_IncludesEditorDiagnostics()
        {
            _dispatcher = new ToolDispatcher(
                _registry,
                new StalledMainThreadPump(12_345),
                new FakeFrameWaiter(),
                new FakeLogSink(),
                () => (isPlaying: false, isCompiling: true));

            RegisterTool("slow.background", ToolAvailability.Always, async (_, ct) =>
            {
                await Task.Delay(1000, ct);
                return ToolResult.Text("never");
            }, timeout: TimeSpan.FromMilliseconds(100), requiresMainThread: false);

            var result = await _dispatcher.DispatchAsync("slow.background", new JObject(), "req-1", CancellationToken.None);

            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.ToolError));
            Assert.That(result.Error.Data, Is.Not.Null);
            Assert.That((string)result.Error.Data["tool"], Is.EqualTo("slow.background"));
            Assert.That((bool)result.Error.Data["isCompiling"], Is.True);
            Assert.That((long)result.Error.Data["mainThreadStalledMs"], Is.EqualTo(12_345));
            Assert.That((string)result.Error.Data["hint"], Does.Contain("No Throttling"));
        }

        [Test]
        public async Task Dispatch_ClientCancellation_ReturnsCancelledError()
        {
            // Distinct from timeout: when the outer token is cancelled, error should be Cancelled not ToolError.
            RegisterTool("test.slow", ToolAvailability.Either, async (_, ct) =>
            {
                await Task.Delay(1000, ct);
                return ToolResult.Text("never");
            }, timeout: TimeSpan.FromSeconds(10));

            using var cts = new CancellationTokenSource();
            var task = _dispatcher.DispatchAsync("test.slow", new JObject(), "req-1", cts.Token);
            cts.CancelAfter(50);
            var result = await task;
            Assert.That(result.Error.Code, Is.EqualTo(McpErrorCodes.Cancelled));
        }

        [Test]
        public async Task Dispatch_CancelledMainThreadWork_HoldsExclusiveLockUntilHandlerSettles()
        {
            using var pump = new MainThreadPump(autoTick: false);
            _dispatcher = new ToolDispatcher(
                _registry,
                pump,
                new FakeFrameWaiter(),
                new FakeLogSink(),
                () => (isPlaying: false, isCompiling: false));

            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var invocationCount = 0;
            RegisterTool("test.exclusive", ToolAvailability.Either, async (_, _) =>
            {
                if (Interlocked.Increment(ref invocationCount) == 1)
                {
                    await releaseFirst.Task;
                    firstFinished.TrySetResult(true);
                }
                return ToolResult.Text("ok");
            }, timeout: TimeSpan.FromMilliseconds(100), exclusiveGroup: "asset-write");

            using var firstCts = new CancellationTokenSource();
            var first = _dispatcher.DispatchAsync("test.exclusive", new JObject(), "req-1", firstCts.Token);
            pump.PumpOnce();
            Assert.That(Volatile.Read(ref invocationCount), Is.EqualTo(1));

            firstCts.Cancel();
            Assert.That((await first).Error.Code, Is.EqualTo(McpErrorCodes.Cancelled));

            var blocked = await _dispatcher.DispatchAsync("test.exclusive", new JObject(), "req-2", CancellationToken.None);
            Assert.That(blocked.Error.Code, Is.EqualTo(McpErrorCodes.ExclusiveBusy));
            Assert.That(Volatile.Read(ref invocationCount), Is.EqualTo(1));

            releaseFirst.SetResult(true);
            await firstFinished.Task;

            var third = _dispatcher.DispatchAsync("test.exclusive", new JObject(), "req-3", CancellationToken.None);
            pump.PumpOnce();
            Assert.That((await third).Error, Is.Null);
            Assert.That(Volatile.Read(ref invocationCount), Is.EqualTo(2));
        }

        private sealed class StalledMainThreadPump : IMainThreadScheduler
        {
            private readonly long _millisecondsSinceLastTick;

            public StalledMainThreadPump(long millisecondsSinceLastTick)
            {
                _millisecondsSinceLastTick = millisecondsSinceLastTick;
            }

            public ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken ct)
                => work(ct);

            public ValueTask RunAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct)
                => work(ct);

            public long MillisecondsSinceLastTick => _millisecondsSinceLastTick;
        }

        private sealed class RejectingAuthorizer : IToolAuthorizer
        {
            public ValueTask AuthorizeAsync(ToolDescriptor tool, JObject arguments, CancellationToken ct)
                => throw new McpToolException(McpErrorCodes.ToolUnavailable, "denied by test policy");
        }
    }
}
