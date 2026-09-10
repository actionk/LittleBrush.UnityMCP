using LittleBrushGames.Mcp.Editor.Host;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class McpBatchWorkerTests
    {
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
