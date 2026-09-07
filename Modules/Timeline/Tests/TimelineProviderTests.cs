#if HAS_UNITY_TIMELINE
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace LittleBrushGames.Mcp.Modules.Timeline.Tests
{
    public sealed class TimelineProviderTests
    {
        private const string Folder = "Assets/__mcp_timeline_tests__";
        private const string TimelinePath = Folder + "/Sequence.playable";
        private const string ClipPath = Folder + "/Motion.anim";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__mcp_timeline_tests__");
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(Folder);

        [Test]
        public void TimelineWrite_DryRunThenAtomicHashCheckedEdit()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timeline, TimelinePath);
            var track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
            var clip = track.CreateClip<AnimationPlayableAsset>();
            clip.start = 1;
            clip.duration = 2;
            AssetDatabase.SaveAssets();

            var read = Tool(new TimelineProvider(), "timeline.read").Handler(Ctx(new JObject { ["path"] = TimelinePath }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;
            Assert.That((string)read["profile"], Is.EqualTo("outline"));
            Assert.That(read["tracks"][0]["clips"][0]["asset"], Is.Null);
            var hash = (string)read["hash"];
            var clipId = (string)read["tracks"][0]["clips"][0]["clipId"];
            var args = new JObject
            {
                ["path"] = TimelinePath,
                ["expectedHash"] = hash,
                ["dryRun"] = true,
                ["operations"] = new JArray(new JObject { ["op"] = "set_clip", ["clipId"] = clipId, ["start"] = 3.0, ["duration"] = 4.0 }),
            };
            Tool(new TimelineProvider(), "timeline.write").Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.That(clip.start, Is.EqualTo(1));
            Assert.That(clip.duration, Is.EqualTo(2));

            args["dryRun"] = false;
            var written = Tool(new TimelineProvider(), "timeline.write").Handler(Ctx(args), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;
            Assert.That(clip.start, Is.EqualTo(3));
            Assert.That(clip.duration, Is.EqualTo(4));
            Assert.That((string)written["hash"], Is.Not.EqualTo(hash));

            var stale = Assert.ThrowsAsync<McpToolException>(async () =>
                await Tool(new TimelineProvider(), "timeline.write").Handler(Ctx(args), CancellationToken.None));
            Assert.That(stale.Code, Is.EqualTo(McpErrorCodes.Conflict));
        }

        [Test]
        public void TimelineRead_DefaultPagesAreBounded()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timeline, TimelinePath);
            var firstTrack = timeline.CreateTrack<AnimationTrack>(null, "Track 0");
            for (var i = 0; i < 30; i++)
                firstTrack.CreateClip<AnimationPlayableAsset>();
            for (var i = 1; i < 30; i++)
                timeline.CreateTrack<AnimationTrack>(null, "Track " + i);
            AssetDatabase.SaveAssets();

            var result = Tool(new TimelineProvider(), "timeline.read")
                .Handler(Ctx(new JObject { ["path"] = TimelinePath }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;

            Assert.That((int)result["trackCount"], Is.EqualTo(30));
            Assert.That((int)result["returnedTracks"], Is.EqualTo(25));
            Assert.That((bool)result["tracksTruncated"], Is.True);
            Assert.That((int)result["nextTrackOffset"], Is.EqualTo(25));
            Assert.That((int)result["tracks"][0]["clipCount"], Is.EqualTo(30));
            Assert.That((int)result["tracks"][0]["returnedClips"], Is.EqualTo(25));
            Assert.That((bool)result["tracks"][0]["clipsTruncated"], Is.True);
            Assert.That((int)result["tracks"][0]["nextClipOffset"], Is.EqualTo(25));
        }

        [Test]
        public void TimelineWrite_InvalidBatchDoesNotPartiallyApply()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timeline, TimelinePath);
            var track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
            var clip = track.CreateClip<AnimationPlayableAsset>();
            clip.start = 1;
            AssetDatabase.SaveAssets();
            var read = Tool(new TimelineProvider(), "timeline.read").Handler(Ctx(new JObject { ["path"] = TimelinePath }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;

            var args = new JObject
            {
                ["path"] = TimelinePath,
                ["expectedHash"] = (string)read["hash"],
                ["operations"] = new JArray(
                    new JObject { ["op"] = "set_clip", ["clipId"] = (string)read["tracks"][0]["clips"][0]["clipId"], ["start"] = 4.0 },
                    new JObject { ["op"] = "set_clip", ["clipId"] = "missing:0", ["start"] = 8.0 }),
            };
            Assert.ThrowsAsync<McpToolException>(async () =>
                await Tool(new TimelineProvider(), "timeline.write").Handler(Ctx(args), CancellationToken.None));
            Assert.That(clip.start, Is.EqualTo(1));
        }

        [Test]
        public void TimelineWrite_CreatesAnimationTrackThenStandaloneClip()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timeline, TimelinePath);
            var animation = new AnimationClip { name = "Motion" };
            AssetDatabase.CreateAsset(animation, ClipPath);
            AssetDatabase.SaveAssets();

            var write = Tool(new TimelineProvider(), "timeline.write");
            var read = Tool(new TimelineProvider(), "timeline.read");
            var initial = read.Handler(Ctx(new JObject { ["path"] = TimelinePath }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;
            var createTrack = new JObject
            {
                ["path"] = TimelinePath,
                ["expectedHash"] = (string)initial["hash"],
                ["operations"] = new JArray(new JObject
                {
                    ["op"] = "create_track",
                    ["trackType"] = "AnimationTrack",
                    ["name"] = "Motion",
                }),
            };
            var trackResult = write.Handler(Ctx(createTrack), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;
            var trackId = (string)trackResult["tracks"][0]["trackId"];

            var createClip = new JObject
            {
                ["path"] = TimelinePath,
                ["expectedHash"] = (string)trackResult["hash"],
                ["operations"] = new JArray(new JObject
                {
                    ["op"] = "create_clip",
                    ["trackId"] = trackId,
                    ["assetPath"] = ClipPath,
                    ["displayName"] = "Lift rise",
                    ["start"] = 3.0,
                    ["duration"] = 4.0,
                }),
            };
            var result = write.Handler(Ctx(createClip), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;

            Assert.That((string)result["tracks"][0]["clips"][0]["displayName"], Is.EqualTo("Lift rise"));
            Assert.That((double)result["tracks"][0]["clips"][0]["start"], Is.EqualTo(3));
            var playableAsset = timeline.GetRootTracks().Single().GetClips().Single().asset as AnimationPlayableAsset;
            Assert.That(playableAsset, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(playableAsset.clip), Is.EqualTo(ClipPath));
        }

        [Test]
        public void TimelineBindingWrite_DryRunsThenBindsCompatibleComponent()
        {
            var directorObject = new GameObject("__McpTimelineDirector__");
            var target = new GameObject("__McpTimelineTarget__");
            try
            {
                var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                AssetDatabase.CreateAsset(timeline, TimelinePath);
                var track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                AssetDatabase.SaveAssets();
                var director = directorObject.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                var animator = target.AddComponent<Animator>();
                var read = Tool(new TimelineProvider(), "timeline.read").Handler(
                        Ctx(new JObject { ["path"] = TimelinePath }), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                var args = new JObject
                {
                    ["timelinePath"] = TimelinePath,
                    ["trackId"] = (string)read["tracks"][0]["trackId"],
                    ["directorPath"] = "__McpTimelineDirector__",
                    ["targetPath"] = "__McpTimelineTarget__",
                    ["dryRun"] = true,
                };
                var tool = Tool(new TimelineBindingProvider(), "timeline.binding.write");
                var preview = tool.Handler(Ctx(args), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((bool)preview["changed"], Is.True);
                Assert.That(director.GetGenericBinding(track), Is.Null);

                args["dryRun"] = false;
                var result = tool.Handler(Ctx(args), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult().StructuredContent;
                Assert.That((string)result["bindingType"], Is.EqualTo(typeof(Animator).FullName));
                Assert.That(director.GetGenericBinding(track), Is.SameAs(animator));
            }
            finally
            {
                Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void CurveWrite_RoundTripsTangentsAndRejectsSubassets()
        {
            var clip = new AnimationClip { name = "Motion" };
            AssetDatabase.CreateAsset(clip, ClipPath);
            AssetDatabase.SaveAssets();
            var readTool = Tool(new AnimationClipCurveProvider(), "asset.animation_clip.curves.read");
            var writeTool = Tool(new AnimationClipCurveProvider(), "asset.animation_clip.curves.write");
            var read = readTool.Handler(Ctx(new JObject { ["path"] = ClipPath }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;
            var args = new JObject
            {
                ["path"] = ClipPath,
                ["expectedHash"] = (string)read["hash"],
                ["operations"] = new JArray(new JObject
                {
                    ["op"] = "set_curve",
                    ["binding"] = new JObject { ["path"] = "", ["componentType"] = typeof(Transform).FullName, ["propertyName"] = "m_LocalPosition.x" },
                    ["curve"] = new JObject
                    {
                        ["keys"] = new JArray(
                            new JObject { ["time"] = 0f, ["value"] = 0f, ["rightTangentMode"] = "Linear" },
                            new JObject { ["time"] = 1f, ["value"] = 2f, ["leftTangentMode"] = "Linear" }),
                    },
                }),
            };
            writeTool.Handler(Ctx(args), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var summary = readTool.Handler(Ctx(new JObject { ["path"] = ClipPath }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;
            Assert.That((string)summary["detail"], Is.EqualTo("bindings"));
            Assert.That(summary["curves"][0]["keys"], Is.Null);
            var roundTrip = readTool.Handler(Ctx(new JObject { ["path"] = ClipPath, ["detail"] = "keys" }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;
            Assert.That((int)roundTrip["curves"][0]["keyCount"], Is.EqualTo(2));
            Assert.That((string)roundTrip["curves"][0]["keys"][0]["rightTangentMode"], Is.EqualTo("Linear"));

            var container = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(container, TimelinePath);
            var subClip = new AnimationClip { name = "ImportedLike" };
            AssetDatabase.AddObjectToAsset(subClip, container);
            AssetDatabase.SaveAssets();
            var subRead = readTool.Handler(Ctx(new JObject { ["path"] = TimelinePath, ["clipName"] = "ImportedLike" }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;
            var rejected = new JObject
            {
                ["path"] = TimelinePath,
                ["clipName"] = "ImportedLike",
                ["expectedHash"] = (string)subRead["hash"],
                ["operations"] = args["operations"].DeepClone(),
            };
            Assert.ThrowsAsync<McpToolException>(async () => await writeTool.Handler(Ctx(rejected), CancellationToken.None));
        }

        [Test]
        public void CurveRead_DefaultKeyPageIsBounded()
        {
            var clip = new AnimationClip { name = "Motion" };
            AssetDatabase.CreateAsset(clip, ClipPath);
            var binding = EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x");
            var keys = Enumerable.Range(0, 30).Select(index => new Keyframe(index, index)).ToArray();
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(keys));
            AssetDatabase.SaveAssets();

            var result = Tool(new AnimationClipCurveProvider(), "asset.animation_clip.curves.read")
                .Handler(Ctx(new JObject { ["path"] = ClipPath, ["detail"] = "keys" }), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult().StructuredContent;

            Assert.That((int)result["curves"][0]["keyReturned"], Is.EqualTo(25));
            Assert.That((bool)result["curves"][0]["keysTruncated"], Is.True);
            Assert.That((int)result["curves"][0]["nextKeyOffset"], Is.EqualTo(25));
        }

        [Test]
        public void Providers_RegisterMinimalSuite()
        {
            var names = Collect(new TimelineProvider())
                .Concat(Collect(new TimelineBindingProvider()))
                .Concat(Collect(new AnimationClipCurveProvider())).Select(tool => tool.Name).ToArray();
            Assert.That(names, Is.EquivalentTo(new[]
            {
                "timeline.read", "timeline.write", "timeline.binding.write",
                "asset.animation_clip.curves.read", "asset.animation_clip.curves.write",
            }));
        }

        private static ToolDescriptor Tool(IToolProvider provider, string name) => Collect(provider).Single(tool => tool.Name == name);

        private static IReadOnlyList<ToolDescriptor> Collect(IToolProvider provider)
        {
            var sink = new Sink();
            provider.RegisterTools(sink);
            return sink.Tools;
        }

        private static ToolContext Ctx(JObject args) => new(args, new NoopProgressReporter(), new FakeMainThreadPump(),
            new FakeFrameWaiter(), null, new FakeLogSink(), "test");

        private sealed class Sink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();
            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }
    }
}
#endif
