using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class EditorStatusProviderTests
    {
        [Test]
        public void RegisterTools_DeclaresStatusAndPlaybackTools()
        {
            var provider = new EditorStatusProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            var names = sink.Tools.Select(t => t.Name).ToList();
            Assert.That(names, Contains.Item("editor.status"));
            Assert.That(names, Contains.Item("editor.tools.list"));
            Assert.That(names, Contains.Item("editor.metrics"));
            Assert.That(names, Contains.Item("editor.play"));
            Assert.That(names, Contains.Item("editor.stop"));
            Assert.That(names, Contains.Item("editor.request_stop_play_mode"));
            Assert.That(names, Contains.Item("editor.pause"));
            Assert.That(names, Contains.Item("editor.resume"));
            Assert.That(names, Contains.Item("editor.refresh"));
            Assert.That(names, Does.Not.Contain("editor.compile"));
            Assert.That(names, Contains.Item("editor.compile.errors"));
            Assert.That(names, Contains.Item("editor.ensure_compiled"));
            Assert.That(names, Contains.Item("editor.wait_ready"));
        }

        [Test]
        public void RegisterTools_CompileWaitersAreAvailableWhileCompiling()
        {
            var provider = new EditorStatusProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);

            var ensureCompiled = sink.Tools.First(t => t.Name == "editor.ensure_compiled");
            var waitReady = sink.Tools.First(t => t.Name == "editor.wait_ready");

            Assert.That((ensureCompiled.Availability & ToolAvailability.Compiling) != 0, Is.True);
            Assert.That((waitReady.Availability & ToolAvailability.Compiling) != 0, Is.True);
        }

        [Test]
        public void ToolsList_DefaultsToCompactFilteredRecords()
        {
            var descriptors = new[]
            {
                new ToolDescriptor { Name = "scene.read", Availability = ToolAvailability.Either, ProviderTypeName = "SceneProvider" },
                new ToolDescriptor { Name = "tests.run", Availability = ToolAvailability.EditMode, ProviderTypeName = "TestRunnerProvider" },
            };

            var compact = EditorStatusProvider.CreateToolsListResponse(descriptors,
                new JObject { ["search"] = "tests." }, false, false);
            Assert.That((int)compact["total"], Is.EqualTo(2));
            Assert.That((int)compact["filtered"], Is.EqualTo(1));
            Assert.That((int)compact["returned"], Is.EqualTo(1));
            Assert.That((string)compact["tools"][0]["name"], Is.EqualTo("tests.run"));
            Assert.That(compact["tools"][0]["provider"], Is.Null);

            var detailed = EditorStatusProvider.CreateToolsListResponse(descriptors,
                new JObject { ["names"] = new JArray("tests.run"), ["includeMetadata"] = true }, false, false);
            Assert.That((string)detailed["tools"][0]["provider"], Is.EqualTo("TestRunnerProvider"));

            var paged = EditorStatusProvider.CreateToolsListResponse(descriptors,
                new JObject { ["limit"] = 1 }, false, false);
            Assert.That((int)paged["returned"], Is.EqualTo(1));
            Assert.That((bool)paged["truncated"], Is.True);
            Assert.That((int)paged["nextOffset"], Is.EqualTo(1));
        }

        [Test]
        public void RegisterTools_ProtectsUserOwnedPlayMode()
        {
            var provider = new EditorStatusProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);

            var stop = sink.Tools.First(t => t.Name == "editor.stop");
            var requestStop = sink.Tools.First(t => t.Name == "editor.request_stop_play_mode");
            var play = sink.Tools.First(t => t.Name == "editor.play");
            var ensureCompiled = sink.Tools.First(t => t.Name == "editor.ensure_compiled");

            Assert.That(play.InputSchema["required"].Values<string>(), Does.Not.Contain("userConsent"));
            Assert.That(play.InputSchema["required"].Values<string>(), Does.Contain("reason"));
            Assert.That(play.Description, Does.Contain("trust policy"));
            Assert.That(stop.InputSchema["required"].Values<string>(), Contains.Item("playSessionToken"));
            Assert.That((bool)stop.Annotations["destructiveHint"], Is.True);
            Assert.That(requestStop.InputSchema["required"].Values<string>(), Contains.Item("reason"));
            Assert.That(requestStop.Description, Does.Contain("trust policy"));
            Assert.That((bool)requestStop.Annotations["destructiveHint"], Is.True);
            Assert.That(ensureCompiled.InputSchema["properties"]["playSessionToken"], Is.Not.Null);
            Assert.That(ensureCompiled.InputSchema["properties"]["forceStop"], Is.Null);
        }

        [Test]
        public void Play_RejectsWithoutReason()
        {
            var provider = new EditorStatusProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            var play = sink.Tools.First(t => t.Name == "editor.play");
            var ctx = new ToolContext(
                new JObject
                {
                    ["reason"] = "",
                },
                new NoopProgressReporter(),
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                null,
                new FakeLogSink(),
                "req");

            var error = Assert.ThrowsAsync<McpToolException>(async () => await play.Handler(ctx, CancellationToken.None));
            Assert.That(error.Message, Does.Contain("reason"));
        }

        [Test]
        public async System.Threading.Tasks.Task CompileErrors_ReturnsStructuredEnvelope()
        {
            var provider = new EditorStatusProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            var tool = sink.Tools.First(t => t.Name == "editor.compile.errors");
            var ctx = new ToolContext(
                new JObject(),
                new NoopProgressReporter(),
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                null,
                new FakeLogSink(),
                "req");
            var result = await tool.Handler(ctx, CancellationToken.None);
            Assert.That(result.StructuredContent["messages"], Is.Not.Null);
            Assert.That(result.StructuredContent["errorCount"], Is.Not.Null);
            Assert.That(result.StructuredContent["warningCount"], Is.Not.Null);
            Assert.That(result.StructuredContent["passCounter"], Is.Not.Null);
            Assert.That((string)result.StructuredContent["detail"], Is.EqualTo("errors"));
        }

        [Test]
        public void CompileProjection_DefaultsToErrorsAndPagesAllMessagesOnRequest()
        {
            var payload = new JObject
            {
                ["errorCount"] = 1,
                ["warningCount"] = 2,
                ["messages"] = new JArray
                {
                    new JObject { ["type"] = "Warning", ["message"] = "warning one" },
                    new JObject { ["type"] = "Error", ["message"] = "error" },
                    new JObject { ["type"] = "Warning", ["message"] = "warning two" },
                },
            };

            var compact = EditorStatusProvider.ProjectCompileMessages((JObject)payload.DeepClone(), new JObject());
            Assert.That((int)compact["returned"], Is.EqualTo(1));
            Assert.That((string)compact["messages"][0]["type"], Is.EqualTo("Error"));

            var all = EditorStatusProvider.ProjectCompileMessages((JObject)payload.DeepClone(), new JObject
            {
                ["detail"] = "all",
                ["offset"] = 1,
                ["limit"] = 1,
            });
            Assert.That((int)all["returned"], Is.EqualTo(1));
            Assert.That((bool)all["truncated"], Is.True);
            Assert.That((int)all["nextOffset"], Is.EqualTo(2));
        }

        [Test]
        public async System.Threading.Tasks.Task Refresh_CallsAssetDatabaseRefresh()
        {
            var provider = new EditorStatusProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            var refresh = sink.Tools.First(t => t.Name == "editor.refresh");
            var ctx = new ToolContext(
                new JObject(),
                new NoopProgressReporter(),
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                null,
                new FakeLogSink(),
                "req");
            var result = await refresh.Handler(ctx, CancellationToken.None);
            Assert.That((string)result.StructuredContent["action"], Is.EqualTo("refresh"));
            Assert.That(result.StructuredContent["isCompiling"], Is.Not.Null);
        }

        [Test]
        public async System.Threading.Tasks.Task Status_ReturnsEditorState()
        {
            var provider = new EditorStatusProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            var status = sink.Tools.First(t => t.Name == "editor.status");
            var ctx = new ToolContext(
                new JObject(),
                new NoopProgressReporter(),
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                null,
                new FakeLogSink(),
                "req");
            var result = await status.Handler(ctx, CancellationToken.None);
            Assert.That(result.StructuredContent["unityVersion"], Is.Not.Null);
            Assert.That(result.StructuredContent["isPlaying"], Is.Not.Null);
        }

        [Test]
        public async System.Threading.Tasks.Task RequestStopPlayMode_WhenAlreadyStopped_ReturnsImmediately()
        {
            var provider = new EditorStatusProvider();
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            var requestStop = sink.Tools.First(t => t.Name == "editor.request_stop_play_mode");
            var ctx = new ToolContext(
                new JObject { ["reason"] = "run the requested editor test" },
                new NoopProgressReporter(),
                new FakeMainThreadPump(),
                new FakeFrameWaiter(),
                null,
                new FakeLogSink(),
                "req");

            var result = await requestStop.Handler(ctx, CancellationToken.None);

            Assert.That((string)result.StructuredContent["action"], Is.EqualTo("already_stopped"));
            Assert.That((bool)result.StructuredContent["approved"], Is.True);
        }

        [Test]
        public void CanUseCachedResult_ReplayedForceWithMatchingHash_ReturnsTrue()
        {
            Assert.That(CanUseCachedResult(
                force: true,
                replayed: true,
                isCompiling: false,
                isUpdating: false,
                currentHash: "abc",
                lastHash: "abc",
                passCounter: 12), Is.True);
        }

        [Test]
        public void CanUseCachedResult_InitialForceWithMatchingHash_ReturnsFalse()
        {
            Assert.That(CanUseCachedResult(
                force: true,
                replayed: false,
                isCompiling: false,
                isUpdating: false,
                currentHash: "abc",
                lastHash: "abc",
                passCounter: 12), Is.False);
        }

        [Test]
        public void CanUseCachedResult_NonForceWithMatchingHash_ReturnsTrue()
        {
            Assert.That(CanUseCachedResult(
                force: false,
                replayed: true,
                isCompiling: false,
                isUpdating: false,
                currentHash: "abc",
                lastHash: "abc",
                passCounter: 12), Is.True);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void CanUseCachedResult_WhenEditorIsBusy_ReturnsFalse(bool isCompiling, bool isUpdating)
        {
            Assert.That(CanUseCachedResult(
                force: false,
                replayed: false,
                isCompiling,
                isUpdating,
                currentHash: "abc",
                lastHash: "abc",
                passCounter: 12), Is.False);
        }

        private static bool CanUseCachedResult(
            bool force,
            bool replayed,
            bool isCompiling,
            bool isUpdating,
            string currentHash,
            string lastHash,
            int passCounter)
        {
            var method = typeof(EditorStatusProvider).GetMethod("CanUseCachedResult", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new object[]
            {
                force,
                replayed,
                isCompiling,
                isUpdating,
                currentHash,
                lastHash,
                passCounter,
            });
        }

        private sealed class CollectingSink : IToolRegistration
        {
            public List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
