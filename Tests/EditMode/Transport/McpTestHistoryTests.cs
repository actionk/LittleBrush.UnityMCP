using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Diagnostics;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Transport
{
    public class McpTestHistoryTests
    {
        private string _directory;
        private string PathName => Path.Combine(_directory, "test-history.json");
        [SetUp] public void Setup() => _directory = Path.Combine(Path.GetTempPath(), "mcp-history-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        private static JObject Run(string id, double seconds, string status = "Passed", string mode = "EditMode") => new()
        {
            ["runId"] = id, ["mode"] = mode, ["status"] = "completed", ["startedAt"] = 1000, ["finishedAt"] = 5000,
            ["durationSec"] = seconds, ["tests"] = new JArray(new JObject { ["fullName"] = "Example.Case(1)", ["status"] = status, ["durationSec"] = seconds, ["output"] = "Do not archive" }),
        };

        [Test]
        public void ReloadDeduplicatesAndSeparatesModesFailuresAndVersions()
        {
            McpTestHistory.Record(PathName, Run("one", 2), "6000");
            McpTestHistory.Record(PathName, Run("two", 4), "6000");
            McpTestHistory.Record(PathName, Run("two", 4), "6000");
            McpTestHistory.Record(PathName, Run("fail", 100, "Failed"), "6000");
            McpTestHistory.Record(PathName, Run("play", 20, mode: "PlayMode"), "6000");
            McpTestHistory.Record(PathName, Run("upgrade", 50), "6001");
            var history = McpTestHistory.Read(PathName);
            Assert.That(((JArray)history["runs"]).Count, Is.EqualTo(5));
            var rows = McpTestHistory.Summarize(history);
            Assert.That(rows.Count, Is.EqualTo(3));
            var row = rows.First(r => (string)r["mode"] == "EditMode" && (string)r["unityVersion"] == "6000");
            Assert.That((double)row["medianSec"], Is.EqualTo(3));
            Assert.That((double)row["lastSec"], Is.EqualTo(100));
            Assert.That((int)row["successfulSamples"], Is.EqualTo(2));
            Assert.That(history["runs"][0]["tests"][0]["output"], Is.Null);
            Assert.That((double)history["runs"][0]["wallDurationSec"], Is.EqualTo(4));
            Assert.That(File.ReadAllText(Path.Combine(_directory, ".gitignore")).Trim(), Is.EqualTo("*"));
        }

        [Test]
        public void CorruptHistoryIsPreservedAndRetentionIsBounded()
        {
            for (int i = 0; i < McpTestHistory.MaxRuns + 2; i++) McpTestHistory.Record(PathName, Run(i.ToString(), i), "6000");
            var runs = (JArray)McpTestHistory.Read(PathName)["runs"];
            Assert.That(runs.Count, Is.EqualTo(McpTestHistory.MaxRuns));
            Assert.That((string)runs[0]["runId"], Is.EqualTo("2"));
            File.WriteAllText(PathName, "broken");
            Assert.Throws<Newtonsoft.Json.JsonReaderException>(() => McpTestHistory.Record(PathName, Run("new", 1), "6000"));
            Assert.That(File.ReadAllText(PathName), Is.EqualTo("broken"));
        }

        [Test]
        public void RepresentativeAndLargeHistoryReportsCostAndRejectsOversizedRun()
        {
            foreach (int count in new[] { 100, 10000 })
            {
                var run = Run("size-" + count, 1);
                run["tests"] = new JArray(Enumerable.Range(0, count).Select(i => new JObject
                {
                    ["fullName"] = "Assembly.Fixture.Case(" + i + ")", ["status"] = "Passed", ["durationSec"] = i / 1000.0,
                }));
                var watch = Stopwatch.StartNew();
                McpTestHistory.Record(PathName, run, "6000");
                var writeMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                var rows = McpTestHistory.Summarize(McpTestHistory.Read(PathName));
                TestContext.WriteLine($"Test history cases={count}: write={writeMs:F1} ms, read/summarize={watch.Elapsed.TotalMilliseconds:F1} ms, bytes={new FileInfo(PathName).Length}");
                Assert.That(rows.Count, Is.EqualTo(count));
                var before = File.ReadAllText(PathName);
                if (count == 10000)
                {
                    ((JArray)run["tests"]).Add(((JArray)run["tests"])[0].DeepClone());
                    Assert.Throws<IOException>(() => McpTestHistory.Record(PathName, run, "6000"));
                    Assert.That(File.ReadAllText(PathName), Is.EqualTo(before));
                }
            }
        }
    }
}
