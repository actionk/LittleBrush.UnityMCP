using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class AttributedToolProviderTests
    {
        private enum Mode { Fast, Full }
        private sealed class SampleProvider : AttributedToolProvider
        {
            public override string Namespace => "sample";
            public int Calls;

            [McpTool("sample.run", "Exercise typed arguments and injected context.", ToolTrustCategory.ProjectWrite,
                RequiresMainThread = false, DestructiveHint = false, TimeoutMs = 5000)]
            private JObject Run(ToolContext context, CancellationToken cancellation,
                [McpParameter(MinLength = 1, MaxLength = 20)] string name,
                [McpParameter(Minimum = 1, Maximum = 10)] int count = 2,
                Mode mode = Mode.Fast, [McpParameter(MaxItems = 3)] string[] labels = null, int? offset = null)
            {
                cancellation.ThrowIfCancellationRequested(); Calls++;
                return new JObject { ["name"] = name, ["count"] = count, ["mode"] = mode.ToString(),
                    ["labels"] = labels == null ? JValue.CreateNull() : new JArray(labels),
                    ["offset"] = offset, ["requestId"] = context.RequestId };
            }

            [McpTool("sample.async", "Async typed response.", ToolTrustCategory.Read)]
            private static async ValueTask<JObject> Async()
            { await Task.Yield(); return new JObject { ["ok"] = true }; }

            [McpTool("sample.error", "Preserve the original validation error.", ToolTrustCategory.Read)]
            private static ToolResult Error() => throw new McpToolException(McpErrorCodes.ValidationFailed, "Expected failure");

            public void Unmarked() => throw new Exception("Never expose this method.");
        }

        private sealed class InvalidProvider : AttributedToolProvider
        {
            public override string Namespace => "invalid";
            [McpTool("invalid.good", "Must not leak from a rejected provider.", ToolTrustCategory.Read)]
            public JObject AValid() => new();
            [McpTool("invalid.bad", "Unsupported signature.", ToolTrustCategory.Read)]
            public JObject ZInvalid(UnityEngine.GameObject target) => new();
        }

        private sealed class RejectingAuthorizer : IToolAuthorizer
        {
            public ToolTrustCategory Seen;
            public ValueTask AuthorizeAsync(ToolDescriptor tool, JObject args, CancellationToken ct)
            { Seen = tool.TrustCategory; throw new McpToolException(McpErrorCodes.ValidationFailed, "Denied"); }
        }

        private static ToolRegistry Registry(IToolProvider provider)
        { var registry = new ToolRegistry(); registry.SetEditorProviders(new[] { provider }); return registry; }
        private static ToolDispatcher Dispatcher(ToolRegistry registry, bool playing = false, IToolAuthorizer authorizer = null)
            => new(registry, new FakeMainThreadPump(), new FakeFrameWaiter(), new FakeLogSink(), () => (playing, false), authorizer: authorizer);

        [Test]
        public async Task TypedCallBindsDefaultsEnumsArraysNullableAndInjectedValues()
        {
            var provider = new SampleProvider(); var registry = Registry(provider);
            Assert.That(registry.Errors, Is.Empty);
            Assert.That(registry.Enumerate().Count(), Is.EqualTo(3));
            var dispatcher = Dispatcher(registry);
            var first = await dispatcher.DispatchAsync("sample.run", JObject.Parse("{'name':'Oak'}"), "request-one", CancellationToken.None);
            Assert.That(first.Error, Is.Null);
            var result = first.Value.StructuredContent;
            Assert.That((int)result["count"], Is.EqualTo(2));
            Assert.That((string)result["mode"], Is.EqualTo("Fast"));
            Assert.That((string)result["requestId"], Is.EqualTo("request-one"));
            Assert.That(result["offset"].Type, Is.EqualTo(JTokenType.Null));
            var explicitValues = await dispatcher.DispatchAsync("sample.run", JObject.Parse("{'name':'Cabin','count':3,'mode':'Full','labels':['a','b'],'offset':4}"), "two", CancellationToken.None);
            Assert.That(explicitValues.Error, Is.Null);
            Assert.That((string)explicitValues.Value.StructuredContent["mode"], Is.EqualTo("Full"));
            Assert.That((JArray)explicitValues.Value.StructuredContent["labels"], Has.Count.EqualTo(2));
            var asyncResult = await dispatcher.DispatchAsync("sample.async", new JObject(), "async", CancellationToken.None);
            Assert.That(asyncResult.Error, Is.Null);
            Assert.That((bool)asyncResult.Value.StructuredContent["ok"], Is.True);
            registry.TryGet("sample.run", out var descriptor);
            Assert.That(descriptor.InputSchema["properties"]["cancellation"], Is.Null);
            Assert.That(descriptor.InputSchema["properties"]["context"], Is.Null);
            Assert.That(descriptor.InputSchema["required"].Values<string>(), Is.EqualTo(new[] { "name" }));
        }

        [TestCase("{}")]
        [TestCase("{'name':'Oak','count':11}")]
        [TestCase("{'name':'Oak','mode':'Unknown'}")]
        [TestCase("{'name':'Oak','count':'3'}")]
        [TestCase("{'name':'Oak','labels':['a','b','c','d']}")]
        [TestCase("{'name':'Oak','unexpected':true}")]
        [TestCase("{'name':'Oak','offset':2147483648}")]
        public async Task InvalidArgumentsNeverReachTheMethod(string json)
        {
            var provider = new SampleProvider();
            var result = await Dispatcher(Registry(provider)).DispatchAsync("sample.run", JObject.Parse(json), "invalid", CancellationToken.None);
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(provider.Calls, Is.Zero);
        }

        [Test]
        public async Task DispatcherStillEnforcesAccessModeAndOriginalException()
        {
            var provider = new SampleProvider(); var registry = Registry(provider); var authorizer = new RejectingAuthorizer();
            var denied = await Dispatcher(registry, authorizer: authorizer).DispatchAsync("sample.run", JObject.Parse("{'name':'Oak'}"), "denied", CancellationToken.None);
            Assert.That(denied.Error.Message, Is.EqualTo("Denied"));
            Assert.That(authorizer.Seen, Is.EqualTo(ToolTrustCategory.ProjectWrite));
            var wrongMode = await Dispatcher(registry, playing: true).DispatchAsync("sample.run", JObject.Parse("{'name':'Oak'}"), "mode", CancellationToken.None);
            Assert.That(wrongMode.Error.Code, Is.EqualTo(McpErrorCodes.ToolUnavailable));
            Assert.That(provider.Calls, Is.Zero);
            var failure = await Dispatcher(registry).DispatchAsync("sample.error", new JObject(), "failure", CancellationToken.None);
            Assert.That(failure.Error.Code, Is.EqualTo(McpErrorCodes.ValidationFailed));
            Assert.That(failure.Error.Message, Is.EqualTo("Expected failure"));
        }

        [Test]
        public void UnsupportedSignatureRejectsWholeProviderWithoutDroppingHealthyTools()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[] { new SampleProvider(), new InvalidProvider() });
            Assert.That(registry.Errors.Single(), Does.Contain("Unsupported MCP parameter type"));
            Assert.That(registry.TryGet("invalid.good", out _), Is.False);
            Assert.That(registry.TryGet("sample.run", out _), Is.True);
        }
    }
}
