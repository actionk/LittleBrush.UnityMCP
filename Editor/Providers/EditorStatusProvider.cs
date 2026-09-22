using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Host;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class EditorStatusProvider : AttributedToolProvider
    {
        public override string Namespace => "editor";

        [McpToolDeclaration]
        private System.Collections.Generic.IEnumerable<ToolDescriptor> DescribeTools()
        {
            McpPlayModeOwnership.Initialize();

            // editor.status is the diagnostic-of-last-resort and MUST NOT require
            // the main thread. If the main thread is stalled (modal dialog, frozen
            // reload), every other tool times out and the agent is blind. Status
            // reads cached values from McpBridgeHost so it answers instantly even
            // when nothing else can run.
            yield return new ToolDescriptor
            {
                Name = "editor.status",
                Description = "Editor state: isPlaying, isPaused, isCompiling, editorFocused, lastUserActivityAt, editorIdleForMs, activeScene, unityVersion, projectPath, mainThreadStalledMs (>0 means tools are likely timing out).",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                ReloadSafe = true,
                RequiresMainThread = false,
                InputSchema = JObject.Parse(@"{ 'type': 'object', 'properties': { 'detail': { 'enum': ['compact', 'full'] } } }"),
                Handler = Status,
            };
            yield return new ToolDescriptor
            {
                Name = "editor.tools.list",
                Description = "Search and page the live Unity-side tool registry. Returns compact name and availability records unless includeMetadata=true.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                ReloadSafe = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""search"": { ""type"": ""string"" },
                        ""availableOnly"": { ""type"": ""boolean"" },
                        ""includeMetadata"": { ""type"": ""boolean"" },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 }
                    }
                }"),
                Handler = ListTools,
            };
            yield return new ToolDescriptor
            {
                Name = "editor.metrics",
                Description = "Page session-local MCP tool duration and serialized response-size metrics. Use query to inspect suspected high-context tools.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                ReloadSafe = true,
                RequiresMainThread = false,
                Annotations = new JObject { ["readOnlyHint"] = true },
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""query"": { ""type"": ""string"" },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100 }
                    }
                }"),
                Handler = Metrics,
            };
            yield return new ToolDescriptor
            {
                Name = "editor.play",
                Description = "Enter Play Mode under the local EditorState trust policy. Returns a playSessionToken; only that token can stop the MCP-started session.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""reason""],
                    ""properties"": {
                        ""reason"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 500, ""description"": ""The specific capability that Edit Mode or preview tools cannot validate."" }
                    }
                }"),
                Handler = Play,
            };
            yield return new ToolDescriptor
            {
                Name = "editor.stop",
                ExclusiveGroup = "profiler",
                Description = "Exit only a Play Mode session started by editor.play. User-started Play Mode is protected. profileExit starts CPU capture immediately before requesting exit and stops after returning to Edit Mode; returns the captureId for profiler.report.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Sync,
                Annotations = new JObject { ["destructiveHint"] = true },
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""playSessionToken""],
                    ""properties"": {
                        ""playSessionToken"": { ""type"": ""string"", ""minLength"": 1 },
                        ""profileExit"": { ""type"": ""boolean"" },
                        ""reason"": { ""type"": ""string"", ""maxLength"": 500 }
                    }
                }"),
                Handler = Stop,
            };
            yield return new ToolDescriptor
            {
                Name = "editor.request_stop_play_mode",
                Description = "Request stopping user-owned Play Mode under the local UserPlayModeStop trust policy.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Async,
                Timeout = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10),
                ExclusiveGroup = "play-mode",
                Annotations = new JObject { ["destructiveHint"] = true },
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""reason""],
                    ""properties"": {
                        ""reason"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 500, ""description"": ""Why the AI needs to leave Play Mode."" }
                    }
                }"),
                Handler = RequestStopPlayMode,
            };
            yield return Tool("editor.pause", "Pause the editor.", Pause);
            yield return Tool("editor.resume", "Resume the editor.", Resume);
            yield return new ToolDescriptor
            {
                Name = "editor.compile.errors",
                RequiresWriterLease = false,
                TrustCategory = ToolTrustCategory.Read,
                Description = "Read the last compile result. Defaults to paged errors; warnings are counted but returned only with detail=all.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                ReloadSafe = true,
                InputSchema = CompileResultSchema(),
                Handler = CompileErrors,
            };
            yield return Tool("editor.refresh", "Force AssetDatabase import to detect external file changes. Prefer editor.ensure_compiled when you also need compile errors.", Refresh, ToolAvailability.EditMode, reloadSafe: true);
            yield return new ToolDescriptor
            {
                Name = "editor.ensure_compiled",
                Description = "Compile once after a coherent C#/.asmdef/package edit batch and return exact errors. Omit force so unchanged script inputs reuse the cached result; asset-only changes do not need compilation. Reload-safe across domain reloads.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Async,
                Timeout = TimeSpan.FromMinutes(3),
                ReloadSafe = true,
                ExclusiveGroup = "compile",
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""playSessionToken"": { ""type"": ""string"", ""description"": ""Required only when a real compile must exit a Play Mode session previously started by editor.play."" },
                        ""force"": { ""type"": ""boolean"", ""description"": ""Expensive cache bypass. Never use for routine verification or after each edit; set true only when the input hash is demonstrably stale."" },
                        ""detail"": { ""type"": ""string"", ""enum"": [""summary"", ""errors"", ""all""] },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 }
                    }
                }"),
                Handler = EnsureCompiled,
            };
            yield return new ToolDescriptor
            {
                Name = "editor.wait_ready",
                Description = "Wait until the Editor is ready, then return status plus a compact compile result.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Async,
                Timeout = TimeSpan.FromMinutes(3),
                ReloadSafe = true,
                RequiresWriterLease = false,
                TrustCategory = ToolTrustCategory.Read,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""timeoutSeconds"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 300 },
                        ""detail"": { ""type"": ""string"", ""enum"": [""summary"", ""errors"", ""all""] },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 }
                    }
                }"),
                Handler = WaitReady,
            };
        }

        private static ToolDescriptor Tool(string name, string description, Func<ToolContext, CancellationToken, ValueTask<ToolResult>> handler,
            ToolAvailability availability = ToolAvailability.Either,
            bool reloadSafe = false)
            => new()
            {
                Name = name,
                Description = description,
                Availability = availability,
                Execution = ToolExecution.Sync,
                ReloadSafe = reloadSafe,
                InputSchema = new JObject { ["type"] = "object" },
                Handler = handler,
            };

        // Runs on a background HTTP thread (RequiresMainThread = false). Every
        // field below MUST be safe to read from any thread:
        //   - McpBridgeHost.Cached* / MainThreadStalledMs: volatile fields, OK
        //   - McpEditorActivity.*: cross-thread-safe cached/session fields, OK
        //   - Application.unityVersion / dataPath: static, set at boot, OK
        //   - CompileErrorStore.* : already accessed cross-thread elsewhere, OK
        // Touching EditorApplication.* / SceneManager.* directly here would
        // throw "can only be called from the main thread" — which is exactly
        // why we cache them.
        private static ValueTask<ToolResult> Status(ToolContext ctx, CancellationToken __)
            => new(ToolResult.Ok(ProjectStatus(BuildStatusPayload(), ctx.Arguments)));

        private static ValueTask<ToolResult> ListTools(ToolContext ctx, CancellationToken __)
        {
            var isPlaying = McpBridgeHost.CachedIsPlaying;
            var isCompiling = McpBridgeHost.CachedIsCompiling;
            return new ValueTask<ToolResult>(ToolResult.Ok(CreateToolsListResponse(
                McpBridgeHost.EnumerateTools(), ctx.Arguments, isPlaying, isCompiling)));
        }

        private static ValueTask<ToolResult> Metrics(ToolContext ctx, CancellationToken __)
            => new(ToolResult.Ok(McpToolMetrics.CreateResponse(ctx.Arguments)));

        internal static JObject CreateToolsListResponse(
            IEnumerable<ToolDescriptor> descriptors,
            JObject args,
            bool isPlaying,
            bool isCompiling)
        {
            var names = args["names"] is JArray nameArray ? nameArray.Values<string>().ToArray() : Array.Empty<string>();
            var search = args["search"]?.Value<string>();
            var availableOnly = args["availableOnly"]?.Value<bool>() == true;
            var includeMetadata = args["includeMetadata"]?.Value<bool>() == true;
            var offset = args["offset"]?.Value<int>() ?? 0;
            var limit = args["limit"]?.Value<int>() ?? 25;
            if (offset < 0 || limit is < 1 or > 500)
                throw new McpToolException(McpErrorCodes.InvalidParams, "offset must be >= 0 and limit must be between 1 and 500.");
            var all = descriptors.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
            var matches = new List<JObject>();
            foreach (var tool in all)
            {
                var available = IsCurrentlyAvailable(tool, isPlaying, isCompiling);
                if (names.Length > 0 && !names.Contains(tool.Name, StringComparer.Ordinal))
                    continue;
                if (!string.IsNullOrWhiteSpace(search)
                    && tool.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (availableOnly && !available)
                    continue;

                var item = new JObject
                {
                    ["name"] = tool.Name,
                    ["currentlyAvailable"] = available,
                };
                if (includeMetadata)
                {
                    item["provider"] = tool.ProviderTypeName;
                    item["assembly"] = tool.ProviderAssemblyName;
                    item["availability"] = tool.Availability.ToString();
                    item["requiresMainThread"] = tool.RequiresMainThread;
                    item["requiresWriterLease"] = tool.RequiresWriterLease;
                    item["reloadSafe"] = tool.ReloadSafe;
                    var category = McpTrustPolicy.ResolveCategory(tool, new JObject());
                    item["trustCategory"] = category.ToString();
                    item["trustDecision"] = McpTrustPolicy.GetDecision(category).ToString();
                    if (!string.IsNullOrEmpty(tool.ExclusiveGroup)) item["exclusiveGroup"] = tool.ExclusiveGroup;
                }
                matches.Add(item);
            }

            var tools = new JArray(matches.Skip(offset).Take(limit));
            var result = new JObject
            {
                ["total"] = all.Count,
                ["filtered"] = matches.Count,
                ["offset"] = offset,
                ["returned"] = tools.Count,
                ["truncated"] = offset + tools.Count < matches.Count,
                ["isPlaying"] = isPlaying,
                ["playModeOwner"] = !isPlaying
                    ? "none"
                    : McpPlayModeOwnership.IsOwnedCached ? "mcp" : "user",
                ["isCompiling"] = isCompiling,
                ["trustPolicy"] = McpTrustPolicy.Snapshot(),
                ["tools"] = tools,
            };
            if (offset + tools.Count < matches.Count)
                result["nextOffset"] = offset + tools.Count;
            return result;
        }

        private static ValueTask<ToolResult> CompileErrors(ToolContext ctx, CancellationToken __)
            => new(ToolResult.Ok(ProjectCompileMessages(BuildCompileErrorsPayload(), ctx.Arguments)));

        private static JObject CompileResultSchema() => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""detail"": { ""type"": ""string"", ""enum"": [""summary"", ""errors"", ""all""] },
                ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 }
            }
        }");

        internal static JObject ProjectStatus(JObject status, JObject arguments)
        {
            if ((string)arguments?["detail"] != "compact") return status;
            var compact = new JObject();
            foreach (var key in new[] { "isPlaying", "playModeOwner", "isPaused", "isCompiling", "isUpdating", "mainThreadStalledMs", "compilePassCounter", "registryErrorCount", "registryErrors", "writer" })
                if (status[key] != null) compact[key] = status[key];
            return compact;
        }

        private static JObject BuildStatusPayload()
        {
            var stalledMs = McpBridgeHost.MainThreadStalledMs;
            var result = new JObject
            {
                ["isPlaying"] = McpBridgeHost.CachedIsPlaying,
                ["playModeOwner"] = !McpBridgeHost.CachedIsPlaying
                    ? "none"
                    : McpPlayModeOwnership.IsOwnedCached ? "mcp" : "user",
                ["isPaused"] = McpBridgeHost.CachedIsPaused,
                ["isCompiling"] = McpBridgeHost.CachedIsCompiling,
                ["isUpdating"] = McpBridgeHost.CachedIsUpdating,
                ["editorFocused"] = McpEditorActivity.IsEditorFocused,
                ["lastUserActivityAt"] = McpEditorActivity.LastActivityAtUnixMs,
                ["editorIdleForMs"] = McpEditorActivity.IdleForMs,
                ["lastUserActivitySource"] = McpEditorActivity.LastActivitySource,
                ["recentActivityThresholdMs"] = McpEditorActivity.RecentActivityWindowMs,
                ["unityVersion"] = UnityEngine.Application.unityVersion,
                ["projectPath"] = System.IO.Path.GetFullPath(UnityEngine.Application.dataPath + "/.."),
                ["activeScenePath"] = McpBridgeHost.CachedActiveScenePath,
                ["activeSceneName"] = McpBridgeHost.CachedActiveSceneName,
                ["bridgePort"] = McpBridgeHost.Port,
                ["worker"] = new JObject
                {
                    ["batchMode"] = McpBatchWorker.IsBatchMode,
                    ["managed"] = McpBatchWorker.IsOwned,
                    ["graphicsAvailable"] = McpBatchWorker.HasGraphics,
                },
                ["compilePassCounter"] = CompileErrorStore.PassCounter,
                ["lastCompileFinishedAt"] = CompileErrorStore.LastFinishedAt,
                ["lastCompileStartedAt"] = CompileErrorStore.LastStartedAt,
                ["lastInputHash"] = CompileErrorStore.LastInputHash,
                ["currentInputHash"] = SafeCurrentHash(),
                ["mainThreadStalledMs"] = stalledMs,
                ["trustPolicy"] = McpTrustPolicy.Snapshot(),
                ["writer"] = McpBridgeHost.WriterStatus,
            };
            var registryErrors = McpBridgeHost.RegistryErrors;
            result["registryErrorCount"] = registryErrors.Count;
            if (registryErrors.Count > 0)
                result["registryErrors"] = new JArray(registryErrors.Take(10));
            return result;
        }

        private static JObject BuildCompileErrorsPayload()
        {
            var snapshot = CompileErrorStore.Snapshot();
            var arr = new JArray();
            int errors = 0, warnings = 0;
            foreach (var m in snapshot)
            {
                arr.Add(new JObject
                {
                    ["assembly"] = m.Assembly,
                    ["file"] = m.File,
                    ["line"] = m.Line,
                    ["column"] = m.Column,
                    ["message"] = m.Text,
                    ["type"] = m.Type.ToString(),
                    ["timestamp"] = m.Timestamp,
                });
                if (m.Type == UnityEditor.Compilation.CompilerMessageType.Error) errors++;
                else if (m.Type == UnityEditor.Compilation.CompilerMessageType.Warning) warnings++;
            }
            return new JObject
            {
                ["passCounter"] = CompileErrorStore.PassCounter,
                ["lastFinishedAt"] = CompileErrorStore.LastFinishedAt,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["errorCount"] = errors,
                ["warningCount"] = warnings,
                ["messages"] = arr,
            };
        }

        private static ValueTask<ToolResult> Play(ToolContext ctx, CancellationToken __)
        {
            if (EditorApplication.isCompiling) throw new McpToolException(McpErrorCodes.Compiling, "Editor is compiling.");
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["action"] = "already_playing",
                    ["playModeOwner"] = McpPlayModeOwnership.IsOwnedCached ? "mcp" : "user",
                }));
            }

            var reason = RequirePlayReason(ctx);
            var token = McpPlayModeOwnership.Begin();
            ctx.Logger?.Log(LogLevel.Info,
                $"Play Mode started by MCP request '{ctx.RequestId}' ({reason}).");
            try
            {
                EditorApplication.isPlaying = true;
            }
            catch
            {
                McpPlayModeOwnership.Clear();
                throw;
            }

            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["action"] = "play",
                ["playModeOwner"] = "mcp",
                ["playSessionToken"] = token,
            }));
        }

        private static ValueTask<ToolResult> Stop(ToolContext ctx, CancellationToken __)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["action"] = "already_stopped" }));

            JObject capture = null;
            McpPlayModeOwnership.Stop(ctx, ctx.Arguments.Value<string>("playSessionToken"), "editor.stop",
                ctx.Arguments.Value<bool>("profileExit") ? () => capture = ProfilerProvider.StartExitCapture(ctx) : null);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["action"] = "stop",
                ["playModeOwner"] = "mcp",
                ["capture"] = capture,
            }));
        }

        private static ValueTask<ToolResult> RequestStopPlayMode(ToolContext ctx, CancellationToken ct)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["action"] = "already_stopped", ["approved"] = true }));

            var reason = ctx.Arguments.Value<string>("reason").Replace('\r', ' ').Replace('\n', ' ').Trim();
            McpPlayModeOwnership.StopAfterApproval(ctx, "editor.request_stop_play_mode", reason);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["action"] = "stop",
                ["approved"] = true,
                ["playModeOwner"] = "user",
            }));
        }

        private static ValueTask<ToolResult> Pause(ToolContext _, CancellationToken __)
        {
            EditorApplication.isPaused = true;
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["action"] = "pause" }));
        }

        private static ValueTask<ToolResult> Resume(ToolContext _, CancellationToken __)
        {
            EditorApplication.isPaused = false;
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["action"] = "resume" }));
        }

        private static ValueTask<ToolResult> Refresh(ToolContext _, CancellationToken __)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject
            {
                ["action"] = "refresh",
                ["isCompiling"] = EditorApplication.isCompiling,
            }));
        }

        private static string SafeCurrentHash()
        {
            try { return CompileErrorStore.ComputeInputHash(); }
            catch { return null; }
        }

        private static async ValueTask<ToolResult> EnsureCompiled(ToolContext ctx, CancellationToken ct)
        {
            var force = ctx.Arguments["force"]?.Value<bool>() == true;
            var playSessionToken = ctx.Arguments.Value<string>("playSessionToken");
            var replayed = ctx.IsReplay;
            var envelope = await EnsureCompiledCore(ctx, force, playSessionToken, ct, replayed);
            return ToolResult.Ok(ProjectCompileMessages(envelope, ctx.Arguments));
        }

        private static async ValueTask<ToolResult> WaitReady(ToolContext ctx, CancellationToken ct)
        {
            var timeoutSeconds = ctx.Arguments["timeoutSeconds"]?.Value<int>() ?? 120;
            await ctx.Frames.WaitUntilAsync(
                () => !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                TimeSpan.FromSeconds(timeoutSeconds),
                ct);

            return ToolResult.Ok(new JObject
            {
                ["ready"] = true,
                ["status"] = BuildStatusPayload(),
                ["compile"] = ProjectCompileMessages(BuildCompileErrorsPayload(), ctx.Arguments),
            });
        }

        private static bool IsCurrentlyAvailable(ToolDescriptor tool, bool isPlaying, bool isCompiling)
        {
            if (McpBatchWorker.UnavailableReason(tool) != null) return false;
            if (isCompiling && (tool.Availability & ToolAvailability.Compiling) == 0)
                return false;

            return (isPlaying && (tool.Availability & ToolAvailability.PlayMode) != 0)
                   || (!isPlaying && (tool.Availability & ToolAvailability.EditMode) != 0);
        }

        /// <summary>
        /// Ensures the on-disk script set is fully imported and compiled, then returns a
        /// structured envelope describing the last completed pass. Shared by
        /// <c>editor.ensure_compiled</c>.
        /// </summary>
        internal static async ValueTask<JObject> EnsureCompiledCore(
            ToolContext ctx, bool force, string playSessionToken, CancellationToken ct, bool replayed = false)
        {
            var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var timings = new JObject { ["fingerprint"] = 0L, ["refresh"] = 0L, ["wait"] = 0L, ["editMode"] = 0L };
            var compileRequests = 0;
            var refreshRequests = 0;
            var phase = "timeout_inflight";
            string currentHash = null;
            var initialPass = CompileErrorStore.PassCounter;

            TimeSpan Remaining()
            {
                var remaining = TimeSpan.FromMinutes(2) - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Compilation inputs did not settle within two minutes.");
                return remaining;
            }
            void AddTime(string key, long before) => timings[key] = (long)timings[key] + watch.ElapsedMilliseconds - before;
            string Fingerprint()
            {
                var before = watch.ElapsedMilliseconds;
                try
                {
                    return SafeCurrentHash() ?? throw new McpToolException(McpErrorCodes.ToolError,
                        "Cannot fingerprint compilation inputs. No compile result can certify the current files; inspect filesystem access before retrying.");
                }
                finally { AddTime("fingerprint", before); }
            }
            async ValueTask WaitForCompilation()
            {
                var before = watch.ElapsedMilliseconds;
                try { await WaitForActiveCompilation(ctx, ct, Remaining()); }
                finally { AddTime("wait", before); }
            }
            JObject Reply(JObject envelope)
            {
                envelope["timingsMs"] = timings;
                envelope["compileRequests"] = compileRequests;
                envelope["refreshRequests"] = refreshRequests;
                envelope["replayed"] = replayed;
                return envelope;
            }
            bool CurrentPassMatches(bool requireNewPass) => CanUseCachedResult(
                requireNewPass, false, EditorApplication.isCompiling, EditorApplication.isUpdating,
                currentHash, CompileErrorStore.LastInputHash, CompileErrorStore.PassCounter);

            try
            {
                await WaitForCompilation();
                currentHash = Fingerprint();
                // An already-running pass also satisfies force, but only for its actual inputs.
                var forcePending = force && !replayed && CompileErrorStore.PassCounter == initialPass;
                if (CurrentPassMatches(forcePending))
                    return Reply(BuildEnvelope(CompileErrorStore.PassCounter > initialPass ? "fresh"
                        : force ? "replayed_cached" : "cached", currentHash, startedAt));

                // No second disk scan when both state checks complete synchronously in Edit Mode.
                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    var before = watch.ElapsedMilliseconds;
                    try { await EnsureOwnedEditMode(ctx, playSessionToken, "editor.ensure_compiled", ct); }
                    finally { AddTime("editMode", before); }
                    await WaitForCompilation();
                    currentHash = Fingerprint();
                    forcePending &= CompileErrorStore.PassCounter == initialPass;
                    if (CurrentPassMatches(forcePending))
                        return Reply(BuildEnvelope("fresh", currentHash, startedAt));
                }

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    Remaining();
                    phase = "timeout_refresh";
                    var beforeRefreshPass = CompileErrorStore.PassCounter;
                    var before = watch.ElapsedMilliseconds;
                    try
                    {
                        refreshRequests++;
                        AssetDatabase.Refresh();
                    }
                    finally { AddTime("refresh", before); }
                    // Give Unity's automatic compilation request a tick to start before deciding
                    // whether an explicit request is necessary. Never cache by elapsed time alone.
                    before = watch.ElapsedMilliseconds;
                    try { await ctx.Frames.WaitNextFrameAsync(ct); }
                    finally { AddTime("wait", before); }
                    await WaitForCompilation();
                    currentHash = Fingerprint();
                    forcePending &= CompileErrorStore.PassCounter == beforeRefreshPass;
                    if (CurrentPassMatches(forcePending))
                        return Reply(BuildEnvelope("fresh", currentHash, startedAt));

                    var beforeRequestPass = CompileErrorStore.PassCounter;
                    compileRequests++;
                    CompilationPipeline.RequestScriptCompilation();
                    before = watch.ElapsedMilliseconds;
                    try
                    {
                        await ctx.Frames.WaitUntilAsync(
                            () => !EditorApplication.isCompiling && !EditorApplication.isUpdating
                                && CompileErrorStore.PassCounter > beforeRequestPass,
                            Remaining(), ct);
                    }
                    finally { AddTime("wait", before); }
                    forcePending = false;
                    currentHash = Fingerprint();
                    if (CurrentPassMatches(false))
                        return Reply(BuildEnvelope("fresh", currentHash, startedAt));
                    // External edits during compilation require another pass, not a stale success.
                    phase = "timeout_inputs_changed";
                    Remaining();
                }
            }
            catch (TimeoutException ex)
            {
                return Reply(BuildCompileTimeoutEnvelope(phase, currentHash, startedAt, ex));
            }
        }

        private static async ValueTask<bool> WaitForActiveCompilation(ToolContext ctx, CancellationToken ct, TimeSpan? timeout = null)
        {
            var requireCompletedPass = EditorApplication.isCompiling;
            if (!requireCompletedPass && !EditorApplication.isUpdating)
                return false;

            var passBefore = CompileErrorStore.PassCounter;
            await ctx.Frames.WaitUntilAsync(
                () => !EditorApplication.isCompiling
                      && !EditorApplication.isUpdating
                      && (!requireCompletedPass || CompileErrorStore.PassCounter > passBefore),
                timeout ?? TimeSpan.FromMinutes(2),
                ct);
            return CompileErrorStore.PassCounter > passBefore;
        }

        internal static bool CanUseCachedResult(
            bool force,
            bool replayed,
            bool isCompiling,
            bool isUpdating,
            string currentHash,
            string lastHash,
            int passCounter)
        {
            if (isCompiling || isUpdating
                || string.IsNullOrEmpty(currentHash)
                || string.IsNullOrEmpty(lastHash)
                || passCounter <= 0
                || currentHash != lastHash)
                return false;
            return !force || replayed;
        }

        private static JObject BuildCompileTimeoutEnvelope(string source, string inputHash, long startedAt, TimeoutException ex)
        {
            var envelope = BuildEnvelope(source, inputHash, startedAt);
            envelope["success"] = false;
            envelope["timeout"] = true;
            envelope["timeoutMessage"] = ex.Message;
            envelope["isCompiling"] = EditorApplication.isCompiling;
            envelope["hint"] = "Unity did not report compile completion before the wait timeout. Check editor.status and editor.compile.errors; if mainThreadStalledMs is high, focus the Editor or set Preferences > General > Interaction Mode to No Throttling.";
            return envelope;
        }

        internal static JObject BuildEnvelope(string source, string inputHash, long startedAt)
        {
            var snapshot = CompileErrorStore.Snapshot();
            int errors = 0, warnings = 0;
            var messages = new JArray();
            foreach (var m in snapshot)
            {
                if (m.Type == CompilerMessageType.Error) errors++;
                else if (m.Type == CompilerMessageType.Warning) warnings++;
                messages.Add(new JObject
                {
                    ["assembly"] = m.Assembly,
                    ["file"] = m.File,
                    ["line"] = m.Line,
                    ["column"] = m.Column,
                    ["message"] = m.Text,
                    ["type"] = m.Type.ToString(),
                });
            }
            return new JObject
            {
                ["success"] = errors == 0,
                ["source"] = source,
                ["inputHash"] = inputHash,
                ["passCounter"] = CompileErrorStore.PassCounter,
                ["lastFinishedAt"] = CompileErrorStore.LastFinishedAt,
                ["errorCount"] = errors,
                ["warningCount"] = warnings,
                ["durationMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt,
                ["messages"] = messages,
            };
        }

        internal static JObject ProjectCompileMessages(JObject payload, JObject arguments)
        {
            var detail = arguments?["detail"]?.Value<string>() ?? "errors";
            var offset = arguments?["offset"]?.Value<int>() ?? 0;
            var limit = arguments?["limit"]?.Value<int>() ?? 25;
            var all = payload["messages"] as JArray ?? new JArray();
            var selected = detail == "all"
                ? all.Children().ToList()
                : detail == "summary"
                    ? new List<JToken>()
                    : all.Children().Where(message => string.Equals(
                        message["type"]?.Value<string>(),
                        CompilerMessageType.Error.ToString(),
                        StringComparison.OrdinalIgnoreCase)).ToList();

            payload["detail"] = detail;
            payload["messageCount"] = all.Count;
            payload.Remove("messages");
            if (detail == "summary") return payload;

            var page = new JArray(selected.Skip(offset).Take(limit));
            payload["offset"] = offset;
            payload["returned"] = page.Count;
            payload["truncated"] = offset + page.Count < selected.Count;
            if (offset + page.Count < selected.Count) payload["nextOffset"] = offset + page.Count;
            payload["messages"] = page;
            return payload;
        }

        internal static async ValueTask EnsureOwnedEditMode(
            ToolContext ctx,
            string playSessionToken,
            string operation,
            CancellationToken ct)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            McpPlayModeOwnership.Stop(ctx, playSessionToken, operation);
            await ctx.Frames.WaitUntilAsync(
                () => !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode,
                TimeSpan.FromSeconds(30),
                ct);
        }

        private static string RequirePlayReason(ToolContext ctx)
        {
            var reason = ctx.Arguments.Value<string>("reason");
            if (string.IsNullOrWhiteSpace(reason))
                throw new McpToolException(
                    McpErrorCodes.ValidationFailed,
                    "editor.play requires a non-empty reason explaining why Edit Mode or preview tools cannot validate the task.");
            return reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }

    internal static class McpPlayModeOwnership
    {
        private const string TokenKey = "LittleBrushGames.Mcp.PlayModeOwnership.Token";
        private static volatile bool s_isOwned;

        static McpPlayModeOwnership()
        {
            s_isOwned = !string.IsNullOrEmpty(SessionState.GetString(TokenKey, ""));
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        internal static bool IsOwnedCached => s_isOwned;

        internal static void Initialize() { }

        internal static string Begin()
        {
            var token = Guid.NewGuid().ToString("N");
            SessionState.SetString(TokenKey, token);
            s_isOwned = true;
            return token;
        }

        internal static void Clear()
        {
            SessionState.EraseString(TokenKey);
            s_isOwned = false;
        }

        internal static void Stop(ToolContext ctx, string token, string operation, Action beforeStop = null)
        {
            var current = SessionState.GetString(TokenKey, "");
            if (string.IsNullOrEmpty(current) || !string.Equals(current, token, StringComparison.Ordinal))
            {
                var owner = string.IsNullOrEmpty(current) ? "user" : "mcp";
                ctx.Logger?.Log(LogLevel.Warn,
                    $"Blocked '{operation}' from stopping {owner}-owned Play Mode (request '{ctx.RequestId}').");
                throw new McpToolException(
                    McpErrorCodes.Conflict,
                    "Play Mode is user-owned or belongs to another MCP session. MCP may only stop a session it started with editor.play and the matching token.",
                    new JObject
                    {
                        ["playModeOwner"] = owner,
                        ["operation"] = operation,
                    });
            }

            var reason = ctx.Arguments.Value<string>("reason");
            reason = string.IsNullOrWhiteSpace(reason)
                ? "no reason supplied"
                : reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
            ctx.Logger?.Log(LogLevel.Warn,
                $"Play Mode stop by '{operation}' for MCP-owned session, request '{ctx.RequestId}' ({reason}).");
            beforeStop?.Invoke();
            EditorApplication.isPlaying = false;
        }

        internal static void StopAfterApproval(ToolContext ctx, string operation, string reason)
        {
            ctx.Logger?.Log(LogLevel.Warn,
                $"Play Mode stop by '{operation}' after user approval for request '{ctx.RequestId}' ({reason}).");
            EditorApplication.isPlaying = false;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state is PlayModeStateChange.ExitingPlayMode or PlayModeStateChange.EnteredEditMode)
                Clear();
        }
    }
}
