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
    public sealed partial class ProfilerProvider : IToolProvider
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
            EditorApplication.playModeStateChanged += change => {
                if (change == PlayModeStateChange.EnteredEditMode && state?.Value<bool>("stopAfterExit") == true)
                    state["exitObserved"] = true;
            };
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
                Description = "Search cached CPU aggregates by capture, thread, marker and time interval. First analysis loads raw data into Profiler history (up to 512 MiB); subsequent marker/sort/page queries reuse a disk cache. Partial analysis returns a cursor: repeat the same capture/thread/time query with that cursor. Inclusive rows overlap; gaps are elapsed time, not CPU work. maxFrame/maxSample locate the slowest occurrence for profiler.sample. Coverage is the retained Unity history, which may be a suffix of the raw file.",
                InputSchema = JObject.Parse(@"{'type':'object','additionalProperties':false,'properties':{'captureId':{'type':'string','minLength':32,'maxLength':32},'thread':{'type':'string','maxLength':200},'marker':{'type':'string','maxLength':200},'fromMs':{'type':'number','minimum':0},'toMs':{'type':'number','minimum':0},'cursor':{'type':'string','maxLength':32},'budgetMs':{'type':'integer','minimum':1,'maximum':5000},'offset':{'type':'integer','minimum':0},'topN':{'type':'integer','minimum':1,'maximum':100},'sortBy':{'enum':['selfMs','inclusiveMs','maxMs','calls','gapMs']}}}"),
                Handler = QueryReport,
            });
            RegisterAnalysisTools(reg);
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
            if (state.Value<bool>("exitObserved")) Finish("playModeExit");
            else if (EditorApplication.timeSinceStartup - state.Value<double>("startedAt") >= state.Value<double>("durationSeconds")) Finish("duration");
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

    }

    internal sealed class ProfilerMarkerTotals
    {
        internal readonly string Marker;
        internal readonly ulong ThreadId;
        private readonly string thread;
        private int calls, intervals;
        private double self, total, max, previousStart, coveredEnd, cadence, gap, maxGap;
        private int maxFrame = -1, maxThreadIndex = -1, maxSample = -1;
        internal ProfilerMarkerTotals(string thread, ulong threadId, string marker) { this.thread = thread; ThreadId = threadId; Marker = marker; }
        internal void Add(double start, double duration, double selfMs, int frame = -1, int threadIndex = -1, int sample = -1)
        {
            if (calls == 0 || duration > max) { maxFrame = frame; maxThreadIndex = threadIndex; maxSample = sample; }
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
            ["maxFrame"] = maxFrame, ["maxThreadIndex"] = maxThreadIndex, ["maxSample"] = maxSample,
            ["intervals"] = intervals, ["meanCadenceMs"] = intervals == 0 ? JValue.CreateNull() : new JValue(cadence / intervals),
            ["gapMs"] = gap, ["maxGapMs"] = intervals == 0 ? JValue.CreateNull() : new JValue(maxGap) };
    }
}
