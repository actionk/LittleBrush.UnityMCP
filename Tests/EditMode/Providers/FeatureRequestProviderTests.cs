using System;
using System.Diagnostics;
using System.IO;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Providers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class FeatureRequestProviderTests
    {
        private string _directory;
        private string FilePath => Path.Combine(_directory, "feature-requests.json");
        private static JObject Request(string key = "scene.preview.camera-pose") => new()
        {
            ["capability"] = key, ["task"] = "Inspect a close view", ["missing"] = "Explicit camera position",
            ["checkedTools"] = "scene.preview_screenshot: auto-framing does not provide a position argument",
            ["workaround"] = "Custom preview snippet", ["benefit"] = "Estimate: replace one custom snippet with one typed call",
        };
        [SetUp] public void Setup() => _directory = Path.Combine(Path.GetTempPath(), "mcp-feature-" + Guid.NewGuid());
        [TearDown] public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

        [Test]
        public void RegistrationDeclaresBackgroundProjectWrite()
        {
            var registry = new ToolRegistry();
            registry.SetEditorProviders(new IToolProvider[] { new FeatureRequestProvider() });
            Assert.That(registry.TryGet("mcp.feature_request", out var tool), Is.True);
            Assert.That(tool.RequiresMainThread, Is.False);
            Assert.That(tool.TrustCategory, Is.EqualTo(ToolTrustCategory.ProjectWrite));
            Assert.That(tool.ReloadSafe, Is.False);
        }

        [Test]
        public void RepeatedSubmissionsReloadAndMergeWithBoundedExamples()
        {
            var first = FeatureRequestProvider.Submit(FilePath, Request());
            Assert.That((bool)first["merged"], Is.False);
            for (var i = 0; i < 4; i++)
            {
                var request = Request(); request["task"] = "Task " + i;
                Assert.That((bool)FeatureRequestProvider.Submit(FilePath, request)["merged"], Is.True);
            }
            var requests = (JObject)JObject.Parse(File.ReadAllText(FilePath))["requests"];
            Assert.That(requests.Count, Is.EqualTo(1));
            var entry = requests["scene.preview.camera-pose"];
            Assert.That((long)entry["submissions"], Is.EqualTo(5));
            Assert.That(((JArray)entry["examples"]).Count, Is.EqualTo(3));
            Assert.That((string)entry["examples"][0]["task"], Is.EqualTo("Task 1"));
        }

        [Test]
        public void InvalidInputAndCorruptFileArePreserved()
        {
            var request = Request(); request["checkedTools"] = " ";
            Assert.Throws<McpToolException>(() => FeatureRequestProvider.Submit(FilePath, request));
            Assert.That(File.Exists(FilePath), Is.False);
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, "broken JSON");
            Assert.Throws<McpToolException>(() => FeatureRequestProvider.Submit(FilePath, Request()));
            Assert.That(File.ReadAllText(FilePath), Is.EqualTo("broken JSON"));
        }

        [Test]
        public void FullCatalogAllowsMergeButPreservesDataOnOverflow()
        {
            Directory.CreateDirectory(_directory);
            var requests = new JObject();
            var sample = Request(); sample.Remove("capability");
            foreach (var property in sample.Properties()) property.Value = new string('x', 1000);
            for (var i = 0; i < FeatureRequestProvider.MaxRequests; i++)
                requests["feature." + i] = new JObject { ["submissions"] = 1, ["examples"] = new JArray(sample.DeepClone(), sample.DeepClone(), sample.DeepClone()) };
            File.WriteAllText(FilePath, new JObject { ["schemaVersion"] = 1, ["requests"] = requests }.ToString());
            var watch = Stopwatch.StartNew();
            FeatureRequestProvider.Submit(FilePath, Request("feature.0"));
            watch.Stop();
            TestContext.WriteLine($"Merge into 256 requests / 768 examples: {watch.ElapsedMilliseconds} ms; file {new FileInfo(FilePath).Length} bytes; background I/O.");
            var before = File.ReadAllText(FilePath);
            Assert.Throws<McpToolException>(() => FeatureRequestProvider.Submit(FilePath, Request("feature.overflow")));
            Assert.That(File.ReadAllText(FilePath), Is.EqualTo(before));
        }
    }
}
