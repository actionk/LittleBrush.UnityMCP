using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public sealed class MaterialProviderTests
    {
        [Test]
        public void RegisterTools_ExposesMaterialAndShaderTools()
        {
            var sink = new CollectingSink();
            new MaterialProvider().RegisterTools(sink);
            var names = sink.Tools.Select(tool => tool.Name);
            Assert.That(names, Contains.Item("material.read"));
            Assert.That(names, Contains.Item("material.write"));
            Assert.That(names, Contains.Item("shader.inspect"));
        }

        [Test]
        public void Read_PaginatesPropertiesAndOmitsDescriptionsByDefault()
        {
            const string folder = "Assets/__mcp_material_test__";
            const string path = folder + "/Paged.mat";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "__mcp_material_test__");
            var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            Assert.That(shader, Is.Not.Null);
            AssetDatabase.CreateAsset(new Material(shader), path);

            try
            {
                var sink = new CollectingSink();
                new MaterialProvider().RegisterTools(sink);
                var tool = sink.Tools.Single(item => item.Name == "material.read");
                var result = tool.Handler(Ctx(new JObject
                {
                    ["path"] = path,
                    ["propertyLimit"] = 1,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;

                Assert.That((int)result["propertyCount"], Is.GreaterThan(1));
                Assert.That((int)result["returnedProperties"], Is.EqualTo(1));
                Assert.That((bool)result["propertiesTruncated"], Is.True);
                Assert.That((int)result["nextPropertyOffset"], Is.EqualTo(1));
                Assert.That(result["properties"][0]["description"], Is.Null);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static ToolContext Ctx(JObject args) => new(
            args,
            new NoopProgressReporter(),
            new FakeMainThreadPump(),
            new FakeFrameWaiter(),
            null,
            new FakeLogSink(),
            "req");

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
