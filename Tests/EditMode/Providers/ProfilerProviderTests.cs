using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class ProfilerProviderTests
    {
        private sealed class Sink : IToolRegistration
        {
            internal readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
        private static ToolResult Call(Sink sink, string name, JObject args = null) => sink.Tools.Single(t => t.Name == name).Handler(
            new ToolContext(args ?? new JObject(), new NoopProgressReporter(), new FakeMainThreadPump(), new FakeFrameWaiter(), null, new FakeLogSink(), "profiler-test"),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        [Test]
        public void MarkerTimes_SeparateNestedExecutionCadenceAndUncoveredGaps()
        {
            var totals = new ProfilerMarkerTotals("worker", 42, "marker");
            totals.Add(0, 10, 8);
            totals.Add(2, 2, 2); // Nested call must not shorten the covered interval.
            totals.Add(15, 3, 3);
            var row = totals.Row();
            Assert.That((int)row["calls"], Is.EqualTo(3));
            Assert.That((double)row["selfMs"], Is.EqualTo(13));
            Assert.That((double)row["inclusiveMs"], Is.EqualTo(15));
            Assert.That((double)row["meanCadenceMs"], Is.EqualTo(7.5));
            Assert.That((double)row["gapMs"], Is.EqualTo(5));
            Assert.That((double)row["maxGapMs"], Is.EqualTo(5));
            Assert.That(new ProfilerMarkerTotals("main", 1, "once").Row()["meanCadenceMs"].Type, Is.EqualTo(JTokenType.Null));
        }

        [UnityTest]
        public IEnumerator Capture_AutoStopsRestoresSettingsAndReportsSavedMarkers()
        {
            if (Profiler.enabled || ProfilerDriver.enabled || ProfilerDriver.deepProfiling)
                Assert.Ignore("An existing profiler session must not be changed by this test.");
            var sink = new Sink(); new ProfilerProvider().RegisterTools(sink);
            bool editor = ProfilerDriver.profileEditor, binary = Profiler.enableBinaryLog, callstacks = Profiler.enableAllocationCallstacks;
            string log = Profiler.logFile;
            int memory = Profiler.maxUsedMemory;
            var areas = System.Enum.GetValues(typeof(ProfilerArea)).Cast<ProfilerArea>().Select(Profiler.GetAreaEnabled).ToArray();
            try
            {
                Assert.Throws<McpToolException>(() => Call(sink, "profiler.start", new JObject { ["durationSeconds"] = 0 }));
                var start = Call(sink, "profiler.start", new JObject { ["durationSeconds"] = 1 }).StructuredContent;
                Assert.Throws<McpToolException>(() => Call(sink, "profiler.start"));
                Assert.Throws<McpToolException>(() => Call(sink, "profiler.report"));
                Assert.That(Profiler.GetAreaEnabled(ProfilerArea.Memory), Is.False);
                Assert.That(Profiler.enableAllocationCallstacks, Is.False);
                double deadline = EditorApplication.timeSinceStartup + 3;
                while (Profiler.enabled && EditorApplication.timeSinceStartup < deadline)
                {
                    Profiler.BeginSample("McpProfiler.TestMarker");
                    System.Threading.Thread.SpinWait(10000);
                    Profiler.EndSample();
                    yield return null;
                }
                Assert.That(Profiler.enabled, Is.False, "Automatic deadline must stop recording.");
                var stopped = Call(sink, "profiler.stop").StructuredContent;
                Assert.That((string)stopped["stopReason"], Is.EqualTo("duration"));
                Assert.That(File.Exists((string)start["rawPath"]), Is.True);
                Assert.That(ProfilerDriver.profileEditor, Is.EqualTo(editor));
                Assert.That(Profiler.enableBinaryLog, Is.EqualTo(binary));
                Assert.That(Profiler.logFile, Is.EqualTo(log));
                Assert.That(Profiler.enableAllocationCallstacks, Is.EqualTo(callstacks));
                Assert.That(Profiler.maxUsedMemory, Is.EqualTo(memory));
                Assert.That(System.Enum.GetValues(typeof(ProfilerArea)).Cast<ProfilerArea>().Select(Profiler.GetAreaEnabled), Is.EqualTo(areas));
                var report = Call(sink, "profiler.report", new JObject { ["captureId"] = start["captureId"], ["marker"] = "McpProfiler.TestMarker", ["topN"] = 1 }).StructuredContent;
                Assert.That((JArray)report["rows"], Has.Count.EqualTo(1));
                Assert.That((int)report["rows"][0]["calls"], Is.GreaterThan(1));
                Assert.That((double)report["rows"][0]["meanCadenceMs"], Is.GreaterThan(0));
                TestContext.WriteLine(report.ToString());
                var empty = Call(sink, "profiler.report", new JObject { ["captureId"] = start["captureId"], ["thread"] = "missing-thread", ["fromMs"] = 0, ["toMs"] = 1 }).StructuredContent;
                Assert.That((JArray)empty["rows"], Is.Empty);
                Assert.Throws<McpToolException>(() => Call(sink, "profiler.report", new JObject { ["captureId"] = "../outside" }));
                Assert.Throws<McpToolException>(() => Call(sink, "profiler.report", new JObject { ["fromMs"] = 20, ["toMs"] = 10 }));
                Call(sink, "profiler.start", new JObject { ["durationSeconds"] = 60, ["includeMemory"] = true });
                Assert.That(Profiler.GetAreaEnabled(ProfilerArea.Memory), Is.True);
                yield return null;
                Assert.That((string)Call(sink, "profiler.stop").StructuredContent["stopReason"], Is.EqualTo("requested"));
                Assert.That(Profiler.enabled, Is.False);
                Assert.That(System.Enum.GetValues(typeof(ProfilerArea)).Cast<ProfilerArea>().Select(Profiler.GetAreaEnabled), Is.EqualTo(areas));
            }
            finally { Call(sink, "profiler.stop"); }
        }
    }
}
