#if HAS_VFX_GRAPH
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace LittleBrushGames.Mcp.Modules.VisualEffectGraph.Tests
{
    public sealed class VisualEffectGraphProviderTests
    {
        private const string Folder = "Assets/__mcp_vfx_tests__";
        private const string Path = Folder + "/RoundTrip.vfx";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "__mcp_vfx_tests__");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void CatalogWriteReadAndReplace_RoundTripsAndPreservesGuid()
        {
            var tools = Collect();
            CollectionAssert.AreEquivalent(
                new[] { "vfx.catalog", "vfx.graph.read", "vfx.graph.write" },
                tools.Select(tool => tool.Name));

            var catalog = Call(tools, "vfx.catalog", new JObject
            {
                ["kind"] = "context",
                ["search"] = "Initialize Particle",
                ["limit"] = 10,
            });
            Assert.That((int)catalog["count"], Is.GreaterThan(0));
            Assert.That((string)catalog["detail"], Is.EqualTo("summary"));
            Assert.That(catalog["items"][0]["settings"], Is.Null);

            var graph = SimpleGraph();
            var dryRun = Call(tools, "vfx.graph.write", new JObject
            {
                ["path"] = Path,
                ["graph"] = graph,
                ["dryRun"] = true,
            });
            Assert.That((bool)dryRun["compiled"], Is.True);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(Path), Is.Null);

            var created = Call(tools, "vfx.graph.write", new JObject { ["path"] = Path, ["graph"] = graph });
            var guid = (string)created["guid"];
            Assert.That(guid, Is.Not.Empty);

            var outline = Call(tools, "vfx.graph.read", new JObject { ["path"] = Path });
            Assert.That((string)outline["profile"], Is.EqualTo("outline"));
            Assert.That((int)outline["graph"]["contextCount"], Is.EqualTo(8));
            Assert.That(outline["graph"]["flowEdges"], Is.Null);

            var read = Call(tools, "vfx.graph.read", new JObject { ["path"] = Path, ["profile"] = "full" });
            Assert.That((int)read["graph"]["contexts"].Count(), Is.EqualTo(8));
            Assert.That((int)read["graph"]["flowEdges"].Count(), Is.EqualTo(6));
            Assert.That((int)read["graph"]["operators"].Count(), Is.EqualTo(2));
            Assert.That((int)read["graph"]["parameters"].Count(), Is.EqualTo(1));
            Assert.That((string)read["graph"]["parameters"][0]["name"], Is.EqualTo("Speed"));
            Assert.That((int)read["graph"]["dataEdges"].Count(), Is.EqualTo(2));
            Assert.That((int)read["graph"]["parameters"][0]["nodes"].Count(), Is.EqualTo(2));
            Assert.That((int)read["graph"]["ui"]["notes"].Count(), Is.EqualTo(1));
            Assert.That((int)read["graph"]["ui"]["groups"].Count(), Is.EqualTo(1));
            Assert.That((string)read["hash"], Is.Not.Empty);

            var replaced = Call(tools, "vfx.graph.write", new JObject
            {
                ["path"] = Path,
                ["expectedHash"] = (string)read["hash"],
                ["graph"] = read["graph"],
            });
            Assert.That((string)replaced["guid"], Is.EqualTo(guid));
        }

        private static JObject SimpleGraph()
        {
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["contexts"] = new JArray
                {
                    Node("spawn", "Context/#0Common/Spawn", 0, 0, new JArray
                    {
                        Node("burst", "Spawn/Single Burst", 0, 0),
                    }),
                    Node("initialize", "Context/#0Common/Initialize Particle", 300, 0),
                    Node("update", "Context/#0Common/Update Particle", 600, 0, new JArray
                    {
                        Node("trigger", "GPUEvent/Trigger Event|Over Time", 0, 0),
                    }),
                    new JObject
                    {
                        ["id"] = "output",
                        ["catalogId"] = "Context/#2Output Basic/Output Particle|Unlit|Quad",
                        ["position"] = new JObject { ["x"] = 900, ["y"] = 0 },
                        ["slots"] = new JObject
                        {
                            ["mainTexture"] = new JObject
                            {
                                ["guid"] = "0000000000000000f000000000000000",
                                ["path"] = "Resources/unity_builtin_extra",
                                ["name"] = "Default-Particle",
                                ["type"] = "UnityEngine.Texture2D",
                            },
                        },
                    },
                    Node("gpuEvent", "Context/#1Event/GPU Event", 0, 400),
                    Node("stripInitialize", "Context/#0Common/Initialize Particle Strip", 300, 400),
                    Node("stripUpdate", "Context/#0Common/Update Particle", 600, 400),
                    Node("stripOutput", "type:UnityEditor.VFX.VFXQuadStripOutput", 900, 400),
                },
                ["operators"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "count",
                        ["catalogId"] = "Operator/Inline/float",
                        ["position"] = new JObject { ["x"] = -200, ["y"] = 150 },
                        ["slots"] = new JObject { ["slot0"] = 128.0f },
                    },
                    new JObject
                    {
                        ["id"] = "vectorDivide",
                        ["catalogId"] = "Operator/Math/Arithmetic/Divide",
                        ["position"] = new JObject { ["x"] = -200, ["y"] = 220 },
                        ["inputSlots"] = new JArray
                        {
                            new JObject
                            {
                                ["path"] = "a",
                                ["type"] = "UnityEngine.Vector4",
                                ["value"] = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3, ["w"] = 4 },
                            },
                            new JObject { ["path"] = "b", ["type"] = "System.Single", ["value"] = 2 },
                        },
                    },
                },
                ["parameters"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "speed",
                        ["type"] = "System.Single",
                        ["name"] = "Speed",
                        ["exposed"] = true,
                        ["settings"] = new JObject { ["m_ExposedName"] = "StaleName" },
                        ["value"] = 2.5f,
                        ["nodes"] = new JArray
                        {
                            new JObject { ["id"] = 4, ["position"] = new JObject { ["x"] = -200, ["y"] = 300 } },
                            new JObject { ["id"] = 7, ["position"] = new JObject { ["x"] = -200, ["y"] = 420 } },
                        },
                    },
                },
                ["flowEdges"] = new JArray
                {
                    Edge("spawn", "initialize"),
                    Edge("initialize", "update"),
                    Edge("update", "output"),
                    Edge("gpuEvent", "stripInitialize"),
                    Edge("stripInitialize", "stripUpdate"),
                    Edge("stripUpdate", "stripOutput"),
                },
                ["dataEdges"] = new JArray
                {
                    new JObject
                    {
                        ["from"] = new JObject { ["node"] = "count", ["slot"] = "slot0" },
                        ["to"] = new JObject { ["node"] = "burst", ["slot"] = "Count" },
                    },
                    new JObject
                    {
                        ["from"] = new JObject { ["node"] = "trigger", ["slot"] = "evt" },
                        ["to"] = new JObject { ["node"] = "gpuEvent", ["slot"] = "evt" },
                    },
                },
                ["ui"] = new JObject
                {
                    ["notes"] = new JArray
                    {
                        new JObject
                        {
                            ["title"] = "Test note",
                            ["contents"] = "Round-trip",
                            ["position"] = new JObject { ["x"] = 0, ["y"] = 500, ["width"] = 220, ["height"] = 120 },
                        },
                    },
                    ["groups"] = new JArray
                    {
                        new JObject
                        {
                            ["title"] = "Inputs",
                            ["position"] = new JObject { ["x"] = -260, ["y"] = 250, ["width"] = 300, ["height"] = 350 },
                            ["contents"] = new JArray
                            {
                                new JObject { ["node"] = "speed", ["nodeId"] = 7 },
                                new JObject { ["note"] = 0 },
                            },
                        },
                    },
                },
            };
        }

        private static JObject Node(string id, string catalogId, float x, float y, JArray blocks = null)
        {
            var node = new JObject
            {
                ["id"] = id,
                ["catalogId"] = catalogId,
                ["position"] = new JObject { ["x"] = x, ["y"] = y },
            };
            if (blocks != null) node["blocks"] = blocks;
            return node;
        }

        private static JObject Edge(string from, string to)
            => new()
            {
                ["from"] = new JObject { ["node"] = from, ["slot"] = 0 },
                ["to"] = new JObject { ["node"] = to, ["slot"] = 0 },
            };

        private static JObject Call(IEnumerable<ToolDescriptor> tools, string name, JObject arguments)
            => tools.Single(tool => tool.Name == name).Handler(
                new ToolContext(arguments, new NoopProgressReporter(), new FakeMainThreadPump(), new FakeFrameWaiter(), null, new FakeLogSink(), "test"),
                CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;

        private static List<ToolDescriptor> Collect()
        {
            var sink = new CollectingSink();
            new VisualEffectGraphProvider().RegisterTools(sink);
            return sink.Tools;
        }

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
#endif
