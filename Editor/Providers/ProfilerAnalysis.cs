using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine.Profiling;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    public sealed partial class ProfilerProvider
    {
        internal const long MaxLoadBytes = 512L * 1024 * 1024;
        private static LoadedCapture loaded;
        private static Analysis pendingAnalysis;

        private sealed class LoadedCapture
        {
            internal string Id, Stamp, Path;
            internal int First, Last, LastSamples;
            internal double Origin, LastStart, LoadMs;
        }

        // One resumable scan in memory; completed aggregates survive reloads on disk.
        private sealed class Analysis
        {
            internal string Key, Cursor = Guid.NewGuid().ToString("N");
            internal LoadedCapture Capture;
            internal int Frame, Thread, Sample;
            internal long Samples;
            internal double ElapsedMs;
            internal readonly Dictionary<(ulong, string), ProfilerMarkerTotals> Totals = new();
        }

        private static void RegisterAnalysisTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor {
                Name = "profiler.frames", TrustCategory = ToolTrustCategory.EditorState, ExclusiveGroup = "profiler",
                Description = "Find slow main-thread frames in a saved capture (512 MiB load limit). Returns capture-relative frame indexes for profiler.sample, sorted by duration. Reuses loaded data when valid. Native loading may evict earlier Profiler history.",
                InputSchema = JObject.Parse(@"{'type':'object','additionalProperties':false,'properties':{'captureId':{'type':'string','minLength':32,'maxLength':32},'offset':{'type':'integer','minimum':0},'topN':{'type':'integer','minimum':1,'maximum':100}}}"),
                Handler = Frames,
            });
            reg.Register(new ToolDescriptor {
                Name = "profiler.sample", TrustCategory = ToolTrustCategory.EditorState, ExclusiveGroup = "profiler",
                Description = "Inspect one CPU sample and page its direct children, sorted by duration. Use maxFrame/maxThreadIndex/maxSample from profiler.report, or frame from profiler.frames with threadIndex=0 and sample=0. Self time excludes direct children. Frame indexes are relative to retained capture history.",
                InputSchema = JObject.Parse(@"{'type':'object','additionalProperties':false,'required':['captureId','frame'],'properties':{'captureId':{'type':'string','minLength':32,'maxLength':32},'frame':{'type':'integer','minimum':0},'threadIndex':{'type':'integer','minimum':0,'maximum':1023},'sample':{'type':'integer','minimum':0},'offset':{'type':'integer','minimum':0},'topN':{'type':'integer','minimum':1,'maximum':100}}}"),
                Handler = Sample,
            });
        }

        internal static JObject StartExitCapture(ToolContext ctx)
        {
            Start(ctx, CancellationToken.None).GetAwaiter().GetResult();
            state["stopAfterExit"] = true;
            SaveState();
            return Summary();
        }

        private static (string id, string path, string stamp) CaptureFile(JObject args)
        {
            if (state?.Value<bool>("active") == true || Profiler.enabled || ProfilerDriver.enabled)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Stop recording before reporting.");
            string id = args.Value<string>("captureId") ?? state?.Value<string>("captureId");
            if (id == null || id.Length != 32 || id.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new McpToolException(McpErrorCodes.InvalidParams, "Supply the captureId returned by profiler.start.");
            string path = Path.Combine(DirectoryPath, id + ".raw");
            var file = new FileInfo(path);
            if (!file.Exists) throw new McpToolException(McpErrorCodes.NotFound, "Capture not found: " + path);
            ValidateLoadSize(file.Length);
            return (id, path, file.Length + ":" + file.LastWriteTimeUtc.Ticks);
        }

        internal static void ValidateLoadSize(long bytes)
        {
            if (bytes > MaxLoadBytes)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "Capture exceeds the 512 MiB native load budget. Raw evidence is preserved; inspect it in Unity's Profiler. Recording's 128 MiB threshold is cooperative and may overshoot during stalls.");
        }

        private static bool IsLoaded(LoadedCapture capture)
        {
            if (capture == null || ProfilerDriver.firstFrameIndex > capture.First || ProfilerDriver.lastFrameIndex != capture.Last) return false;
            using var last = ProfilerDriver.GetRawFrameDataView(capture.Last, 0);
            return last.valid && last.frameStartTimeMs == capture.LastStart && last.sampleCount == capture.LastSamples;
        }

        private static LoadedCapture Load((string id, string path, string stamp) file)
        {
            if (loaded?.Id == file.id && loaded.Stamp == file.stamp && IsLoaded(loaded)) return loaded;
            int first = ProfilerDriver.lastFrameIndex + 1;
            var timer = Stopwatch.StartNew();
            if (!ProfilerDriver.LoadProfile(file.path, true))
                throw new McpToolException(McpErrorCodes.InvalidParams, "Unity could not load this capture.");
            int last = ProfilerDriver.lastFrameIndex;
            first = Math.Max(first, ProfilerDriver.firstFrameIndex);
            using var firstData = ProfilerDriver.GetRawFrameDataView(first, 0);
            using var lastData = ProfilerDriver.GetRawFrameDataView(last, 0);
            if (!firstData.valid || !lastData.valid)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Capture contains no retained CPU frames.");
            loaded = new LoadedCapture { Id = file.id, Path = file.path, Stamp = file.stamp,
                First = first, Last = last, Origin = firstData.frameStartTimeMs,
                LastStart = lastData.frameStartTimeMs, LastSamples = lastData.sampleCount, LoadMs = timer.Elapsed.TotalMilliseconds };
            return loaded;
        }

        private static string CacheKey(string stamp, string thread, double from, double to)
        {
            using var hash = SHA256.Create();
            var scope = new JArray(2, stamp, thread.ToLowerInvariant(), from, to).ToString(Newtonsoft.Json.Formatting.None);
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(scope))).Replace("-", "").ToLowerInvariant();
        }

        private static async ValueTask<ToolResult> QueryReport(ToolContext ctx, CancellationToken ct)
        {
            var args = ctx.Arguments;
            var file = CaptureFile(args);
            double from = args.Value<double?>("fromMs") ?? 0, to = args.Value<double?>("toMs") ?? double.MaxValue;
            if (from < 0 || to <= from || double.IsNaN(from) || double.IsNaN(to) || double.IsInfinity(from) || double.IsInfinity(to))
                throw new McpToolException(McpErrorCodes.InvalidParams, "Use a finite interval 0 <= fromMs < toMs.");
            ValidatePage(args);
            int budget = args.Value<int?>("budgetMs") ?? 1000;
            if (budget < 1 || budget > 5000) throw new McpToolException(McpErrorCodes.InvalidParams, "budgetMs must be 1–5000.");
            string threadFilter = args.Value<string>("thread") ?? "";
            string key = CacheKey(file.id + ":" + file.stamp, threadFilter, from, to);
            string cachePath = Path.Combine(DirectoryPath, file.id + "." + key + ".report.json");
            if (File.Exists(cachePath))
            {
                try { return ToolResult.Ok(SelectReport(JObject.Parse(File.ReadAllText(cachePath)), args, true)); }
                catch (Newtonsoft.Json.JsonException) { /* Rebuild an interrupted or invalid cache from raw evidence. */ }
            }
            string cursor = args.Value<string>("cursor");
            if (cursor != null && (pendingAnalysis?.Cursor != cursor || pendingAnalysis.Key != key))
                throw new McpToolException(McpErrorCodes.InvalidParams, "Analysis cursor expired or scope changed. Repeat without cursor to restart; keep capture/thread/time filters unchanged when resuming.");
            ct.ThrowIfCancellationRequested();
            var capture = Load(file);
            if (pendingAnalysis?.Key != key || pendingAnalysis.Capture != capture)
            {
                if (cursor != null) throw new McpToolException(McpErrorCodes.Conflict, "Profiler history changed; restart analysis without cursor.");
                pendingAnalysis = new Analysis { Key = key, Capture = capture, Frame = capture.First };
            }
            var scan = pendingAnalysis;
            var timer = Stopwatch.StartNew();
            var slice = Stopwatch.StartNew();
            long startSamples = scan.Samples;
            double maxSlice = 0;
            while (scan.Frame <= capture.Last && timer.ElapsedMilliseconds < budget && scan.Samples - startSamples < 250000)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsLoaded(capture)) throw new McpToolException(McpErrorCodes.Conflict, "Profiler history changed during analysis. Restart without cursor.");
                using (var data = ProfilerDriver.GetRawFrameDataView(scan.Frame, scan.Thread))
                {
                    if (!data.valid || scan.Thread >= 1024) { scan.Frame++; scan.Thread = scan.Sample = 0; continue; }
                    if (!Matches(data.threadName, threadFilter) || data.frameStartTimeMs - capture.Origin >= to ||
                        data.frameStartTimeMs - capture.Origin + data.frameTimeMs < from)
                    { scan.Thread++; scan.Sample = 0; continue; }
                    while (scan.Sample < data.sampleCount)
                    {
                        if ((scan.Sample & 255) == 0 && (slice.ElapsedMilliseconds >= 8 || timer.ElapsedMilliseconds >= budget || scan.Samples - startSamples >= 250000)) break;
                        int sample = scan.Sample++;
                        scan.Samples++;
                        double start = data.GetSampleStartTimeMs(sample) - capture.Origin;
                        if (start < from || start >= to) continue;
                        string marker = data.GetSampleName(sample) ?? "(unnamed)";
                        var group = (data.threadId, marker);
                        if (!scan.Totals.TryGetValue(group, out var value))
                        {
                            if (scan.Totals.Count >= 20000) throw new McpToolException(McpErrorCodes.InvalidParams, "Capture has over 20,000 marker/thread groups in this scope. Narrow thread/time filters; raw evidence is preserved.");
                            scan.Totals.Add(group, value = new ProfilerMarkerTotals(data.threadName, data.threadId, marker));
                        }
                        double duration = data.GetSampleTimeMs(sample);
                        value.Add(start, duration, SelfMs(data, sample), scan.Frame - capture.First, scan.Thread, sample);
                    }
                    if (scan.Sample >= data.sampleCount) { scan.Thread++; scan.Sample = 0; }
                }
                if (slice.ElapsedMilliseconds >= 8)
                {
                    maxSlice = Math.Max(maxSlice, slice.Elapsed.TotalMilliseconds);
                    if (timer.ElapsedMilliseconds >= budget) break;
                    await ctx.Frames.WaitNextFrameAsync(ct);
                    slice.Restart();
                }
            }
            scan.ElapsedMs += timer.Elapsed.TotalMilliseconds;
            bool partial = scan.Frame <= capture.Last;
            var result = new JObject {
                ["captureId"] = file.id, ["rawPath"] = file.path, ["partial"] = partial,
                ["cursor"] = partial ? scan.Cursor : null,
                ["partialReason"] = partial ? "Analysis budget reached; resume with cursor and the same capture/thread/time scope." : null,
                ["firstFrame"] = 0, ["lastFrame"] = capture.Last - capture.First,
                ["nextFrame"] = scan.Frame - capture.First, ["nextThread"] = scan.Thread, ["nextSample"] = scan.Sample,
                ["samplesScanned"] = scan.Samples, ["analysisMs"] = scan.ElapsedMs,
                ["loadMs"] = capture.LoadMs, ["maxAnalysisSliceMs"] = Math.Max(maxSlice, slice.Elapsed.TotalMilliseconds),
                ["coverage"] = "Retained Unity CPU history; older raw frames may have been evicted by Unity's history capacity. Indexes are relative to this retained capture.",
                ["rows"] = new JArray(scan.Totals.Values.Select(v => v.Row())),
                ["note"] = "Only calls starting in the interval count. Inclusive rows overlap. Self excludes direct children. Cadence/gaps and idle markers describe elapsed time, not CPU work."
            };
            if (!partial)
            {
                string temp = cachePath + ".tmp";
                File.WriteAllText(temp, result.ToString(Newtonsoft.Json.Formatting.None));
                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(temp, cachePath);
                pendingAnalysis = null;
            }
            return ToolResult.Ok(SelectReport(result, args, false));
        }

        internal static JObject SelectReport(JObject report, JObject args, bool cached)
        {
            ValidatePage(args);
            string sort = args.Value<string>("sortBy") ?? "selfMs";
            if (!new[] { "selfMs", "inclusiveMs", "maxMs", "calls", "gapMs" }.Contains(sort))
                throw new McpToolException(McpErrorCodes.InvalidParams, "Unknown profiler sortBy.");
            var rows = ((JArray)report["rows"]).OfType<JObject>()
                .Where(r => Matches(r.Value<string>("marker"), args.Value<string>("marker")))
                .OrderByDescending(r => r.Value<double>(sort)).ThenBy(r => r.Value<string>("marker"), StringComparer.Ordinal)
                .ThenBy(r => r.Value<string>("threadId"), StringComparer.Ordinal).ToArray();
            var result = (JObject)report.DeepClone();
            result["cacheHit"] = cached;
            Page(result, rows, args);
            return result;
        }

        private static void ValidatePage(JObject args)
        {
            int top = args.Value<int?>("topN") ?? 20, offset = args.Value<int?>("offset") ?? 0;
            if (top < 1 || top > 100 || offset < 0) throw new McpToolException(McpErrorCodes.InvalidParams, "Use topN 1–100 and offset >= 0.");
        }

        private static void Page(JObject result, JObject[] rows, JObject args)
        {
            int offset = args.Value<int?>("offset") ?? 0, top = args.Value<int?>("topN") ?? 20;
            result["matchedRows"] = rows.Length;
            result["offset"] = offset;
            result["rows"] = new JArray(rows.Skip(offset).Take(top));
            result["nextOffset"] = (long)offset + top < rows.Length ? new JValue(offset + top) : JValue.CreateNull();
        }

        private static double SelfMs(RawFrameDataView data, int sample)
        {
            double children = 0;
            for (int child = sample + 1, n = 0; n < data.GetSampleChildrenCount(sample); n++)
            { children += data.GetSampleTimeMs(child); child += data.GetSampleChildrenCountRecursive(child) + 1; }
            return Math.Max(0, data.GetSampleTimeMs(sample) - children);
        }

        private static ValueTask<ToolResult> Frames(ToolContext ctx, CancellationToken ct)
        {
            ValidatePage(ctx.Arguments);
            var capture = Load(CaptureFile(ctx.Arguments));
            var rows = new List<JObject>();
            for (int f = capture.First; f <= capture.Last; f++)
            {
                ct.ThrowIfCancellationRequested();
                using var data = ProfilerDriver.GetRawFrameDataView(f, 0);
                if (data.valid) rows.Add(new JObject { ["frame"] = f - capture.First,
                    ["startMs"] = data.frameStartTimeMs - capture.Origin, ["durationMs"] = data.frameTimeMs, ["samples"] = data.sampleCount });
            }
            var result = new JObject { ["captureId"] = capture.Id, ["coverage"] = "Retained main-thread frames; Unity history may retain only a suffix of the raw file." };
            Page(result, rows.OrderByDescending(r => r.Value<double>("durationMs")).ToArray(), ctx.Arguments);
            return new(ToolResult.Ok(result));
        }

        private static ValueTask<ToolResult> Sample(ToolContext ctx, CancellationToken ct)
        {
            ValidatePage(ctx.Arguments);
            var args = ctx.Arguments;
            var capture = Load(CaptureFile(args));
            int frame = args.Value<int>("frame"), thread = args.Value<int?>("threadIndex") ?? 0, sample = args.Value<int?>("sample") ?? 0;
            if (frame < 0 || frame > capture.Last - capture.First || thread < 0 || thread >= 1024)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Frame/thread is outside retained capture history.");
            using var data = ProfilerDriver.GetRawFrameDataView(capture.First + frame, thread);
            if (!data.valid || sample < 0 || sample >= data.sampleCount)
                throw new McpToolException(McpErrorCodes.InvalidParams, "Sample does not exist in this frame/thread.");
            JObject Row(int s) => new JObject { ["sample"] = s, ["marker"] = data.GetSampleName(s),
                ["startMs"] = data.GetSampleStartTimeMs(s) - capture.Origin, ["inclusiveMs"] = data.GetSampleTimeMs(s),
                ["selfMs"] = SelfMs(data, s), ["children"] = data.GetSampleChildrenCount(s) };
            var rows = new List<JObject>();
            for (int child = sample + 1, n = 0; n < data.GetSampleChildrenCount(sample); n++)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(Row(child)); child += data.GetSampleChildrenCountRecursive(child) + 1;
            }
            var result = new JObject { ["captureId"] = capture.Id, ["frame"] = frame, ["threadIndex"] = thread,
                ["thread"] = data.threadName, ["sample"] = Row(sample) };
            Page(result, rows.OrderByDescending(r => r.Value<double>("inclusiveMs")).ToArray(), args);
            return new(ToolResult.Ok(result));
        }
    }
}
