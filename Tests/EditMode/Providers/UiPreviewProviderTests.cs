using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class UiPreviewProviderTests
    {
        [Test]
        public void RegisterTools_DeclaresPreviewTool()
        {
            var tool = CollectFor(new UiPreviewProvider()).Tools.Single();
            Assert.That(tool.Name, Is.EqualTo("ui.preview_screenshot"));
            Assert.That(tool.Availability, Is.EqualTo(ToolAvailability.EditMode));
            Assert.That(tool.RequiresMainThread, Is.True);
        }

        [Test]
        public void Preview_RejectsMissingUxml()
        {
            var tool = CollectFor(new UiPreviewProvider()).Tools.Single();
            var args = new JObject { ["uxmlPath"] = "Assets/__missing__.uxml" };
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(Ctx(args), CancellationToken.None));
            Assert.That(ex.Code, Is.EqualTo(McpErrorCodes.NotFound));
        }

        [Test]
        public void Preview_RejectsAbsolutePath()
        {
            var tool = CollectFor(new UiPreviewProvider()).Tools.Single();
            var args = new JObject { ["uxmlPath"] = "C:/Temp/Preview.uxml" };
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(Ctx(args), CancellationToken.None));
            Assert.That(ex.Code, Is.EqualTo(McpErrorCodes.ValidationFailed));
        }

        private static CollectingSink CollectFor(IToolProvider provider)
        {
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            return sink;
        }

        private static ToolContext Ctx(JObject args) => new(
            args,
            new NoopProgressReporter(),
            new FakeMainThreadPump(),
            new FakeFrameWaiter(),
            null,
            new FakeLogSink(),
            "req");

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
