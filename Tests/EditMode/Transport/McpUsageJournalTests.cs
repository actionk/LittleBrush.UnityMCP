using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Transport
{
    public class McpUsageJournalTests
    {
        private string _directory;
        private static JObject Ok => new() { ["result"] = new JObject() };

        [Test]
        public void MetadataOnlyHistoryIsBoundedWhileStatisticsKeepAllCalls()
        {
            using (var journal = new McpUsageJournal(_directory, Assert.Fail, false, executionRetention: 2, retainCode: false))
                for (var i = 0; i < 5; i++)
                    journal.Record("editor.execute_code", new JObject { ["code"] = "private snippet", ["reason"] = "sample " + i }, Ok, 1);
            var history = File.ReadAllText(Path.Combine(_directory, "execute-code.json"));
            Assert.That(history, Does.Not.Contain("private snippet"));
            Assert.That((JArray)JObject.Parse(history)["entries"], Has.Count.EqualTo(2));
            Assert.That((int)JObject.Parse(File.ReadAllText(Path.Combine(_directory, "statistics.json")))["tools"]["editor.execute_code"]["calls"], Is.EqualTo(5));
            using (var journal = new McpUsageJournal(_directory, Assert.Fail, false, executionRetention: 0))
                journal.Record("editor.execute_code", new JObject { ["code"] = "do not retain" }, Ok, 1);
            Assert.That(File.ReadAllText(Path.Combine(_directory, "execute-code.json")), Is.EqualTo(history));
            Assert.That((int)JObject.Parse(File.ReadAllText(Path.Combine(_directory, "statistics.json")))["tools"]["editor.execute_code"]["calls"], Is.EqualTo(6));
        }

        [Test]
        public void SmallerRetentionTrimsExistingHistoryOnRestart()
        {
            using (var journal = new McpUsageJournal(_directory, Assert.Fail, false))
                for (var i = 0; i < 5; i++) journal.Record("editor.execute_code", new JObject { ["reason"] = i.ToString() }, Ok, 1);
            using (var journal = new McpUsageJournal(_directory, Assert.Fail, false, executionRetention: 1)) { }
            var entries = (JArray)JObject.Parse(File.ReadAllText(Path.Combine(_directory, "execute-code.json")))["entries"];
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That((string)entries[0]["reason"], Is.EqualTo("4"));
        }

        [SetUp] public void Setup() => _directory = Path.Combine(Path.GetTempPath(), "mcp-usage-" + Guid.NewGuid());
        [TearDown] public void Cleanup()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            else if (File.Exists(_directory)) File.Delete(_directory);
        }

        [Test]
        public void ReloadPreservesTotalsAndRecordsFailuresWithoutOrdinaryPayloads()
        {
            using (var journal = new McpUsageJournal(_directory, Assert.Fail, false))
            {
                journal.Record("scene.write", new JObject { ["password"] = "not-persisted" }, Ok, 3);
                journal.Record("scene.write", null, JObject.Parse("{ 'error': { 'code': -32001 } }"), 7);
                journal.Record("scene.write", null, JObject.Parse("{ 'result': { 'structuredContent': { 'success': false } } }"), 2);
                journal.Record("scene.write", null, null, 1);
            }
            using (var journal = new McpUsageJournal(_directory, Assert.Fail, false))
                journal.Record("scene.write", null, Ok, 4);
            var text = File.ReadAllText(Path.Combine(_directory, "statistics.json"));
            var tool = JObject.Parse(text)["tools"]["scene.write"];
            Assert.That((int)tool["calls"], Is.EqualTo(5));
            Assert.That((int)tool["ok"], Is.EqualTo(2));
            Assert.That((int)tool["failed"], Is.EqualTo(2));
            Assert.That((int)tool["interrupted"], Is.EqualTo(1));
            Assert.That((int)tool["totalDurationMs"], Is.EqualTo(17));
            Assert.That(text, Does.Not.Contain("not-persisted"));
        }

        [Test]
        public void ExecutionHistoryRetainsNewestBoundedCodeAcrossReload()
        {
            using (var journal = new McpUsageJournal(_directory, Assert.Fail, false))
            {
                for (var i = 0; i < 70; i++)
                    journal.Record("editor.execute_code", new JObject { ["reason"] = "Missing capability " + i, ["code"] = new string('x', 66000) }, Ok, 2);
            }
            using (var journal = new McpUsageJournal(_directory, Assert.Fail, false))
                journal.Record("editor.execute_code", new JObject { ["code"] = "output.Add(1);", ["reason"] = "Last" }, Ok, 1);
            var entries = (JArray)JObject.Parse(File.ReadAllText(Path.Combine(_directory, "execute-code.json")))["entries"];
            Assert.That(entries.Count, Is.EqualTo(64));
            Assert.That((string)entries[0]["reason"], Is.EqualTo("Missing capability 7"));
            Assert.That(((string)entries[0]["code"]).Length, Is.EqualTo(65536));
            Assert.That((bool)entries[0]["codeTruncated"], Is.True);
            Assert.That((string)entries[63]["reason"], Is.EqualTo("Last"));
        }

        [Test]
        public void FailedFlushRetriesWithoutLosingCounts()
        {
            File.WriteAllText(_directory, "blocks directory creation");
            var warnings = 0;
            using var journal = new McpUsageJournal(_directory, _ => warnings++, false);
            journal.Record("test", null, Ok, 1);
            Assert.DoesNotThrow(journal.Flush);
            journal.Flush();
            Assert.That(warnings, Is.EqualTo(1));
            File.Delete(_directory);
            journal.Flush();
            Assert.That((int)JObject.Parse(File.ReadAllText(Path.Combine(_directory, "statistics.json")))["tools"]["test"]["calls"], Is.EqualTo(1));
        }

        [Test]
        public void ConcurrentLargeWorkloadHasBoundedToolCardinality()
        {
            using var journal = new McpUsageJournal(_directory, Assert.Fail, false);
            var response = Ok;
            var watch = Stopwatch.StartNew();
            Parallel.For(0, 10000, i => journal.Record("tool." + (i % 1200), null, response, 1));
            watch.Stop();
            TestContext.WriteLine($"10,000 concurrent records: {watch.ElapsedMilliseconds} ms; mean {watch.Elapsed.TotalMilliseconds / 10000:F4} ms/record (includes contention).");
            journal.Flush();
            var tools = (JObject)JObject.Parse(File.ReadAllText(Path.Combine(_directory, "statistics.json")))["tools"];
            Assert.That(tools.Count, Is.LessThanOrEqualTo(1025));
            long total = 0;
            foreach (var property in tools.Properties()) total += (long)property.Value["calls"];
            Assert.That(total, Is.EqualTo(10000));
        }
    }
}
