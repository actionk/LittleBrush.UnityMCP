using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine.Profiling;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [InitializeOnLoad, McpToolProvider]
    public sealed class ProfilerProvider : IToolProvider
    {
        private const string StateKey = "LittleBrushGames.Mcp.Profiler";
        private const long MaxFileBytes = 128L * 1024 * 1024;
        private static JObject state;
        private static double nextCheck;
        public string Namespace => "profiler";

        static ProfilerProvider()
        {
            var saved = SessionState.GetString(StateKey, "");
            if (saved.Length != 0) state = JObject.Parse(saved);
            if (state?.Value<bool>("active") == true) Finish("domainReload");
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += () => Finish("domainReload");
            EditorApplication.quitting += () => Finish("editorQuit");
        }

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor {
                Name = "profiler.start", TrustCategory = ToolTrustCategory.EditorState, ExclusiveGroup = "profiler",
                Description = "Start a local Editor CPU binary capture (default 10s, max 60s). Memory module and allocation call stacks are off by default; no snapshots. Refuses existing recordings and deep profiling. Returns immediately so you can exercise the Editor. Stops automatically on Editor update, size limit or reload and restores settings. Raw data stays in Library/McpProfiler.",
                InputSchema = JObject.Parse(@"{'type':'object','additionalProperties':false,'properties':{'durationSeconds':{'type':'number','minimum':1,'maximum':60},'includeMemory':{'type':'boolean'}}}"),
                Handler = Start,
            });
            reg.Register(new ToolDescriptor {
                Name = "profiler.stop", TrustCategory = ToolTrustCategory.EditorState, ExclusiveGroup = "profiler", ReloadSafe = true,
                Description = "Stop and flush the current MCP capture, restore profiler settings, and return its captureId/rawPath. Safe to repeat after automatic stop. Never stops a recording not owned by MCP.",
                InputSchema = JObject.Parse(@"{'type':'object','additionalProperties':false,'properties':{}}"),
                Handler = (ctx, ct) => { Finish("requested"); return new(ToolResult.Ok(Summary())); },
            });
            reg.Register(new ToolDescriptor {
                Name = "profiler.report", TrustCategory = ToolTrustCategory.EditorState, ExclusiveGroup = "profiler", Execution = ToolExecution.Async,
                Description = "Compact top-N CPU table for a stopped MCP capture. Appends raw frames to Unity's Profiler buffer (may evict old frames at its history limit); does not open a window. Filters are case-insensitive substrings; interval is sample start time in ms relative to first loaded frame, end exclusive. Separate start-to-start cadence and uncovered end-to-next-start gaps per marker/thread; gaps are not CPU work. Inclusive rows overlap. Work limits return explicit partial results; raw file remains authoritative.",
                InputSchema = JObject.Parse(@"{'type':'object','additionalProperties':false,'properties':{'captureId':{'type':'string','minLength':32,'maxLength':32},'thread':{'type':'string','maxLength':200},'marker':{'type':'string','maxLength':200},'fromMs':{'type':'number','minimum':0},'toMs':{'type':'number','minimum':0},'topN':{'type':'integer','minimum':1,'maximum':100},'sortBy':{'enum':['selfMs','inclusiveMs','maxMs','calls','gapMs']}}}"),
                Handler = Report,
            });
        }

        private static string DirectoryPath => Path.GetFullPath("Library/McpProfiler");
        private static void SaveState() => SessionState.SetString(StateKey, state.ToString(Newtonsoft.Json.Formatting.None));
        private static JObject Summary() => state == null ? new JObject { ["status"] = "idle" } : new JObject {
            ["captureId"] = state["captureId"], ["rawPath"] = state["rawPath"],
            ["status"] = state.Value<bool>("active") ? "recording" : "stopped",
            ["stopReason"] = state["stopReason"], ["durationSeconds"] = state["durationSeconds"],
            ["elapsedSeconds"] = state["elapsedSeconds"], ["includeMemory"] = state["includeMemory"],
            ["rawBytes"] = File.Exists((string)state["rawPath"]) ? new FileInfo((string)state["rawPath"]).Length : 0,
        };

        private static ValueTask<ToolResult> Start(ToolContext ctx, CancellationToken ct)
        {
            if (state?.Value<bool>("active") == true || Profiler.enabled || ProfilerDriver.enabled)
                throw new McpToolException(McpErrorCodes.InvalidParams, "A profiler capture is already active. Stop its owner before starting another.");
            if (ProfilerDriver.deepProfiling)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Disable Deep Profiling before a bounded CPU capture (changing it can reload scripts).");
            var duration = ctx.Arguments.Value<double?>("durationSeconds") ?? 10;
            if (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 1 || duration > 60)
                throw new McpToolException(McpErrorCodes.InvalidParams, "durationSeconds must be between 1 and 60.");
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, ".gitignore"), "*\n");
            string id = Guid.NewGuid().ToString("N");
            var areas = new JArray();
            foreach (ProfilerArea area in Enum.GetValues(typeof(ProfilerArea))) areas.Add(Profiler.GetAreaEnabled(area));
            state = new JObject { ["captureId"] = id, ["rawPath"] = Path.Combine(DirectoryPath, id + ".raw"),
                ["active"] = true, ["durationSeconds"] = duration, ["startedAt"] = EditorApplication.timeSinceStartup,
                ["includeMemory"] = ctx.Arguments.Value<bool?>("includeMemory") ?? false,
                ["previousEditor"] = ProfilerDriver.profileEditor, ["previousBinary"] = Profiler.enableBinaryLog,
                ["previousLog"] = Profiler.logFile, ["previousCallstacks"] = Profiler.enableAllocationCallstacks,
                ["previousMemory"] = Profiler.maxUsedMemory, ["previousAreas"] = areas };
            SaveState(); // Recovery information precedes the first global setting change.
            try
            {
                foreach (ProfilerArea area in Enum.GetValues(typeof(ProfilerArea)))
                    Profiler.SetAreaEnabled(area, area == ProfilerArea.CPU || (area == ProfilerArea.Memory && state.Value<bool>("includeMemory")));
                Profiler.enableAllocationCallstacks = false;
                Profiler.maxUsedMemory = (int)MaxFileBytes;
                ProfilerDriver.profileEditor = true;
                Profiler.logFile = (string)state["rawPath"];
                Profiler.enableBinaryLog = true;
                Profiler.enabled = true;
                nextCheck = 0;
                return new(ToolResult.Ok(Summary()));
            }
            catch { Finish("startFailed"); throw; }
        }

        private static void Tick()
        {
            if (state?.Value<bool>("active") != true || EditorApplication.timeSinceStartup < nextCheck) return;
            nextCheck = EditorApplication.timeSinceStartup + .25;
            if (EditorApplication.timeSinceStartup - state.Value<double>("startedAt") >= state.Value<double>("durationSeconds")) Finish("duration");
            else if (File.Exists((string)state["rawPath"]) && new FileInfo((string)state["rawPath"]).Length >= MaxFileBytes) Finish("sizeLimit");
            else if (!Profiler.enabled) Finish("recordingStopped");
        }

        private static void Finish(string reason)
        {
            if (state?.Value<bool>("active") != true) return;
            try { Profiler.enabled = false; }
            finally
            {
                Profiler.enableBinaryLog = false;
                Profiler.logFile = state.Value<string>("previousLog") ?? "";
                Profiler.enableBinaryLog = state.Value<bool>("previousBinary");
                ProfilerDriver.profileEditor = state.Value<bool>("previousEditor");
                Profiler.enableAllocationCallstacks = state.Value<bool>("previousCallstacks");
                Profiler.maxUsedMemory = state.Value<int>("previousMemory");
                int index = 0;
                foreach (ProfilerArea area in Enum.GetValues(typeof(ProfilerArea))) Profiler.SetAreaEnabled(area, (bool)state["previousAreas"][index++]);
                state["active"] = false;
                state["stopReason"] = reason;
                state["elapsedSeconds"] = EditorApplication.timeSinceStartup - state.Value<double>("startedAt");
                SaveState();
                File.WriteAllText(Path.ChangeExtension((string)state["rawPath"], ".json"), Summary().ToString());
            }
        }

        private static bool Matches(string value, string filter) => string.IsNullOrEmpty(filter) || (value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

        private static async ValueTask<ToolResult> Report(ToolContext ctx, CancellationToken ct)
        {
            if (state?.Value<bool>("active") == true || Profiler.enabled || ProfilerDriver.enabled)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Stop recording before reporting.");
            string id = ctx.Arguments.Value<string>("captureId") ?? state?.Value<string>("captureId");
            if (id == null || id.Length != 32 || id.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new McpToolException(McpErrorCodes.InvalidParams, "Supply the captureId returned by profiler.start.");
            string path = Path.Combine(DirectoryPath, id + ".raw");
            if (!File.Exists(path)) throw new McpToolException(McpErrorCodes.NotFound, "Capture not found: " + path);
            if (new FileInfo(path).Length > MaxFileBytes)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Capture exceeds the 128 MiB report load limit. Open the raw file in Unity's Profiler or record a shorter capture.");
            double from = ctx.Arguments.Value<double?>("fromMs") ?? 0, to = ctx.Arguments.Value<double?>("toMs") ?? double.MaxValue;
            int top = ctx.Arguments.Value<int?>("topN") ?? 20;
            if (from < 0 || to <= from || double.IsNaN(from) || double.IsNaN(to) || double.IsInfinity(from) || double.IsInfinity(to) || top < 1 || top > 100)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Use a finite interval 0 <= fromMs < toMs and topN between 1 and 100.");
            ct.ThrowIfCancellationRequested();
            int first = ProfilerDriver.lastFrameIndex + 1;
            var load = Stopwatch.StartNew();
            if (!ProfilerDriver.LoadProfile(path, true)) throw new McpToolException(McpErrorCodes.InvalidParams, "Unity could not load this capture.");
            load.Stop();
            int last = ProfilerDriver.lastFrameIndex;
            first = Math.Max(first, ProfilerDriver.firstFrameIndex);
            var totals = new Dictionary<(ulong thread, string marker), ProfilerMarkerTotals>();
            var watch = Stopwatch.StartNew();
            var slice = Stopwatch.StartNew();
            long samples = 0;
            int views = 0;
            string partial = null;
            double origin = double.NaN;
            double maxSliceMs = 0;
            for (int frame = first; frame <= last && partial == null; frame++)
            {
                for (int thread = 0; thread < 1024; thread++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (watch.ElapsedMilliseconds > 5000) { partial = "Analysis time limit reached; narrow filters or capture duration."; break; }
                    int nextSample = 0;
                    bool finished = false, valid = true, counted = false;
                    while (!finished && partial == null)
                    {
                        using (var data = ProfilerDriver.GetRawFrameDataView(frame, thread))
                        {
                            if (!data.valid) { valid = false; break; }
                            if (double.IsNaN(origin)) origin = data.frameStartTimeMs;
                            if (!Matches(data.threadName, ctx.Arguments.Value<string>("thread"))) break;
                            if (data.frameStartTimeMs - origin >= to || data.frameStartTimeMs - origin + data.frameTimeMs < from) break;
                            if (!counted) { views++; counted = true; }
                            for (int sample = nextSample; sample < data.sampleCount; sample++)
                            {
                                if ((sample & 255) == 0 && slice.ElapsedMilliseconds >= 8)
                                { nextSample = sample; break; }
                                nextSample = sample + 1;
                                if (++samples > 2000000 || totals.Count >= 20000 || watch.ElapsedMilliseconds > 5000)
                                { partial = "Analysis limit reached; narrow thread/time filters or capture duration."; break; }
                                if ((sample & 1023) == 0) ct.ThrowIfCancellationRequested();
                                double start = data.GetSampleStartTimeMs(sample) - origin;
                                if (start < from || start >= to) continue;
                                string marker = data.GetSampleName(sample) ?? "(unnamed)";
                                if (!Matches(marker, ctx.Arguments.Value<string>("marker"))) continue;
                                double duration = data.GetSampleTimeMs(sample), children = 0;
                                int child = sample + 1;
                                for (int n = 0; n < data.GetSampleChildrenCount(sample); n++)
                                { children += data.GetSampleTimeMs(child); child += data.GetSampleChildrenCountRecursive(child) + 1; }
                                var key = (data.threadId, marker);
                                if (!totals.TryGetValue(key, out var value))
                                    totals.Add(key, value = new ProfilerMarkerTotals(data.threadName, data.threadId, marker));
                                value.Add(start, duration, Math.Max(0, duration - children));
                            }
                            finished = nextSample >= data.sampleCount;
                        }
                        if (slice.ElapsedMilliseconds >= 8 && partial == null)
                        { maxSliceMs = Math.Max(maxSliceMs, slice.Elapsed.TotalMilliseconds); await ctx.Frames.WaitNextFrameAsync(ct); slice.Restart(); }
                    }
                    if (!valid) break;
                    if (partial != null) break;
                    if (thread == 1023) partial = "Thread limit reached (1024).";
                }
                // Dispose native frame views before yielding; the writer lease covers the whole report.
                if (frame < last && partial == null && slice.ElapsedMilliseconds >= 8)
                { maxSliceMs = Math.Max(maxSliceMs, slice.Elapsed.TotalMilliseconds); await ctx.Frames.WaitNextFrameAsync(ct); slice.Restart(); }
            }
            maxSliceMs = Math.Max(maxSliceMs, slice.Elapsed.TotalMilliseconds);
            string sort = ctx.Arguments.Value<string>("sortBy") ?? "selfMs";
            var rows = new JArray(totals.Values.OrderByDescending(v => v.SortValue(sort)).ThenBy(v => v.Marker, StringComparer.Ordinal).ThenBy(v => v.ThreadId).Take(top).Select(v => v.Row()));
            return ToolResult.Ok(new JObject { ["captureId"] = id, ["rawPath"] = path,
                ["firstFrame"] = first, ["lastFrame"] = last, ["threadFramesRead"] = views, ["samplesScanned"] = samples,
                ["analysisMs"] = watch.Elapsed.TotalMilliseconds, ["partial"] = partial != null, ["partialReason"] = partial,
                ["loadMs"] = load.Elapsed.TotalMilliseconds, ["maxAnalysisSliceMs"] = maxSliceMs,
                ["matchedRows"] = totals.Count, ["rows"] = rows,
                ["note"] = "Times in ms and include idle/wait markers and unnamed thread spans. Inclusive rows overlap; self excludes direct children. Cadence is start-to-start; gap is uncovered end-to-next-start on the same marker/thread, not CPU work. Only calls starting inside the interval count. Unity history limits may retain only a suffix of the raw file." });
        }
    }

    internal sealed class ProfilerMarkerTotals
    {
        internal readonly string Marker;
        internal readonly ulong ThreadId;
        private readonly string thread;
        private int calls, intervals;
        private double self, total, max, previousStart, coveredEnd, cadence, gap, maxGap;
        internal ProfilerMarkerTotals(string thread, ulong threadId, string marker) { this.thread = thread; ThreadId = threadId; Marker = marker; }
        internal void Add(double start, double duration, double selfMs)
        {
            if (calls > 0 && start >= previousStart)
            {
                intervals++; cadence += start - previousStart;
                double uncovered = Math.Max(0, start - coveredEnd);
                gap += uncovered; maxGap = Math.Max(maxGap, uncovered);
            }
            coveredEnd = calls == 0 ? start + duration : Math.Max(coveredEnd, start + duration);
            previousStart = start; calls++; total += duration; self += selfMs; max = Math.Max(max, duration);
        }
        internal double SortValue(string sort) => sort switch { "inclusiveMs" => total, "maxMs" => max, "calls" => calls, "gapMs" => gap, _ => self };
        internal JObject Row() => new() { ["thread"] = thread, ["threadId"] = ThreadId.ToString(), ["marker"] = Marker,
            ["calls"] = calls, ["selfMs"] = self, ["inclusiveMs"] = total, ["maxMs"] = max,
            ["intervals"] = intervals, ["meanCadenceMs"] = intervals == 0 ? JValue.CreateNull() : new JValue(cadence / intervals),
            ["gapMs"] = gap, ["maxGapMs"] = intervals == 0 ? JValue.CreateNull() : new JValue(maxGap) };
    }
}
