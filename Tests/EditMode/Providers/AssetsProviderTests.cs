using System;
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
    [Serializable]
    public struct AssetsProviderNestedProbe
    {
        public int number;
    }

    public sealed class AssetsProviderProbe : ScriptableObject
    {
        public AssetsProviderNestedProbe nested;
        public int[] values;
        public long rendererId;
        public string text;
        public Gradient gradient;
        public AnimationCurve curve;
    }

    public sealed class AssetsProviderTests
    {
        private const string Folder = "Assets/__assets_provider_test__";
        private const string Path = Folder + "/Probe.asset";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "__assets_provider_test__");
            AssetDatabase.DeleteAsset(Path);
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(Folder);

        [Test]
        public void RegisterTools_ExposesSerializedAssetTools()
        {
            var names = Collect().Tools.Select(tool => tool.Name);
            Assert.That(names, Contains.Item("project.assets.read"));
            Assert.That(names, Contains.Item("project.assets.write"));
            Assert.That(names, Contains.Item("project.assets.create"));
            Assert.That(names, Contains.Item("project.importer.read"));
            Assert.That(names, Contains.Item("project.importer.write"));
            Assert.That(names, Contains.Item("project.importer.write_many"));
            Assert.That(names, Contains.Item("project.settings.list"));
            Assert.That(names, Contains.Item("project.settings.read"));
            Assert.That(names, Contains.Item("project.settings.write"));
        }

        [Test]
        public void NormalizeProjectSettingsPath_RejectsTraversal()
        {
            Assert.Throws<McpToolException>(() => AssetsProvider.NormalizeProjectSettingsPath("ProjectSettings/../Assets/x.asset"));
        }

        [TestCase(long.MinValue)]
        [TestCase(long.MaxValue)]
        public void WriteAndRead_Preserve64BitIdentifiers(long value)
        {
            CreateProbe();
            GetTool("project.assets.write").Handler(Ctx(new JObject
            {
                ["path"] = Path,
                ["expectedHash"] = AssetDatabase.GetAssetDependencyHash(Path).ToString(),
                ["properties"] = new JObject { ["rendererId"] = value },
            }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var result = GetTool("project.assets.read").Handler(Ctx(new JObject
            {
                ["path"] = Path,
                ["propertySearch"] = "rendererId",
            }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That((long)result.StructuredContent["properties"]["rendererId"], Is.EqualTo(value));
            Assert.That(AssetDatabase.LoadAssetAtPath<AssetsProviderProbe>(Path).rendererId, Is.EqualTo(value));
        }

        [Test]
        public void Read_ReturnsNestedPropertiesAndHash()
        {
            CreateProbe();
            var result = GetTool("project.assets.read").Handler(Ctx(new JObject
            {
                ["path"] = Path,
                ["includeProperties"] = true,
            }), CancellationToken.None).AsTask().GetAwaiter().GetResult();

            Assert.That((string)result.StructuredContent["hash"], Is.Not.Empty);
            Assert.That((int)result.StructuredContent["properties"]["nested.number"], Is.EqualTo(3));
            Assert.That((int)result.StructuredContent["properties"]["values.Array.data[1]"], Is.EqualTo(5));
        }

        [Test]
        public void Read_CurveValuesAreOptIn_AndWriteRoundTripsThem()
        {
            CreateProbe();
            var read = GetTool("project.assets.read");

            var compact = read.Handler(Ctx(new JObject { ["path"] = Path, ["includeProperties"] = true }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That((string)compact.StructuredContent["properties"]["gradient"], Is.EqualTo("<Gradient>"));
            Assert.That((string)compact.StructuredContent["properties"]["curve"], Is.EqualTo("<AnimationCurve>"));

            var expanded = read.Handler(Ctx(new JObject
            {
                ["path"] = Path,
                ["includeProperties"] = true,
                ["includeCurveValues"] = true,
            }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That(expanded.StructuredContent["properties"]["gradient"]["colorKeys"], Is.TypeOf<JArray>());
            Assert.That(expanded.StructuredContent["properties"]["curve"]["keys"], Is.TypeOf<JArray>());

            var writeArgs = new JObject
            {
                ["path"] = Path,
                ["expectedHash"] = AssetDatabase.GetAssetDependencyHash(Path).ToString(),
                ["properties"] = new JObject
                {
                    ["curve"] = new JObject
                    {
                        ["preWrapMode"] = "Loop",
                        ["postWrapMode"] = "PingPong",
                        ["keys"] = new JArray
                        {
                            new JObject { ["time"] = 0f, ["value"] = 2f, ["inTangent"] = 0f, ["outTangent"] = 1f },
                            new JObject { ["time"] = 1f, ["value"] = 4f, ["inTangent"] = 1f, ["outTangent"] = 0f },
                        },
                    },
                    ["gradient"] = new JObject
                    {
                        ["mode"] = "Fixed",
                        ["colorKeys"] = new JArray
                        {
                            new JObject { ["color"] = new JObject { ["r"] = 1f, ["g"] = 0f, ["b"] = 0f, ["a"] = 1f }, ["time"] = 0f },
                            new JObject { ["color"] = new JObject { ["r"] = 0f, ["g"] = 0f, ["b"] = 1f, ["a"] = 1f }, ["time"] = 1f },
                        },
                        ["alphaKeys"] = new JArray
                        {
                            new JObject { ["alpha"] = 1f, ["time"] = 0f },
                            new JObject { ["alpha"] = 0.5f, ["time"] = 1f },
                        },
                    },
                },
            };
            GetTool("project.assets.write").Handler(Ctx(writeArgs), CancellationToken.None).AsTask().GetAwaiter().GetResult();

            var probe = AssetDatabase.LoadAssetAtPath<AssetsProviderProbe>(Path);
            Assert.That(probe.curve.preWrapMode, Is.EqualTo(WrapMode.Loop));
            Assert.That(probe.curve.postWrapMode, Is.EqualTo(WrapMode.PingPong));
            Assert.That(probe.curve.keys[1].value, Is.EqualTo(4f).Within(0.001f));
            Assert.That(probe.gradient.mode, Is.EqualTo(GradientMode.Fixed));
            Assert.That(probe.gradient.Evaluate(0f).r, Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void Write_RequiresHashAndHonorsDryRun()
        {
            CreateProbe();
            var hash = AssetDatabase.GetAssetDependencyHash(Path).ToString();
            var args = new JObject
            {
                ["path"] = Path,
                ["expectedHash"] = hash,
                ["properties"] = new JObject { ["nested.number"] = 9 },
                ["dryRun"] = true,
            };
            GetTool("project.assets.write").Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That(AssetDatabase.LoadAssetAtPath<AssetsProviderProbe>(Path).nested.number, Is.EqualTo(3));

            args["dryRun"] = false;
            GetTool("project.assets.write").Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That(AssetDatabase.LoadAssetAtPath<AssetsProviderProbe>(Path).nested.number, Is.EqualTo(9));
        }

        [Test]
        public void Read_DefaultsToMetadataAndSupportsFilteredPropertyPages()
        {
            CreateProbe();
            var read = GetTool("project.assets.read");

            var metadata = read.Handler(Ctx(new JObject { ["path"] = Path }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert.That(metadata.StructuredContent["properties"], Is.Null);
            Assert.That(metadata.StructuredContent["dependencies"], Is.Null);
            Assert.That(metadata.StructuredContent["dependencyCount"], Is.Not.Null);
            Assert.That((string)metadata.StructuredContent["hash"], Is.Not.Empty);

            var dependencies = read.Handler(Ctx(new JObject { ["path"] = Path, ["includeDependencies"] = true }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert.That(dependencies.StructuredContent["dependencies"], Is.TypeOf<JArray>());

            var page = read.Handler(Ctx(new JObject
            {
                ["path"] = Path,
                ["propertySearch"] = "values.Array.data",
                ["limit"] = 1,
            }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That((int)page.StructuredContent["filteredPropertyCount"], Is.EqualTo(2));
            Assert.That((int)page.StructuredContent["returned"], Is.EqualTo(1));
            Assert.That((bool)page.StructuredContent["truncated"], Is.True);
        }

        [Test]
        public void Create_DryRunThenCreate()
        {
            var args = new JObject
            {
                ["path"] = Path,
                ["type"] = typeof(AssetsProviderProbe).FullName,
                ["properties"] = new JObject { ["nested.number"] = 7 },
                ["dryRun"] = true,
            };
            GetTool("project.assets.create").Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That(AssetDatabase.LoadMainAssetAtPath(Path), Is.Null);

            args["dryRun"] = false;
            GetTool("project.assets.create").Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That(AssetDatabase.LoadAssetAtPath<AssetsProviderProbe>(Path).nested.number, Is.EqualTo(7));
        }

        [Test]
        public void ListProjectSettings_DefaultPageIsBounded()
        {
            var result = GetTool("project.settings.list").Handler(Ctx(new JObject()), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;

            Assert.That((int)result["returned"], Is.LessThanOrEqualTo(25));
            Assert.That((int)result["total"], Is.GreaterThanOrEqualTo((int)result["returned"]));
            Assert.That((bool)result["truncated"], Is.EqualTo((int)result["total"] > 25));
            var settings = (JArray)result["settings"];
            if (settings.Count > 0)
                Assert.That(settings[0]["hash"], Is.Null);
        }

        [Test]
        public void Read_DefaultPropertyPageIsBounded()
        {
            CreateProbe();
            var probe = AssetDatabase.LoadAssetAtPath<AssetsProviderProbe>(Path);
            probe.values = Enumerable.Range(0, 40).ToArray();
            EditorUtility.SetDirty(probe);
            AssetDatabase.SaveAssetIfDirty(probe);

            var result = GetTool("project.assets.read").Handler(Ctx(new JObject
            {
                ["path"] = Path,
                ["includeProperties"] = true,
            }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;

            Assert.That((int)result["returned"], Is.EqualTo(25));
            Assert.That((bool)result["truncated"], Is.True);
            Assert.That((int)result["nextOffset"], Is.EqualTo(25));
        }

        [Test]
        public void Read_BoundsSerializedStringsByDefault()
        {
            CreateProbe();
            var probe = AssetDatabase.LoadAssetAtPath<AssetsProviderProbe>(Path);
            probe.text = new string('x', 600);
            EditorUtility.SetDirty(probe);
            AssetDatabase.SaveAssetIfDirty(probe);

            var result = GetTool("project.assets.read").Handler(Ctx(new JObject
            {
                ["path"] = Path,
                ["propertyPaths"] = new JArray("text"),
                ["maxStringCharacters"] = 256,
            }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;
            var text = (JObject)result["properties"]["text"];

            Assert.That((string)text["$type"], Is.EqualTo("truncatedString"));
            Assert.That(((string)text["text"]).Length, Is.EqualTo(256));
            Assert.That((int)text["characters"], Is.EqualTo(600));
        }

        private static void CreateProbe()
        {
            var probe = ScriptableObject.CreateInstance<AssetsProviderProbe>();
            probe.nested.number = 3;
            probe.values = new[] { 4, 5 };
            probe.text = "short";
            probe.gradient = new Gradient();
            probe.gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.black, 0f),
                    new GradientColorKey(Color.white, 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.5f, 1f),
                });
            probe.curve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 3f));
            AssetDatabase.CreateAsset(probe, Path);
            AssetDatabase.SaveAssetIfDirty(probe);
        }

        private static ToolDescriptor GetTool(string name) => Collect().Tools.Single(tool => tool.Name == name);

        private static CollectingSink Collect()
        {
            var sink = new CollectingSink();
            new AssetsProvider().RegisterTools(sink);
            return sink;
        }

        private static ToolContext Ctx(JObject arguments) => new(arguments, new NoopProgressReporter(),
            new FakeMainThreadPump(), new FakeFrameWaiter(), null, new FakeLogSink(), "req");

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
