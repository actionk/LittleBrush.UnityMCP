using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public sealed class AssetProviderSubassetProbe : ScriptableObject
    {
    }

    public class AssetProviderTests
    {
        [Test]
        public void RegisterTools_DeclaresAllAssetTools()
        {
            var sink = CollectFor(new AssetProvider());
            var names = sink.Tools.Select(t => t.Name).ToList();
            Assert.That(names, Contains.Item("asset.find"));
            Assert.That(names, Contains.Item("asset.create_folder"));
            Assert.That(names, Contains.Item("asset.copy"));
            Assert.That(names, Contains.Item("asset.move"));
            Assert.That(names, Contains.Item("asset.delete"));
            Assert.That(names, Contains.Item("asset.import"));
            Assert.That(names, Contains.Item("asset.subassets.list"));
            Assert.That(names, Contains.Item("asset.animation_clip.extract"));
        }

        [Test]
        public void Copy_RejectsPathOutsideAssetsOrPackages()
        {
            var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.copy");
            var args = new JObject { ["src"] = "Library/Foo.prefab", ["dest"] = "Assets/Foo.prefab" };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        [Test]
        public void Copy_RejectsSameSourceAndDest()
        {
            var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.copy");
            var args = new JObject { ["src"] = "Assets/Foo.prefab", ["dest"] = "Assets/Foo.prefab", ["overwrite"] = true };
            var ctx = Ctx(args);
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("different"));
        }

        [Test]
        public void Copy_OverwriteReplacesExistingAsset()
        {
            const string folder = "Assets/__asset_copy_overwrite_test__";
            const string src = "Assets/__asset_copy_overwrite_test__/Source.asset";
            const string dest = "Assets/__asset_copy_overwrite_test__/Dest.asset";

            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__asset_copy_overwrite_test__");

            var srcAsset = new AnimationClip { frameRate = 12f };
            var destAsset = new AnimationClip { frameRate = 24f };
            UnityEditor.AssetDatabase.CreateAsset(srcAsset, src);
            UnityEditor.AssetDatabase.CreateAsset(destAsset, dest);

            try
            {
                var originalGuid = UnityEditor.AssetDatabase.AssetPathToGUID(dest);
                var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.copy");
                var args = new JObject { ["src"] = src, ["dest"] = dest, ["overwrite"] = true };
                tool.Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                var loaded = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(dest);
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.frameRate, Is.EqualTo(12f));
                Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(dest), Is.EqualTo(originalGuid));
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void Find_ReturnsStructuredEnvelope()
        {
            var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.find");
            var args = new JObject { ["type"] = "Texture2D", ["limit"] = 1 };
            var ctx = Ctx(args);
            var result = tool.Handler(ctx, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That(result.StructuredContent["items"], Is.Not.Null);
            Assert.That(result.StructuredContent["total"], Is.Not.Null);
            Assert.That(result.StructuredContent["returned"], Is.Not.Null);
        }

        [Test]
        public void ListSubAssets_ReturnsAnimationClipIdentity()
        {
            const string folder = "Assets/__asset_subasset_test__";
            const string path = folder + "/Source.anim";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__asset_subasset_test__");
            var source = new AnimationClip { frameRate = 12f, name = "Attack" };
            UnityEditor.AssetDatabase.CreateAsset(source, path);
            var importedName = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(path).name;

            try
            {
                var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.subassets.list");
                var result = tool.Handler(Ctx(new JObject { ["path"] = path, ["type"] = "AnimationClip" }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                Assert.That((int)result.StructuredContent["total"], Is.EqualTo(1));
                Assert.That((string)result.StructuredContent["items"][0]["name"], Is.EqualTo(importedName));
                var item = result.StructuredContent["items"][0];
                Assert.That(item["localId"].Type, Is.EqualTo(Newtonsoft.Json.Linq.JTokenType.String));
                var localId = (long)item["localId"];
                Assert.That(localId, Is.Not.EqualTo(0));
                Assert.That((string)result.StructuredContent["guid"], Is.Not.Empty);
                Assert.That(item["reference"], Is.Null);
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void ListSubAssets_DefaultPageIsBoundedAndSupportsOffset()
        {
            const string folder = "Assets/__asset_subasset_page_test__";
            const string path = folder + "/Paged.asset";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__asset_subasset_page_test__");
            UnityEditor.AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<AssetProviderSubassetProbe>(), path);
            for (var i = 0; i < 30; i++)
                UnityEditor.AssetDatabase.AddObjectToAsset(new AnimationClip { name = $"Clip{i:00}" }, path);
            UnityEditor.AssetDatabase.SaveAssets();

            try
            {
                var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.subassets.list");
                var first = tool.Handler(Ctx(new JObject { ["path"] = path }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((int)first["total"], Is.EqualTo(31));
                Assert.That((int)first["returned"], Is.EqualTo(25));
                Assert.That((bool)first["truncated"], Is.True);
                Assert.That((int)first["nextOffset"], Is.EqualTo(25));

                var second = tool.Handler(Ctx(new JObject { ["path"] = path, ["offset"] = 25 }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((int)second["returned"], Is.EqualTo(6));
                Assert.That((bool)second["truncated"], Is.False);

                var extract = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.animation_clip.extract");
                var error = Assert.ThrowsAsync<McpToolException>(async () => await extract.Handler(Ctx(new JObject
                {
                    ["sourcePath"] = path,
                    ["destinationPath"] = folder + "/Missing.anim",
                    ["clipName"] = "missing",
                }), CancellationToken.None));
                Assert.That((int)error.Data["totalAvailable"], Is.EqualTo(30));
                Assert.That(((JArray)error.Data["availableClips"]).Count, Is.EqualTo(25));
                Assert.That((bool)error.Data["truncated"], Is.True);
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void ExtractAnimationClip_CreatesStandaloneCopy()
        {
            const string folder = "Assets/__asset_extract_test__";
            const string sourcePath = folder + "/Source.anim";
            const string destinationPath = folder + "/Extracted.anim";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "__asset_extract_test__");
            var source = new AnimationClip { frameRate = 24f, name = "Attack" };
            UnityEditor.AssetDatabase.CreateAsset(source, sourcePath);
            var importedName = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(sourcePath).name;

            try
            {
                var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.animation_clip.extract");
                tool.Handler(Ctx(new JObject
                {
                    ["sourcePath"] = sourcePath,
                    ["destinationPath"] = destinationPath,
                    ["clipName"] = importedName,
                }), CancellationToken.None).AsTask().GetAwaiter().GetResult();

                var extracted = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(destinationPath);
                Assert.That(extracted, Is.Not.Null);
                Assert.That(extracted, Is.Not.SameAs(source));
                Assert.That(extracted.frameRate, Is.EqualTo(24f));
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void CreateFolder_RejectsNameWithSeparator()
        {
            var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.create_folder");
            var args = new JObject { ["parent"] = "Assets", ["name"] = "Foo/Bar" };
            var ctx = Ctx(args);
            var ex = Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("separator"));
        }

        [Test]
        public void Delete_RejectsNonAssetsPath()
        {
            var tool = CollectFor(new AssetProvider()).Tools.First(t => t.Name == "asset.delete");
            var args = new JObject { ["paths"] = new JArray { "Library/Foo.asset" } };
            var ctx = Ctx(args);
            Assert.ThrowsAsync<McpToolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        }

        private static CollectingSink CollectFor(IToolProvider provider)
        {
            var sink = new CollectingSink();
            provider.RegisterTools(sink);
            return sink;
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
            public List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }

}
