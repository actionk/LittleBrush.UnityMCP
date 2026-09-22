using LittleBrushGames.Mcp.Editor.Host;
using LittleBrushGames.Mcp.Editor;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class McpBatchWorkerTests
    {
        private sealed class WindowProvider : AttributedToolProvider
        {
            public override string Namespace => "batch_test";
            [McpTool("batch_test.window", "Inspect a real editor window.", ToolTrustCategory.Read,
                RequiresGraphics = true, RequiresInteractiveEditor = true)]
            private static JObject Capture() => new();
        }

        [Test]
        public void AttributeDeclaredWindowIsUnavailableInBatch()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new[] { new WindowProvider() });
            Assert.That(registry.TryGet("batch_test.window", out var tool), Is.True);
            Assert.That(McpBatchWorker.EnvironmentUnavailableReason(tool, true, true), Is.EqualTo("interactive_editor_required"));
            Assert.That(McpBatchWorker.EnvironmentUnavailableReason(tool, false, false), Is.EqualTo("graphics_device_required"));
        }

        [TestCase(false, true, null)]
        [TestCase(true, true, "interactive_editor_required")]
        [TestCase(false, false, "graphics_device_required")]
        public void WindowCaptureReportsRequiredEnvironment(bool batch, bool graphics, string reason)
        {
            var tool = new ToolDescriptor { RequiresInteractiveEditor = true, RequiresGraphics = true };
            Assert.That(McpBatchWorker.EnvironmentUnavailableReason(tool, batch, graphics), Is.EqualTo(reason));
        }

        [Test]
        public void RenderTexturePreviewAllowsBatchButRequiresGraphics()
        {
            var tool = new ToolDescriptor { RequiresGraphics = true };
            Assert.That(McpBatchWorker.EnvironmentUnavailableReason(tool, true, true), Is.Null);
            Assert.That(McpBatchWorker.EnvironmentUnavailableReason(tool, true, false), Is.EqualTo("graphics_device_required"));
            Assert.That(McpBatchWorker.EnvironmentUnavailableReason(new ToolDescriptor(), true, false), Is.Null);
        }
    }
}
