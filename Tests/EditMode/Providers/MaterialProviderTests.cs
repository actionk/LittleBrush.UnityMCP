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
            Assert.That(sink.Tools.Single(tool => tool.Name == "material.write").Availability, Is.EqualTo(ToolAvailability.Either));
            Assert.That(sink.Tools.Single(tool => tool.Name == "material.write").InputSchema["additionalProperties"]?.Value<bool>(), Is.False);
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

                var texturePath = folder + "/Tiling.asset";
                AssetDatabase.CreateAsset(new Texture2D(2, 2), texturePath);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                var write = sink.Tools.Single(item => item.Name == "material.write");
                var args = new JObject
                {
                    ["path"] = path,
                    ["expectedHash"] = result["hash"],
                    ["dryRun"] = true,
                    ["properties"] = new JObject { ["_MainTex"] = new JObject
                    {
                        ["assetPath"] = texturePath,
                        ["scale"] = new JObject { ["x"] = 3f, ["y"] = 2f },
                        ["offset"] = new JObject { ["x"] = .1f, ["y"] = .2f },
                    } },
                };
                write.Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That(material.GetTextureScale("_MainTex"), Is.EqualTo(Vector2.one), "Dry-run must preserve the material.");
                args["dryRun"] = false;
                write.Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Assert.That(material.GetTextureScale("_MainTex"), Is.EqualTo(new Vector2(3, 2)));
                Assert.That(material.GetTextureOffset("_MainTex"), Is.EqualTo(new Vector2(.1f, .2f)));
                var readback = tool.Handler(Ctx(new JObject { ["path"] = path, ["propertyLimit"] = 100 }),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;
                var textureValue = readback["properties"].Single(p => (string)p["name"] == "_MainTex")["value"];
                Assert.That((float)textureValue["scale"]["x"], Is.EqualTo(3f));
                Assert.That((float)textureValue["offset"]["y"], Is.EqualTo(.2f));
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
