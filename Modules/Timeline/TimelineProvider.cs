#if HAS_UNITY_TIMELINE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp;
using LittleBrushGames.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace LittleBrushGames.Mcp.Modules.Timeline
{
    [McpToolProvider]
    public sealed class TimelineProvider : IToolProvider
    {
        private const int MaxOperations = 256;

        public string Namespace => "timeline";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "timeline.read",
                Description = "Read a Timeline asset as paged normalized tracks and clips with asset-local IDs and an optimistic hash. Track and per-track clip pages default to 25.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path""], ""additionalProperties"": false,
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""profile"": { ""type"": ""string"", ""enum"": [""outline"", ""full""] },
                        ""trackOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""trackLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100 },
                        ""clipOffset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""clipLimit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200 }
                    }
                }"),
                Handler = Read,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "timeline.write",
                Description = "Atomically create Animation tracks/clips or edit Timeline clip timing and duration settings. Requires expectedHash and supports dryRun. Create a track and read its trackId before creating a clip.",
                Availability = ToolAvailability.EditMode,
                ExclusiveGroup = "timeline-write",
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""path"", ""expectedHash"", ""operations""],
                    ""properties"": {
                        ""path"": { ""type"": ""string"" },
                        ""expectedHash"": { ""type"": ""string"" },
                        ""dryRun"": { ""type"": ""boolean"" },
                        ""operations"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 256, ""items"": { ""type"": ""object"" } }
                    }
                }"),
                Handler = Write,
            });
        }

        private static ValueTask<ToolResult> Read(ToolContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = NormalizeAssetPath((string)ctx.Arguments["path"]);
            return new ValueTask<ToolResult>(ToolResult.Ok(ReadDocument(LoadTimeline(path), path, ctx.Arguments)));
        }

        private static ValueTask<ToolResult> Write(ToolContext ctx, CancellationToken ct)
        {
            var path = NormalizeAssetPath((string)ctx.Arguments["path"]);
            var timeline = LoadTimeline(path);
            RequireHash(path, (string)ctx.Arguments["expectedHash"]);
            var operations = ctx.Arguments["operations"] as JArray ?? throw Validation("operations must be an array.");
            if (operations.Count is < 1 or > MaxOperations)
                throw Validation($"operations must contain 1 to {MaxOperations} items.");

            var tracks = Flatten(timeline).ToArray();
            var clips = tracks.SelectMany(track => track.GetClips().Select((clip, index) =>
                (id: ClipId(track, index), track, clip))).ToDictionary(item => item.id, StringComparer.Ordinal);
            var changes = new JArray();
            var actions = new List<Action>();
            var targets = new HashSet<string>(StringComparer.Ordinal);
            var timelineChanged = false;
            var trackNames = new HashSet<string>(tracks.Select(track => track.name), StringComparer.Ordinal);

            foreach (var token in operations)
            {
                ct.ThrowIfCancellationRequested();
                var op = token as JObject ?? throw Validation("Each operation must be an object.");
                var kind = RequiredString(op, "op");
                if (kind == "set_clip")
                {
                    var id = RequiredString(op, "clipId");
                    if (!clips.TryGetValue(id, out var target))
                        throw new McpToolException(McpErrorCodes.NotFound, $"Timeline clip not found: '{id}'.");
                    if (!targets.Add("clip:" + id)) throw Validation($"Clip '{id}' may only be edited once per request.");
                    ValidateClipEdit(target.clip, op);
                    changes.Add(new JObject { ["op"] = kind, ["clipId"] = id });
                    actions.Add(() => ApplyClipEdit(target.clip, op));
                }
                else if (kind == "set_timeline")
                {
                    if (timelineChanged) throw Validation("set_timeline may only appear once per request.");
                    timelineChanged = true;
                    ValidateTimelineEdit(op);
                    changes.Add(new JObject { ["op"] = kind });
                    actions.Add(() => ApplyTimelineEdit(timeline, op));
                }
                else if (kind == "create_track")
                {
                    var trackType = RequiredString(op, "trackType");
                    if (!string.Equals(trackType, nameof(AnimationTrack), StringComparison.Ordinal))
                        throw Validation("create_track currently supports trackType 'AnimationTrack'.");
                    var name = RequiredString(op, "name");
                    if (!trackNames.Add(name))
                        throw Validation($"Timeline track name must be unique: '{name}'.");
                    changes.Add(new JObject { ["op"] = kind, ["trackType"] = trackType, ["name"] = name });
                    actions.Add(() =>
                    {
                        var created = timeline.CreateTrack<AnimationTrack>(null, name);
                        Undo.RegisterCreatedObjectUndo(created, "MCP Timeline Write");
                    });
                }
                else if (kind == "create_clip")
                {
                    var trackId = RequiredString(op, "trackId");
                    var track = tracks.FirstOrDefault(candidate =>
                        LocalId(candidate).ToString(CultureInfo.InvariantCulture) == trackId);
                    if (track == null)
                        throw new McpToolException(McpErrorCodes.NotFound, $"Timeline track not found: '{trackId}'.");
                    if (track is not AnimationTrack animationTrack)
                        throw Validation("create_clip currently supports AnimationTrack targets.");
                    var assetPath = NormalizeAnimationClipPath(RequiredString(op, "assetPath"));
                    var animationClip = AssetDatabase.LoadMainAssetAtPath(assetPath) as AnimationClip
                        ?? throw new McpToolException(McpErrorCodes.NotFound, $"Standalone AnimationClip not found: '{assetPath}'.");
                    if (!targets.Add("create_clip:" + trackId))
                        throw Validation($"Track '{trackId}' may receive only one new clip per request.");
                    ValidateCreatedClip(op, animationClip);
                    changes.Add(new JObject
                    {
                        ["op"] = kind,
                        ["trackId"] = trackId,
                        ["assetPath"] = assetPath,
                    });
                    actions.Add(() => ApplyCreateClip(animationTrack, animationClip, op));
                }
                else throw Validation($"Unsupported timeline operation: '{kind}'.");
            }

            if (ctx.Arguments["dryRun"]?.Value<bool>() == true)
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["dryRun"] = true,
                    ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                    ["changes"] = changes,
                }));

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("MCP Timeline Write");
            Undo.RegisterCompleteObjectUndo(new Object[] { timeline }.Concat(tracks).ToArray(), "MCP Timeline Write");
            try
            {
                foreach (var action in actions) action();
                EditorUtility.SetDirty(timeline);
                foreach (var track in Flatten(timeline)) EditorUtility.SetDirty(track);
                AssetDatabase.SaveAssetIfDirty(timeline);
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
            Undo.CollapseUndoOperations(undoGroup);

            var refreshed = RefreshDirectors(timeline);
            var document = ReadDocument(timeline, path, new JObject());
            document["dryRun"] = false;
            document["changes"] = changes;
            document["refreshedDirectorCount"] = refreshed;
            return new ValueTask<ToolResult>(ToolResult.Ok(document));
        }

        private static JObject ReadDocument(TimelineAsset timeline, string path, JObject args)
        {
            var profile = (string)args["profile"] ?? "outline";
            if (profile is not ("outline" or "full")) throw Validation("profile must be outline or full.");
            var trackOffset = args["trackOffset"]?.Value<int>() ?? 0;
            var trackLimit = args["trackLimit"]?.Value<int>() ?? 25;
            var clipOffset = args["clipOffset"]?.Value<int>() ?? 0;
            var clipLimit = args["clipLimit"]?.Value<int>() ?? 25;
            var allTracks = Flatten(timeline).ToArray();
            var tracks = new JArray();
            foreach (var track in allTracks.Skip(trackOffset).Take(trackLimit))
            {
                var allClips = track.GetClips().ToArray();
                var clips = new JArray();
                var clipEnd = (int)Math.Min(allClips.Length, (long)clipOffset + clipLimit);
                for (var index = clipOffset; index < clipEnd; index++)
                {
                    var clip = allClips[index];
                    var clipJson = new JObject
                    {
                        ["clipId"] = ClipId(track, index),
                        ["index"] = index,
                        ["displayName"] = clip.displayName,
                        ["start"] = clip.start,
                        ["duration"] = clip.duration,
                    };
                    if (profile == "full")
                    {
                        clipJson["end"] = clip.end;
                        clipJson["clipIn"] = clip.clipIn;
                        clipJson["timeScale"] = clip.timeScale;
                        clipJson["easeInDuration"] = clip.easeInDuration;
                        clipJson["easeOutDuration"] = clip.easeOutDuration;
                        clipJson["blendInDuration"] = clip.blendInDuration;
                        clipJson["blendOutDuration"] = clip.blendOutDuration;
                        clipJson["preExtrapolationMode"] = clip.preExtrapolationMode.ToString();
                        clipJson["postExtrapolationMode"] = clip.postExtrapolationMode.ToString();
                        clipJson["capabilities"] = Capabilities(clip.clipCaps);
                        clipJson["asset"] = DescribeObject(clip.asset);
                    }
                    clips.Add(clipJson);
                }

                var trackJson = new JObject
                {
                    ["trackId"] = LocalId(track).ToString(CultureInfo.InvariantCulture),
                    ["name"] = track.name,
                    ["type"] = track.GetType().FullName,
                    ["clips"] = clips,
                };
                if (clipOffset > 0 || clipEnd < allClips.Length)
                {
                    trackJson["clipCount"] = allClips.Length;
                    trackJson["clipOffset"] = clipOffset;
                    trackJson["returnedClips"] = clips.Count;
                    trackJson["clipsTruncated"] = clipEnd < allClips.Length;
                    if (clipEnd < allClips.Length) trackJson["nextClipOffset"] = clipEnd;
                }
                if (track.parent is TrackAsset parent)
                    trackJson["parentTrackId"] = LocalId(parent).ToString(CultureInfo.InvariantCulture);
                if (track.muted) trackJson["muted"] = true;
                if (track.locked) trackJson["locked"] = true;
                tracks.Add(trackJson);
            }

            var result = new JObject
            {
                ["schemaVersion"] = 1,
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["hash"] = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                ["duration"] = timeline.duration,
                ["durationMode"] = timeline.durationMode.ToString(),
                ["fixedDuration"] = timeline.fixedDuration,
                ["frameRate"] = timeline.editorSettings.frameRate,
                ["profile"] = profile,
                ["trackCount"] = allTracks.Length,
                ["trackOffset"] = trackOffset,
                ["returnedTracks"] = tracks.Count,
                ["tracksTruncated"] = (long)trackOffset + tracks.Count < allTracks.Length,
                ["tracks"] = tracks,
            };
            if (trackOffset + tracks.Count < allTracks.Length)
                result["nextTrackOffset"] = trackOffset + tracks.Count;
            return result;
        }

        private static IEnumerable<TrackAsset> Flatten(TimelineAsset timeline)
        {
            foreach (var root in timeline.GetRootTracks())
            {
                yield return root;
                foreach (var child in Flatten(root)) yield return child;
            }
        }

        private static IEnumerable<TrackAsset> Flatten(TrackAsset track)
        {
            foreach (var child in track.GetChildTracks())
            {
                yield return child;
                foreach (var descendant in Flatten(child)) yield return descendant;
            }
        }

        private static void ValidateClipEdit(TimelineClip clip, JObject op)
        {
            var duration = OptionalFinite(op, "duration", clip.duration, value => value > 0, "duration must be > 0.");
            OptionalFinite(op, "start", clip.start, value => value >= 0, "start must be >= 0.");
            OptionalFinite(op, "clipIn", clip.clipIn, value => value >= 0, "clipIn must be >= 0.", clip, ClipCaps.ClipIn);
            OptionalFinite(op, "timeScale", clip.timeScale, value => value > 0, "timeScale must be > 0.", clip, ClipCaps.SpeedMultiplier);
            var easeIn = OptionalFinite(op, "easeInDuration", clip.easeInDuration, value => value >= 0, "easeInDuration must be >= 0.");
            var easeOut = OptionalFinite(op, "easeOutDuration", clip.easeOutDuration, value => value >= 0, "easeOutDuration must be >= 0.");
            var blendIn = OptionalFinite(op, "blendInDuration", clip.blendInDuration, value => value >= 0, "blendInDuration must be >= 0.", clip, ClipCaps.Blending);
            var blendOut = OptionalFinite(op, "blendOutDuration", clip.blendOutDuration, value => value >= 0, "blendOutDuration must be >= 0.", clip, ClipCaps.Blending);
            if (easeIn + easeOut > duration) throw Validation("ease durations cannot exceed clip duration.");
            if (blendIn + blendOut > duration) throw Validation("blend durations cannot exceed clip duration.");
            if (op["displayName"] != null && op["displayName"].Type != JTokenType.String)
                throw Validation("displayName must be a string.");
            if (op["preExtrapolationMode"] != null || op["postExtrapolationMode"] != null)
                throw Validation("Timeline extrapolation modes are read-only in this installed Timeline API.");
        }

        private static void ApplyClipEdit(TimelineClip clip, JObject op)
        {
            if (op["displayName"] != null) clip.displayName = (string)op["displayName"];
            if (op["start"] != null) clip.start = op["start"].Value<double>();
            if (op["duration"] != null) clip.duration = op["duration"].Value<double>();
            if (op["clipIn"] != null) clip.clipIn = op["clipIn"].Value<double>();
            if (op["timeScale"] != null) clip.timeScale = op["timeScale"].Value<double>();
            if (op["easeInDuration"] != null) clip.easeInDuration = op["easeInDuration"].Value<double>();
            if (op["easeOutDuration"] != null) clip.easeOutDuration = op["easeOutDuration"].Value<double>();
            if (op["blendInDuration"] != null) clip.blendInDuration = op["blendInDuration"].Value<double>();
            if (op["blendOutDuration"] != null) clip.blendOutDuration = op["blendOutDuration"].Value<double>();
        }

        private static void ValidateTimelineEdit(JObject op)
        {
            if (op["durationMode"] != null) ParseEnum<TimelineAsset.DurationMode>(op, "durationMode");
            OptionalFinite(op, "fixedDuration", 1, value => value > 0, "fixedDuration must be > 0.");
            OptionalFinite(op, "frameRate", 60, value => value is >= 1 and <= 240, "frameRate must be between 1 and 240.");
        }

        private static void ApplyTimelineEdit(TimelineAsset timeline, JObject op)
        {
            if (op["durationMode"] != null) timeline.durationMode = ParseEnum<TimelineAsset.DurationMode>(op, "durationMode");
            if (op["fixedDuration"] != null) timeline.fixedDuration = op["fixedDuration"].Value<double>();
            if (op["frameRate"] != null) timeline.editorSettings.frameRate = op["frameRate"].Value<double>();
        }

        private static void ValidateCreatedClip(JObject op, AnimationClip animationClip)
        {
            OptionalFinite(op, "start", 0, value => value >= 0, "start must be >= 0.");
            OptionalFinite(op, "duration", animationClip.length, value => value > 0, "duration must be > 0.");
            OptionalFinite(op, "clipIn", 0, value => value >= 0, "clipIn must be >= 0.");
            OptionalFinite(op, "timeScale", 1, value => value > 0, "timeScale must be > 0.");
            if (op["displayName"] != null && op["displayName"].Type != JTokenType.String)
                throw Validation("displayName must be a string.");
        }

        private static void ApplyCreateClip(AnimationTrack track, AnimationClip animationClip, JObject op)
        {
            var clip = track.CreateClip<AnimationPlayableAsset>();
            if (clip.asset is AnimationPlayableAsset playableAsset)
            {
                Undo.RegisterCreatedObjectUndo(playableAsset, "MCP Timeline Write");
                playableAsset.clip = animationClip;
                EditorUtility.SetDirty(playableAsset);
            }
            if (op["displayName"] != null) clip.displayName = (string)op["displayName"];
            clip.start = op["start"]?.Value<double>() ?? 0;
            clip.duration = op["duration"]?.Value<double>() ?? animationClip.length;
            clip.clipIn = op["clipIn"]?.Value<double>() ?? 0;
            clip.timeScale = op["timeScale"]?.Value<double>() ?? 1;
        }

        private static double OptionalFinite(JObject op, string name, double fallback, Func<double, bool> predicate,
            string message, TimelineClip clip = null, ClipCaps capability = ClipCaps.None)
        {
            if (op[name] == null) return fallback;
            if (clip != null) RequireCapability(clip, capability, name);
            double value;
            try { value = op[name].Value<double>(); }
            catch { throw Validation($"{name} must be a number."); }
            if (double.IsNaN(value) || double.IsInfinity(value) || !predicate(value)) throw Validation(message);
            return value;
        }

        private static void RequireCapability(TimelineClip clip, ClipCaps capability, string field)
        {
            if ((clip.clipCaps & capability) == 0)
                throw Validation($"Clip does not support '{field}' ({capability}).");
        }

        private static T ParseEnum<T>(JObject op, string name) where T : struct
        {
            if (op[name]?.Type != JTokenType.String || !Enum.TryParse((string)op[name], true, out T value) || !Enum.IsDefined(typeof(T), value))
                throw Validation($"Invalid {name}: '{op[name]}'.");
            return value;
        }

        private static JArray Capabilities(ClipCaps caps)
        {
            var values = new JArray();
            foreach (var value in new[] { ClipCaps.Looping, ClipCaps.Extrapolation, ClipCaps.ClipIn, ClipCaps.SpeedMultiplier, ClipCaps.Blending })
                if ((caps & value) != 0) values.Add(value.ToString());
            return values;
        }

        private static int RefreshDirectors(TimelineAsset timeline)
        {
            var count = 0;
            foreach (var director in Resources.FindObjectsOfTypeAll<PlayableDirector>())
            {
                if (director.playableAsset != timeline || !director.gameObject.scene.IsValid()) continue;
                director.RebuildGraph();
                director.Evaluate();
                count++;
            }
            return count;
        }

        private static TimelineAsset LoadTimeline(string path)
            => AssetDatabase.LoadAssetAtPath<TimelineAsset>(path)
               ?? throw new McpToolException(McpErrorCodes.NotFound, $"Timeline asset not found: '{path}'.");

        internal static string NormalizeAssetPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || System.IO.Path.IsPathRooted(raw)) throw Validation("path must be project-relative.");
            var path = raw.Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                throw Validation("path must be a .playable asset under Assets/.");
            return path;
        }

        private static string NormalizeAnimationClipPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || System.IO.Path.IsPathRooted(raw))
                throw Validation("assetPath must be project-relative.");
            var path = raw.Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) ||
                !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw Validation("assetPath must be a standalone .anim asset under Assets/.");
            return path;
        }

        internal static void RequireHash(string path, string expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash)) throw Validation("expectedHash is required.");
            var actual = AssetDatabase.GetAssetDependencyHash(path).ToString();
            if (!string.Equals(expectedHash, actual, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict, "Asset changed since it was read.", new JObject { ["expectedHash"] = expectedHash, ["actualHash"] = actual });
        }

        internal static long LocalId(Object value)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out _, out long localId);
            return localId;
        }

        private static string ClipId(TrackAsset track, int index)
            => LocalId(track).ToString(CultureInfo.InvariantCulture) + ":" + index.ToString(CultureInfo.InvariantCulture);

        internal static JObject DescribeObject(Object value)
        {
            if (value == null) return null;
            var path = AssetDatabase.GetAssetPath(value);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out var guid, out long localId);
            return new JObject
            {
                ["guid"] = guid,
                ["localId"] = localId.ToString(CultureInfo.InvariantCulture),
                ["path"] = path,
                ["name"] = value.name,
                ["type"] = value.GetType().FullName,
            };
        }

        private static string RequiredString(JObject obj, string name)
            => obj[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)obj[name])
                ? ((string)obj[name]).Trim()
                : throw Validation($"{name} is required.");

        internal static McpToolException Validation(string message)
            => new(McpErrorCodes.ValidationFailed, message);
    }
}
#endif
