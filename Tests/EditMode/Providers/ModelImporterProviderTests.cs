using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class ModelImporterProviderTests
    {
        private const string FixtureRoot = "Assets/__mcp_model_importer_tests__";
        private const string FixturePath = FixtureRoot + "/Clip.dae";
        private const string AnimatedCollada = @"<?xml version=""1.0"" encoding=""utf-8""?>
<COLLADA xmlns=""http://www.collada.org/2005/11/COLLADASchema"" version=""1.4.1"">
  <asset><created>2026-01-01T00:00:00Z</created><modified>2026-01-01T00:00:00Z</modified><unit name=""meter"" meter=""1""/><up_axis>Y_UP</up_axis></asset>
  <library_geometries>
    <geometry id=""Triangle-mesh"" name=""Triangle""><mesh>
      <source id=""Triangle-positions""><float_array id=""Triangle-positions-array"" count=""9"">0 0 0 1 0 0 0 1 0</float_array><technique_common><accessor source=""#Triangle-positions-array"" count=""3"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common></source>
      <source id=""Triangle-normals""><float_array id=""Triangle-normals-array"" count=""3"">0 0 1</float_array><technique_common><accessor source=""#Triangle-normals-array"" count=""1"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common></source>
      <vertices id=""Triangle-vertices""><input semantic=""POSITION"" source=""#Triangle-positions""/></vertices>
      <triangles count=""1""><input semantic=""VERTEX"" source=""#Triangle-vertices"" offset=""0""/><input semantic=""NORMAL"" source=""#Triangle-normals"" offset=""1""/><p>0 0 1 0 2 0</p></triangles>
    </mesh></geometry>
  </library_geometries>
  <library_animations>
    <animation id=""Node-translate-X"">
      <source id=""Node-translate-X-input""><float_array id=""Node-translate-X-input-array"" count=""2"">0 1</float_array><technique_common><accessor source=""#Node-translate-X-input-array"" count=""2"" stride=""1""><param name=""TIME"" type=""float""/></accessor></technique_common></source>
      <source id=""Node-translate-X-output""><float_array id=""Node-translate-X-output-array"" count=""2"">0 1</float_array><technique_common><accessor source=""#Node-translate-X-output-array"" count=""2"" stride=""1""><param name=""X"" type=""float""/></accessor></technique_common></source>
      <source id=""Node-translate-X-interpolation""><Name_array id=""Node-translate-X-interpolation-array"" count=""2"">LINEAR LINEAR</Name_array><technique_common><accessor source=""#Node-translate-X-interpolation-array"" count=""2"" stride=""1""><param name=""INTERPOLATION"" type=""Name""/></accessor></technique_common></source>
      <sampler id=""Node-translate-X-sampler""><input semantic=""INPUT"" source=""#Node-translate-X-input""/><input semantic=""OUTPUT"" source=""#Node-translate-X-output""/><input semantic=""INTERPOLATION"" source=""#Node-translate-X-interpolation""/></sampler>
      <channel source=""#Node-translate-X-sampler"" target=""Node/translation.X""/>
    </animation>
  </library_animations>
  <library_animation_clips><animation_clip id=""Take-001"" name=""Take 001"" start=""0"" end=""1""><instance_animation url=""#Node-translate-X""/></animation_clip></library_animation_clips>
  <library_visual_scenes><visual_scene id=""Scene"" name=""Scene""><node id=""Node"" name=""Node"" sid=""Node"" type=""NODE""><translate sid=""translation"">0 0 0</translate><instance_geometry url=""#Triangle-mesh""/></node></visual_scene></library_visual_scenes>
  <scene><instance_visual_scene url=""#Scene""/></scene>
</COLLADA>";

        [Test]
        public void RegisterTools_DeclaresAllModelImporterTools()
        {
            var sink = Collect(new ModelImporterProvider());
            var names = sink.Tools.Select(t => t.Name).ToList();
            Assert.That(names, Contains.Item("model_importer.get"));
            Assert.That(names, Contains.Item("model_importer.set"));
            Assert.That(names, Contains.Item("model_importer.set_many"));
            Assert.That(names, Contains.Item("model_importer.clips.get"));
            Assert.That(names, Contains.Item("model_importer.clips.set"));
            Assert.That(names, Contains.Item("model_importer.clips.set_many"));
            Assert.That(sink.Tools.Single(t => t.Name == "model_importer.clips.get")
                .InputSchema["properties"]["limit"]["maximum"].Value<int>(), Is.EqualTo(256));
            Assert.That(sink.Tools.Single(t => t.Name == "model_importer.clips.set")
                .InputSchema["properties"]["allowMetaFallback"], Is.Not.Null);
            var setMany = sink.Tools.Single(t => t.Name == "model_importer.clips.set_many");
            Assert.That(setMany.InputSchema["properties"]["allowMetaFallback"], Is.Not.Null);
            Assert.That(setMany.InputSchema["properties"]["items"]["maxItems"].Value<int>(), Is.EqualTo(256));
        }

        [Test]
        public void PatchClipMetaText_ChangesOnlyTheMatchingClipAndMappedFields()
        {
            const string source =
                "ModelImporter:\n" +
                "  animations:\n" +
                "    clipAnimations:\n" +
                "    - serializedVersion: 16\n" +
                "      name: Idle\n" +
                "      takeName: Idle\n" +
                "      loopBlend: 0\n" +
                "      loopBlendPositionY: 1\n" +
                "      keepOriginalPositionY: 1\n" +
                "      level: 0\n" +
                "    - serializedVersion: 16\n" +
                "      name: Walk\n" +
                "      takeName: Walk\n" +
                "      loopBlend: 0\n" +
                "      loopBlendPositionY: 1\n" +
                "      keepOriginalPositionY: 1\n" +
                "      level: 0\n" +
                "    animationCompression: 3\n";

            var patched = ModelImporterProvider.PatchClipMetaText(
                source,
                "Walk",
                "Walk",
                new JObject
                {
                    ["loopPose"] = true,
                    ["lockRootHeightY"] = false,
                    ["keepOriginalPositionY"] = false,
                    ["heightOffset"] = 1.25f,
                });

            Assert.That(patched, Does.Contain(
                "name: Idle\n      takeName: Idle\n      loopBlend: 0\n" +
                "      loopBlendPositionY: 1\n      keepOriginalPositionY: 1\n      level: 0"));
            Assert.That(patched, Does.Contain(
                "name: Walk\n      takeName: Walk\n      loopBlend: 1\n" +
                "      loopBlendPositionY: 0\n      keepOriginalPositionY: 0\n      level: 1.25"));
            Assert.That(patched, Does.EndWith("    animationCompression: 3\n"));
        }

        [Test]
        public void PatchClipMetaText_RejectsIdentityFields()
        {
            var ex = Assert.Throws<McpToolException>(() =>
                ModelImporterProvider.PatchClipMetaText(
                    "    clipAnimations:\n",
                    "Walk",
                    "Walk",
                    new JObject { ["name"] = "Run" }));
            Assert.That(ex.Message, Does.Contain("cannot use serialized .meta fallback"));
        }

        [Test]
        public void ClipsGet_Paginates()
        {
            var path = CreateFixture();
            try
            {
                var get = Tool("model_importer.clips.get");
                var first = get.Handler(Ctx(new JObject
                {
                    ["path"] = path,
                    ["limit"] = 1,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;

                Assert.That((int)first["clipCount"], Is.EqualTo(2));
                Assert.That((int)first["returned"], Is.EqualTo(1));
                Assert.That((bool)first["truncated"], Is.True);
                Assert.That((int)first["nextOffset"], Is.EqualTo(1));

                var second = get.Handler(Ctx(new JObject
                {
                    ["path"] = path,
                    ["offset"] = 1,
                    ["limit"] = 1,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((int)second["returned"], Is.EqualTo(1));
                Assert.That((bool)second["truncated"], Is.False);
            }
            finally
            {
                DeleteFixture();
            }
        }

        [Test]
        public void PatchClipMetaText_RoundTripsGeneratedFixtureAndRestoresOriginal()
        {
            var path = CreateFixture();
            var metaPath = Path.GetFullPath(path + ".meta");
            var original = File.ReadAllBytes(metaPath);
            try
            {
                var get = Tool("model_importer.clips.get");
                var before = get.Handler(Ctx(new JObject { ["path"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                var clip = (JObject)before["clips"][0];
                var nextLoopPose = !(bool)clip["loopPose"];
                var patched = ModelImporterProvider.PatchClipMetaText(
                    Encoding.UTF8.GetString(original),
                    (string)clip["name"],
                    (string)clip["takeName"],
                    new JObject { ["loopPose"] = nextLoopPose });

                File.WriteAllText(metaPath, patched, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var changed = get.Handler(Ctx(new JObject { ["path"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((bool)changed["clips"][0]["loopPose"], Is.EqualTo(nextLoopPose));

                File.WriteAllBytes(metaPath, original);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var restored = get.Handler(Ctx(new JObject { ["path"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((bool)restored["clips"][0]["loopPose"], Is.EqualTo((bool)clip["loopPose"]));
            }
            finally
            {
                if (File.Exists(metaPath))
                {
                    File.WriteAllBytes(metaPath, original);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
                DeleteFixture();
            }
        }

        [Test]
        public void ClipsReadWrite_UsesHashDryRunAndExactReadback()
        {
            var path = CreateFixture();
            try
            {
                var get = Tool("model_importer.clips.get");
                var set = Tool("model_importer.clips.set");
                var first = get.Handler(Ctx(new JObject { ["path"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                var clip = (JObject)first["clips"][0];
                var clipName = (string)clip["name"];
                var nextLoopTime = !(bool)clip["loopTime"];
                var args = new JObject
                {
                    ["path"] = path,
                    ["expectedHash"] = (string)first["hash"],
                    ["clipName"] = clipName,
                    ["properties"] = new JObject { ["loopTime"] = nextLoopTime },
                    ["dryRun"] = true,
                };

                var dryRun = set.Handler(Ctx(args), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((bool)dryRun["dryRun"], Is.True);
                Assert.That((bool)dryRun["reimported"], Is.False);
                Assert.That((bool)dryRun["clip"]["loopTime"], Is.EqualTo((bool)clip["loopTime"]));

                args["dryRun"] = false;
                var written = set.Handler(Ctx(args), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((bool)written["reimported"], Is.True);
                Assert.That((bool)written["clip"]["loopTime"], Is.EqualTo(nextLoopTime));
                Assert.That((string)written["hash"], Is.Not.Empty);

                var stale = Assert.ThrowsAsync<McpToolException>(async () =>
                    await set.Handler(Ctx(args), CancellationToken.None));
                Assert.That(stale.Code, Is.EqualTo(McpErrorCodes.Conflict));
            }
            finally
            {
                DeleteFixture();
            }
        }

        [Test]
        public void ClipsSetMany_RequiresPerItemExpectedHash()
        {
            var tool = Tool("model_importer.clips.set_many");
            var args = new JObject
            {
                ["items"] = new JArray(new JObject
                {
                    ["path"] = "Assets/x.fbx",
                    ["clipName"] = "Walk",
                    ["properties"] = new JObject { ["loopTime"] = true },
                }),
            };
            var ex = Assert.ThrowsAsync<McpToolException>(async () =>
                await tool.Handler(Ctx(args), CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("expectedHash"));
        }

        [Test]
        public void ClipsSetMany_RejectsOversizedBatch()
        {
            var items = new JArray();
            for (var i = 0; i < 257; i++)
                items.Add(new JObject());

            var ex = Assert.ThrowsAsync<McpToolException>(async () =>
                await Tool("model_importer.clips.set_many").Handler(
                    Ctx(new JObject { ["items"] = items }), CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("between 1 and 256"));
        }

        [Test]
        public void ClipsSetMany_PersistsImportedSettings()
        {
            var path = CreateFixture();
            try
            {
                var get = Tool("model_importer.clips.get");
                var setMany = Tool("model_importer.clips.set_many");
                var before = get.Handler(Ctx(new JObject { ["path"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                var clip = (JObject)before["clips"][0];
                var nextKeepOriginalPositionY = !(bool)clip["keepOriginalPositionY"];
                var nextHeightFromFeet = !(bool)clip["heightFromFeet"];

                setMany.Handler(Ctx(new JObject
                {
                    ["items"] = new JArray(new JObject
                    {
                        ["path"] = path,
                        ["expectedHash"] = (string)before["hash"],
                        ["clipName"] = (string)clip["name"],
                        ["properties"] = new JObject
                        {
                            ["keepOriginalPositionY"] = nextKeepOriginalPositionY,
                            ["heightFromFeet"] = nextHeightFromFeet,
                        },
                    }),
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                var after = get.Handler(Ctx(new JObject { ["path"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((bool)after["clips"][0]["keepOriginalPositionY"], Is.EqualTo(nextKeepOriginalPositionY));
                Assert.That((bool)after["clips"][0]["heightFromFeet"], Is.EqualTo(nextHeightFromFeet));
            }
            finally
            {
                DeleteFixture();
            }
        }

        [Test]
        public void Set_RejectsUnknownProperty()
        {
            var tool = Collect(new ModelImporterProvider()).Tools.First(t => t.Name == "model_importer.set");
            var args = new JObject
            {
                ["path"] = "Assets/x.fbx",
                ["properties"] = new JObject { ["nonExistent"] = true },
            };
            var ctx = Ctx(args);
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("whitelist"));
        }

        [Test]
        public void Set_RejectsNonModelAsset()
        {
            var tool = Collect(new ModelImporterProvider()).Tools.First(t => t.Name == "model_importer.set");
            var args = new JObject
            {
                ["path"] = "Assets/__not_a_model__.png",
                ["properties"] = new JObject { ["importAnimation"] = true },
            };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        private static ToolDescriptor Tool(string name)
            => Collect(new ModelImporterProvider()).Tools.First(t => t.Name == name);

        private static string CreateFixture()
        {
            if (!AssetDatabase.IsValidFolder(FixtureRoot))
                AssetDatabase.CreateFolder("Assets", "__mcp_model_importer_tests__");
            File.WriteAllText(Path.GetFullPath(FixturePath), AnimatedCollada, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(FixturePath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(FixturePath) as ModelImporter;
            Assert.That(importer, Is.Not.Null, $"Generated model importer fixture did not import: '{FixturePath}'.");
            var defaults = importer.defaultClipAnimations;
            Assert.That(defaults, Is.Not.Empty, "Generated Collada fixture must contain an animation take.");
            var take = defaults[0];
            importer.clipAnimations = new[]
            {
                NewClip("Clip A", take),
                NewClip("Clip B", take),
            };
            importer.SaveAndReimport();
            importer = AssetImporter.GetAtPath(FixturePath) as ModelImporter;
            Assert.That(importer?.clipAnimations, Has.Length.EqualTo(2));
            return FixturePath;
        }

        private static ModelImporterClipAnimation NewClip(string name, ModelImporterClipAnimation take)
            => new()
            {
                name = name,
                takeName = take.takeName,
                firstFrame = take.firstFrame,
                lastFrame = take.lastFrame,
                wrapMode = take.wrapMode,
                loopTime = take.loopTime,
                loopPose = take.loopPose,
            };

        private static void DeleteFixture()
        {
            AssetDatabase.DeleteAsset(FixtureRoot);
            AssetDatabase.SaveAssets();
        }

        private static CollectingSink Collect(IToolProvider p) { var s = new CollectingSink(); p.RegisterTools(s); return s; }

        private static ToolContext Ctx(JObject args) => new(args, new NoopProgressReporter(), new FakeMainThreadPump(),
            new FakeFrameWaiter(), null, new FakeLogSink(), "req");

        private sealed class CollectingSink : IToolRegistration
        {
            public List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
