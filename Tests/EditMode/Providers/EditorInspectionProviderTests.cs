using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class EditorInspectionProviderTests
    {
        private sealed class Sink : IToolRegistration
        {
            internal readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
        private static ToolResult Call(ToolDescriptor tool, JObject args) => tool.Handler(new ToolContext(args,
            new NoopProgressReporter(), new FakeMainThreadPump(), new FakeFrameWaiter(), null,
            new FakeLogSink(), "inspection-test"), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        [Test]
        public void Selection_ValidatesWholeBatchAndPagesReadback()
        {
            var sink = new Sink(); new EditorInspectionProvider().RegisterTools(sink);
            var set = sink.Tools.Single(t => t.Name == "editor.selection.set");
            var read = sink.Tools.Single(t => t.Name == "editor.selection.read");
            var previous = Selection.objects;
            var first = new Mesh { name = "First" }; var second = new Mesh { name = "Second" };
            try
            {
                Call(set, new JObject { ["entityIds"] = new JArray(UnitySerializer.ToEntityIdString(first), UnitySerializer.ToEntityIdString(second)) });
                Assert.That(Selection.objects, Is.EquivalentTo(new Object[] { first, second }));
                Assert.Throws<McpToolException>(() => Call(set, new JObject { ["entityIds"] = new JArray(UnitySerializer.ToEntityIdString(first), "invalid") }));
                Assert.That(Selection.objects, Has.Length.EqualTo(2));
                var result = Call(read, new JObject { ["limit"] = 1 }).StructuredContent;
                Assert.That((int)result["total"], Is.EqualTo(2));
                Assert.That((JArray)result["items"], Has.Count.EqualTo(1));
                Assert.That((bool)result["truncated"], Is.True);
                Call(set, new JObject { ["entityIds"] = new JArray() });
                Assert.That(Selection.objects, Is.Empty);
            }
            finally { Selection.objects = previous; Object.DestroyImmediate(first); Object.DestroyImmediate(second); }
        }

        [Test]
        public void InspectionReadsAreBoundedAndDoNotRequireWriterOwnership()
        {
            var sink = new Sink(); new EditorInspectionProvider().RegisterTools(sink);
            foreach (var tool in sink.Tools.Where(t => t.TrustCategory == ToolTrustCategory.Read))
            {
                Assert.That(tool.RequiresWriterLease, Is.False);
                Assert.That(tool.ReloadSafe, Is.True);
                var result = Call(tool, new JObject { ["limit"] = 1 }).StructuredContent;
                Assert.That(((JArray)result["items"]).Count, Is.LessThanOrEqualTo(1));
            }
        }
    }
}
