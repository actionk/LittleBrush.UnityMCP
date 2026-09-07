using System;
using System.Collections.Generic;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Providers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public sealed class ContentProviderTests
    {
        [Test]
        public void RegisterTools_ExposesContentWorkflow()
        {
            var sink = new CollectingSink();
            new ContentProvider().RegisterTools(sink);
            var names = sink.Tools.Select(tool => tool.Name);
            Assert.That(names, Contains.Item("content.status"));
            Assert.That(names, Contains.Item("content.read"));
            Assert.That(names, Contains.Item("content.write"));
            Assert.That(names, Contains.Item("content.reindex"));
            Assert.That(names, Contains.Item("content.reload"));
        }

        [Test]
        public void RegisterTools_ReindexAwaitsCompletion()
        {
            var sink = new CollectingSink();
            new ContentProvider().RegisterTools(sink);
            var tool = sink.Tools.Single(tool => tool.Name == "content.reindex");

            Assert.That(tool.Execution, Is.EqualTo(ToolExecution.Async));
            Assert.That(tool.Timeout, Is.EqualTo(TimeSpan.FromMinutes(2)));
        }

        [Test]
        public void ValidatePath_RejectsGeneratedAndSystemFiles()
        {
            Assert.Throws<McpToolException>(() => ContentProvider.ValidatePath("Assets/Resources/Content/index.json", false));
            Assert.Throws<McpToolException>(() => ContentProvider.ValidatePath("Assets/Resources/Content/_system/item.json", false));
        }

        [Test]
        public void ReadProjection_DefaultsToPagedSummariesAndSupportsTargetedFullValues()
        {
            var document = JObject.Parse(@"{
                ""items"": [{ ""id"": ""one"" }, { ""id"": ""two"" }],
                ""description"": ""compact""
            }");

            var summary = ContentProvider.CreateReadResponse("content.json", "hash", document, new JObject());
            Assert.That((string)summary["detail"], Is.EqualTo("summary"));
            Assert.That(summary["content"], Is.Null);
            Assert.That((int)summary["total"], Is.EqualTo(2));
            Assert.That((string)summary["items"][0]["key"], Is.EqualTo("items"));

            var full = ContentProvider.CreateReadResponse("content.json", "hash", document, new JObject
            {
                ["pointer"] = "/items",
                ["detail"] = "full",
                ["offset"] = 1,
                ["limit"] = 1,
            });
            Assert.That((string)full["content"][0]["id"], Is.EqualTo("two"));
            Assert.That((bool)full["truncated"], Is.False);
        }

        [Test]
        public void ReadProjection_RejectsOversizedFullPage()
        {
            var document = new JObject { ["large"] = new string('x', 2000) };

            var ex = Assert.Throws<McpToolException>(() => ContentProvider.CreateReadResponse(
                "content.json", "hash", document, new JObject
                {
                    ["detail"] = "full",
                    ["maxContentCharacters"] = 1000,
                }));

            Assert.That(ex.Message, Does.Contain("narrow pointer/limit"));
        }

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
