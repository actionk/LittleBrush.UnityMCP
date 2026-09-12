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

        private static JObject CompleteReport(Sink sink, JObject args)
        {
            JObject result = null;
            long previous = 0;
            for (int attempt = 0; attempt < 500; attempt++)
            {
                result = Call(sink, "profiler.report", args).StructuredContent;
                if (!(bool)result["partial"]) return result;
                Assert.That((long)result["samplesScanned"], Is.GreaterThanOrEqualTo(previous));
                previous = (long)result["samplesScanned"];
                Assert.That((string)result["cursor"], Is.Not.Empty);
                args["cursor"] = result["cursor"];
            }
            Assert.Fail("Resumable analysis did not complete.");
            return result;
        }

        [Test]
        public void LoadBudgetAcceptsRecordingOvershootButRemainsBounded()
        {
            Assert.DoesNotThrow(() => ProfilerProvider.ValidateLoadSize(216881889));
            Assert.DoesNotThrow(() => ProfilerProvider.ValidateLoadSize(ProfilerProvider.MaxLoadBytes));
            Assert.Throws<McpToolException>(() => ProfilerProvider.ValidateLoadSize(ProfilerProvider.MaxLoadBytes + 1));
        }

        [Test]
        public void ExitOwnershipIsValidatedBeforeStartingCapture()
        {
            bool started = false;
            var context = new ToolContext(new JObject(), new NoopProgressReporter(), new FakeMainThreadPump(),
                new FakeFrameWaiter(), null, new FakeLogSink(), "profiler-ownership-test");
            Assert.Throws<McpToolException>(() => McpPlayModeOwnership.Stop(context, "invalid-token", "editor.stop", () => started = true));
            Assert.That(started, Is.False, "A rejected stop must not start or replace a recording.");
        }

        [Test]
        public void CachedSearchPagesWithoutChangingAggregatesAndRetainsSlowestLocation()
        {
            var ivy = new ProfilerMarkerTotals("Main Thread", 1, "Ivy.Tick");
            ivy.Add(0, 23, 20, 3, 0, 7); ivy.Add(30, 2, 1, 4, 0, 9);
            var other = new ProfilerMarkerTotals("Main Thread", 1, "ExitPlayMode"); other.Add(40, 1, 1);
            var report = new JObject { ["rows"] = new JArray(ivy.Row(), other.Row()) };
            var selected = ProfilerProvider.SelectReport(report, new JObject { ["marker"] = "IVY", ["topN"] = 1 }, true);
            Assert.That((int)selected["matchedRows"], Is.EqualTo(1));
            Assert.That((int)selected["rows"][0]["maxSample"], Is.EqualTo(7));
            Assert.That((int)selected["rows"][0]["maxFrame"], Is.EqualTo(3));
            Assert.That((double)selected["rows"][0]["inclusiveMs"], Is.EqualTo(25));
            Assert.That((JArray)report["rows"], Has.Count.EqualTo(2));
            var page = ProfilerProvider.SelectReport(report, new JObject { ["topN"] = 1, ["offset"] = 1 }, true);
            Assert.That((string)page["rows"][0]["marker"], Is.EqualTo("ExitPlayMode"));
        }

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
                var query = new JObject { ["captureId"] = start["captureId"], ["thread"] = "Main Thread", ["marker"] = "McpProfiler.TestMarker", ["topN"] = 1, ["budgetMs"] = 1 };
                var report = CompleteReport(sink, query);
                Assert.That((JArray)report["rows"], Has.Count.EqualTo(1));
                Assert.That((int)report["rows"][0]["calls"], Is.GreaterThan(1));
                Assert.That((double)report["rows"][0]["meanCadenceMs"], Is.GreaterThan(0));
                TestContext.WriteLine(report.ToString());
                var cached = Call(sink, "profiler.report", query).StructuredContent;
                Assert.That((bool)cached["cacheHit"], Is.True);
                Assert.That(JToken.DeepEquals(cached["rows"], report["rows"]), Is.True);
                var hit = report["rows"][0];
                var detail = Call(sink, "profiler.sample", new JObject { ["captureId"] = start["captureId"], ["frame"] = hit["maxFrame"], ["threadIndex"] = hit["maxThreadIndex"], ["sample"] = hit["maxSample"] }).StructuredContent;
                Assert.That((string)detail["sample"]["marker"], Is.EqualTo("McpProfiler.TestMarker"));
                var frames = Call(sink, "profiler.frames", new JObject { ["captureId"] = start["captureId"], ["topN"] = 1 }).StructuredContent;
                Assert.That((JArray)frames["rows"], Has.Count.EqualTo(1));
                var empty = CompleteReport(sink, new JObject { ["captureId"] = start["captureId"], ["thread"] = "missing-thread", ["fromMs"] = 0, ["toMs"] = 1 });
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
