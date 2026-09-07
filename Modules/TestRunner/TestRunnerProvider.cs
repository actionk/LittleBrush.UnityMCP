#if UNITY_TESTS_FRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor;
using LittleBrushGames.Mcp.Editor.Host;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine.SceneManagement;

namespace LittleBrushGames.Mcp.Modules.TestRunner
{
    /// <summary>
    /// Wraps Unity Test Framework's <see cref="TestRunnerApi"/> as MCP tools.
    /// Supports both EditMode and PlayMode tests. PlayMode results persist
    /// across domain reload via <see cref="SessionState"/>.
    /// </summary>
    [McpToolProvider]
    [InitializeOnLoad]
    public sealed class TestRunnerProvider : IToolProvider
    {
        private const string SessionKeyPrefix = "McpTestRun_";
        private const string ActiveRunsKey = "McpTestRun_ActiveRunIds";
        private const string RecentRunsKey = "McpTestRun_RecentRuns";
        private const int RecentRunLimit = 32;
        private const string SceneSnapshotKeyPrefix = "McpTestSceneSnapshot_";
        private const string EditModeSceneNamePrefix = "McpEditModeTests_";
        private static readonly HashSet<string> s_ownedRunIds = new(StringComparer.Ordinal);
        private static readonly MethodInfo s_isRunActive = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly Dictionary<string, EditorApplication.CallbackFunction> s_watchdogs = new();
        private static readonly Dictionary<string, PersistentRunCallback> s_callbacks = new();
        private static volatile bool s_hasActiveRun;
        private static McpTestLogCapture s_editModeLogCapture;
        private static string s_editModeLogCaptureRunId;

        public string Namespace => "tests";

        static TestRunnerProvider()
        {
            // After domain reload, re-register callbacks for any in-flight runs
            s_hasActiveRun = !string.IsNullOrEmpty(SessionState.GetString(ActiveRunsKey, ""));
            EditorApplication.update += ReattachPendingRuns;
        }

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "tests.list",
                Description = "Search and page test cases by mode, full-name substring, assembly, or category.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.Async,
                RequiresMainThread = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""mode"": { ""enum"": [""EditMode"", ""PlayMode"", ""All""] },
                        ""query"": { ""type"": ""string"" },
                        ""assemblyNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""categoryNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""detail"": { ""enum"": [""names"", ""metadata""] },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500 }
                    }
                }"),
                Handler = ListTests,
            });

            var testsRun = new ToolDescriptor
            {
                Name = "tests.run",
                Description = "Run tests matching an optional filter under the Tests trust policy. Dirty saved scenes are saved under the UnsavedWork policy; untitled scenes remain blocked. EditMode tests run in a temporary empty scene. Supply a unique runId (32 lowercase hex characters) before dispatch so a lost launch response can be recovered with tests.result. Otherwise use tests.runs to recover the generated ID. Never blindly repeat an unknown launch.",
                Availability = ToolAvailability.Either,
                Execution = ToolExecution.LongRunning,
                RequiresMainThread = true,
                ExclusiveGroup = "test",
                BackgroundOperationActive = () => s_hasActiveRun,
                Annotations = new JObject { ["destructiveHint"] = true },
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""runId"": { ""type"": ""string"", ""minLength"": 32, ""maxLength"": 32, ""description"": ""Optional client-generated UUID without hyphens. Must be unique in this Editor session; reuse is rejected before scene changes. Poll tests.result with this ID if the launch response is lost."" },
                        ""mode"": { ""enum"": [""EditMode"", ""PlayMode""] },
                        ""reason"": { ""type"": ""string"", ""maxLength"": 500 },
                        ""testNames"":     { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""groupNames"":    { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""categoryNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
                        ""assemblyNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
                    }
                }"),
                Handler = RunTests,
            };
            reg.Register(testsRun);

            reg.Register(new ToolDescriptor
            {
                Name = "tests.runs",
                TrustCategory = ToolTrustCategory.Read,
                Description = "Recover a lost tests.run response: list the latest 32 launches, newest first, including completed runs. Metadata survives domain reload, not Editor restart. filterSummary is capped at 1000 characters. Use tests.result with the matching runId for current status/results; do not guess when multiple launches match.",
                Availability = ToolAvailability.Always,
                RequiresMainThread = true,
                RequiresWriterLease = false,
                ReloadSafe = true,
                ExclusiveGroup = "test",
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""properties"": {} }"),
                Handler = (ctx, ct) => new ValueTask<ToolResult>(ToolResult.Ok(new JObject
                {
                    ["runs"] = ReadRecentRuns(),
                    ["retentionLimit"] = RecentRunLimit,
                    ["scope"] = "editor_session",
                })),
            });

            reg.Register(new ToolDescriptor
            {
                Name = "tests.result",
                ReloadSafe = true,
                RequiresWriterLease = false,
                ExclusiveGroup = "test",
                Description = "Poll a test run without changing scenes. running, cancelling and restoring are nonterminal; completed means scene cleanup has finished. auto returns summary while pending and compact paged failures when finished. Request diagnostics explicitly for bounded stack traces and output.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                RequiresMainThread = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""runId""],
                    ""properties"": {
                        ""runId"": { ""type"": ""string"" },
                        ""afterRevision"": { ""type"": ""string"", ""description"": ""Return a compact unchanged response if this result revision still matches."" },
                        ""detail"": { ""enum"": [""auto"", ""summary"", ""failures"", ""diagnostics"", ""all""] },
                        ""offset"": { ""type"": ""integer"", ""minimum"": 0 },
                        ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100 },
                        ""maxTextCharacters"": { ""type"": ""integer"", ""minimum"": 256, ""maximum"": 20000 }
                    }
                }"),
                Handler = GetResult,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "tests.cancel",
                ExclusiveGroup = "test",
                Description = "Cancel a running test run by runId.",
                Availability = ToolAvailability.Always,
                Execution = ToolExecution.Sync,
                RequiresMainThread = true,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""runId""],
                    ""properties"": { ""runId"": { ""type"": ""string"", ""minLength"": 1 } }
                }"),
                Handler = CancelRun,
            });
        }

        // ─── List ───────────────────────────────────────────────

        private static ValueTask<ToolResult> ListTests(ToolContext ctx, CancellationToken ct)
        {
            var mode = ParseMode((string)ctx.Arguments["mode"]) ?? TestMode.EditMode;
            var api = UnityEngine.ScriptableObject.CreateInstance<TestRunnerApi>();
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));

            var modes = new List<TestMode>();
            if (mode == TestMode.EditMode || mode == (TestMode.EditMode | TestMode.PlayMode)) modes.Add(TestMode.EditMode);
            if (mode == TestMode.PlayMode || mode == (TestMode.EditMode | TestMode.PlayMode)) modes.Add(TestMode.PlayMode);

            var pending = modes.Count;
            var totals = new JObject { ["editMode"] = 0, ["playMode"] = 0 };
            var entries = new JArray();
            var sync = new object();

            foreach (var m in modes)
            {
                api.RetrieveTestList(m, root =>
                {
                    if (root != null) Flatten(root, m, entries, totals, sync, null);
                    if (Interlocked.Decrement(ref pending) == 0)
                        tcs.TrySetResult(CreateListResponse(entries, totals, ctx.Arguments));
                });
            }

            return new ValueTask<ToolResult>(tcs.Task.ContinueWith(t => ToolResult.Ok(t.Result), TaskScheduler.Default));
        }

        private static void Flatten(ITestAdaptor node, TestMode mode, JArray sink, JObject totals, object sync, string assembly)
        {
            if (node == null) return;
            if (node.IsTestAssembly)
                assembly = node.Name;
            if (!node.IsSuite && !node.IsTestAssembly && node.Method != null)
            {
                lock (sync)
                {
                    sink.Add(new JObject
                    {
                        ["fullName"] = node.FullName,
                        ["name"] = node.Name,
                        ["uniqueName"] = node.UniqueName,
                        ["assembly"] = assembly,
                        ["mode"] = mode.ToString(),
                        ["categories"] = new JArray(node.Categories ?? Array.Empty<string>()),
                        ["runState"] = node.RunState.ToString(),
                        ["parentFullName"] = node.ParentFullName,
                    });
                    var key = mode == TestMode.EditMode ? "editMode" : "playMode";
                    totals[key] = (int)totals[key] + 1;
                }
            }
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    Flatten(child, mode, sink, totals, sync, assembly);
            }
        }

        internal static JObject CreateListResponse(JArray entries, JObject totals, JObject args)
        {
            var offset = args["offset"]?.Value<int>() ?? 0;
            var limit = args["limit"]?.Value<int>() ?? 25;
            var detail = args["detail"]?.Value<string>() ?? "names";
            if (offset < 0 || limit is < 1 or > 500)
                throw new McpToolException(McpErrorCodes.InvalidParams, "offset must be >= 0 and limit must be between 1 and 500.");
            if (detail is not ("names" or "metadata"))
                throw new McpToolException(McpErrorCodes.InvalidParams, "detail must be names or metadata.");

            var query = args["query"]?.Value<string>();
            var assemblies = ToStringArray(args["assemblyNames"]);
            var categories = ToStringArray(args["categoryNames"]);
            var filtered = entries.OfType<JObject>()
                .Where(entry => string.IsNullOrWhiteSpace(query)
                    || ((string)entry["fullName"])?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(entry => assemblies == null || assemblies.Length == 0
                    || assemblies.Contains((string)entry["assembly"], StringComparer.Ordinal))
                .Where(entry => categories == null || categories.Length == 0
                    || entry["categories"] is JArray entryCategories
                    && entryCategories.Values<string>().Any(category => categories.Contains(category, StringComparer.Ordinal)))
                .OrderBy(entry => (string)entry["fullName"], StringComparer.Ordinal)
                .ToList();
            var selected = filtered.Skip(offset).Take(limit).ToList();
            var page = detail == "metadata"
                ? new JArray(selected.Select(entry => entry.DeepClone()))
                : new JArray(selected.Select(entry => entry["fullName"]?.Value<string>()));
            var result = new JObject
            {
                ["totals"] = totals.DeepClone(),
                ["total"] = entries.Count,
                ["filtered"] = filtered.Count,
                ["detail"] = detail,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["truncated"] = offset + page.Count < filtered.Count,
                ["tests"] = page,
            };
            if (offset + page.Count < filtered.Count)
                result["nextOffset"] = offset + page.Count;
            return result;
        }

        // ─── Run ────────────────────────────────────────────────

        private static async ValueTask<ToolResult> RunTests(ToolContext ctx, CancellationToken ct)
        {
            var runId = ResolveRunId(ctx.Arguments);
            EnsureNoActiveTestRun();

            var modeStr = (string)ctx.Arguments["mode"];
            var mode = ParseMode(modeStr) ?? TestMode.EditMode;
            var isPlayMode = (mode & TestMode.PlayMode) != 0;

            await EnsureEditModeForTests(ctx, ct);
            var autoSavedScenes = await PrepareScenesForTestRun(ctx, ct);
            await EnsureEditModeForTests(ctx, ct);
            EnsureNoActiveTestRun();

            var filter = new Filter
            {
                testMode = mode,
                testNames = ToStringArray(ctx.Arguments["testNames"]),
                groupNames = ToStringArray(ctx.Arguments["groupNames"]),
                categoryNames = ToStringArray(ctx.Arguments["categoryNames"]),
                assemblyNames = ToStringArray(ctx.Arguments["assemblyNames"]),
            };

            var api = UnityEngine.ScriptableObject.CreateInstance<TestRunnerApi>();

            var result = isPlayMode
                ? await RunPlayModeTests(api, filter, runId, ctx.Arguments)
                : await RunEditModeTests(api, filter, runId, ctx.Arguments);

            if (autoSavedScenes.Count > 0)
                result.StructuredContent["autoSavedScenes"] = autoSavedScenes;

            return result;
        }

        private static void EnsureNoActiveTestRun()
        {
            var activeRunId = GetActiveRunId();
            if (activeRunId != null)
                throw new McpToolException(McpErrorCodes.Conflict, $"Test run '{activeRunId}' is already active. Poll tests.result before starting another run.");
            if (IsFrameworkRunActive())
                throw new McpToolException(McpErrorCodes.Conflict, "Unity TestRunner is still running or cleaning up the previous run. Retry shortly.");
        }

        private static async ValueTask EnsureEditModeForTests(ToolContext ctx, CancellationToken ct)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode) return;
            await McpTrustPolicy.AuthorizeOnMainThreadAsync(
                ctx,
                ToolTrustCategory.EditorState,
                "AI requests to stop Play Mode for tests",
                "Unity TestRunner must begin from Edit Mode.",
                ctx.Arguments.Value<string>("reason") ?? "Run the requested Unity tests.",
                ct,
                () => EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode);
            EditorApplication.isPlaying = false;
            await ctx.Frames.WaitUntilAsync(
                () => !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode,
                TimeSpan.FromSeconds(30),
                ct);
        }

        private static async ValueTask<JArray> PrepareScenesForTestRun(ToolContext ctx, CancellationToken ct)
        {
            var untitledScenes = GetUntitledLoadedScenes();
            if (untitledScenes.Count > 0)
            {
                throw new McpToolException(
                    McpErrorCodes.ValidationFailed,
                    "Unity's TestRunner cannot restore an untitled scene after a run. Save it to a chosen path first, then retry.",
                    new JObject
                    {
                        ["reason"] = "untitled_scene",
                        ["untitledScenes"] = untitledScenes,
                        ["suggestedAction"] = "save_scene_with_user_chosen_path",
                    });
            }

            var dirtyScenes = GetDirtySavedScenes();
            if (dirtyScenes.Count > 0)
            {
                var sceneSnapshot = CaptureLoadedSceneState();
                await McpTrustPolicy.AuthorizeOnMainThreadAsync(
                    ctx,
                    ToolTrustCategory.UnsavedWork,
                    "AI requests to save scenes and run tests",
                    dirtyScenes.ToString(),
                    ctx.Arguments.Value<string>("reason") ?? "Save the listed scenes, then run the requested Unity tests.",
                    ct,
                    () => !EditorApplication.isPlaying
                        && !EditorApplication.isPlayingOrWillChangePlaymode
                        && string.Equals(sceneSnapshot, CaptureLoadedSceneState(), StringComparison.Ordinal));

                EnsureNoActiveTestRun();
                var savedScenes = SaveDirtyScenes();
                if (GetDirtySavedScenes().Count > 0)
                    throw new McpToolException(
                        McpErrorCodes.ToolError,
                        "Failed to save all dirty scenes before running tests.");

                UnityEngine.Debug.Log(
                    $"● [MCP] tests.run saved {savedScenes.Count} dirty scene(s) before request {ctx.RequestId}.");
                return savedScenes;
            }

            return new JArray();
        }

        private static ValueTask<ToolResult> RunEditModeTests(TestRunnerApi api, Filter filter, string runId, JObject arguments)
        {

            SessionState.SetString(SceneSnapshotKeyPrefix + runId, CaptureScenes(includeViews: true).ToString());
            var state = CreateRunningState(runId, TestMode.EditMode.ToString());
            SessionState.SetString(SessionKeyPrefix + runId, state.ToString());
            RecordRun(state, arguments);
            AddActiveRunId(runId);
            s_ownedRunIds.Add(runId);
            McpTestProgressOverlay.Show(state);

            var callback = new PersistentRunCallback(runId, api);
            api.RegisterCallbacks(callback);
            s_callbacks[runId] = callback;

            try
            {
                Scene testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                testScene.name = EditModeSceneNamePrefix + runId;
                state["consoleSuppression"] = StartEditModeLogCapture(runId)
                    ? "managed_debug"
                    : "unsupported";
                SessionState.SetString(SessionKeyPrefix + runId, state.ToString());
                StoreUnityRunGuid(runId, api.Execute(new ExecutionSettings(filter)));
            }
            catch (Exception ex)
            {
                state["message"] = $"tests.run failed: {ex.Message}";
                BeginRestoration(runId, state, "failed");
                throw new McpToolException(McpErrorCodes.ToolError, (string)state["message"],
                    new JObject { ["runId"] = runId, ["status"] = "restoring" });
            }

            var response = CreateStartedResponse(runId, TestMode.EditMode.ToString());
            response["consoleSuppression"] = state["consoleSuppression"];
            return new ValueTask<ToolResult>(ToolResult.Ok(response));
        }

        private static ValueTask<ToolResult> RunPlayModeTests(TestRunnerApi api, Filter filter, string runId, JObject arguments)
        {

            // Snapshot the open scene set BEFORE Play Mode begins — Unity swaps in
            // InitTestScene during the run, and its internal restore on RunFinished
            // is known to fail on some versions (see tracker issues). We'll restore
            // ourselves on RunFinished and on post-reload reattach as a fallback.
            SessionState.SetString(SceneSnapshotKeyPrefix + runId, CaptureScenes(includeViews: true).ToString());

            // Store initial state in SessionState (survives domain reload)
            var state = CreateRunningState(runId, TestMode.PlayMode.ToString());
            SessionState.SetString(SessionKeyPrefix + runId, state.ToString());
            RecordRun(state, arguments);
            AddActiveRunId(runId);
            s_ownedRunIds.Add(runId);
            McpTestProgressOverlay.Show(state);

            // Register persistent callback
            var callback = new PersistentRunCallback(runId, api);
            api.RegisterCallbacks(callback);
            s_callbacks[runId] = callback;

            var settings = new ExecutionSettings(filter);
            try { StoreUnityRunGuid(runId, api.Execute(settings)); }
            catch (Exception ex)
            {
                state["message"] = $"tests.run failed: {ex.Message}";
                BeginRestoration(runId, state, "failed");
                throw new McpToolException(McpErrorCodes.ToolError, (string)state["message"],
                    new JObject { ["runId"] = runId, ["status"] = "restoring" });
            }

            // Return immediately — domain reload will happen, client polls tests.result
            return new ValueTask<ToolResult>(ToolResult.Ok(CreateStartedResponse(runId, TestMode.PlayMode.ToString())));
        }

        internal static string ResolveRunId(JObject arguments)
        {
            var runId = (string)arguments["runId"];
            if (runId == null) return Guid.NewGuid().ToString("N");
            if (runId.Length != 32 || runId.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new McpToolException(McpErrorCodes.InvalidParams, "runId must contain exactly 32 lowercase hexadecimal characters.");
            if (!string.IsNullOrEmpty(SessionState.GetString(SessionKeyPrefix + runId, "")))
                throw new McpToolException(McpErrorCodes.Conflict,
                    $"Test run '{runId}' already exists. Poll tests.result with this runId; use a new ID only for an intentional new run.",
                    new JObject { ["runId"] = runId, ["recoveryTool"] = "tests.result" });
            return runId;
        }

        internal static JArray ReadRecentRuns()
            => JArray.Parse(SessionState.GetString(RecentRunsKey, "[]"));

        internal static void RecordRun(JObject state, JObject arguments)
        {
            var selection = new JObject();
            CopyIfPresent(arguments, selection, "testNames", "groupNames", "categoryNames", "assemblyNames");
            var summary = selection.ToString(Newtonsoft.Json.Formatting.None);
            var runs = ReadRecentRuns();
            runs.Insert(0, new JObject
            {
                ["runId"] = state["runId"],
                ["mode"] = state["mode"],
                ["startedAt"] = state["startedAt"],
                ["filterSummary"] = summary.Length > 1000 ? summary[..1000] : summary,
                ["filterSummaryTruncated"] = summary.Length > 1000,
            });
            while (runs.Count > RecentRunLimit) runs.RemoveAt(runs.Count - 1);
            SessionState.SetString(RecentRunsKey, runs.ToString(Newtonsoft.Json.Formatting.None));
        }

        internal static JObject CreateRunningState(string runId, string mode)
        {
            return new JObject
            {
                ["runId"] = runId,
                ["status"] = "running",
                ["mode"] = mode,
                ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["tests"] = new JArray(),
            };
        }

        internal static JObject CreateStartedResponse(string runId, string mode)
        {
            return new JObject
            {
                ["runId"] = runId,
                ["status"] = "started",
                ["mode"] = mode,
                ["message"] = $"{mode} test run started. Poll tests.result with this runId.",
                ["pollAfterMs"] = 500,
            };
        }

        // ─── Result polling ─────────────────────────────────────

        private static ValueTask<ToolResult> GetResult(ToolContext ctx, CancellationToken ct)
        {
            var runId = (string)ctx.Arguments["runId"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "runId required.");

            var json = SessionState.GetString(SessionKeyPrefix + runId, "");
            if (string.IsNullOrEmpty(json))
                throw new McpToolException(McpErrorCodes.NotFound, $"No test run found with id '{runId}'.");

            var state = JObject.Parse(json);
            var detail = ctx.Arguments["detail"]?.Value<string>() ?? "auto";
            var offset = ctx.Arguments["offset"]?.Value<int>() ?? 0;
            var limit = ctx.Arguments["limit"]?.Value<int>() ?? 10;
            var maxTextCharacters = ctx.Arguments["maxTextCharacters"]?.Value<int>() ?? 2000;
            var projected = CreateResultResponse(state, detail, offset, limit, maxTextCharacters);
            var revision = UnityEngine.Hash128.Compute(json).ToString();
            if ((string)ctx.Arguments["afterRevision"] == revision)
                return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["runId"] = runId, ["status"] = state["status"], ["revision"] = revision, ["unchanged"] = true }));
            projected["revision"] = revision;
            return new ValueTask<ToolResult>(ToolResult.Ok(projected));
        }

        internal static JObject CreateResultResponse(
            JObject state,
            string detail,
            int offset,
            int limit,
            int maxTextCharacters = 2000)
        {
            if (detail is not ("auto" or "summary" or "failures" or "diagnostics" or "all"))
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "detail must be auto, summary, failures, diagnostics, or all.");
            if (offset < 0 || limit is < 1 or > 100)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "offset must be >= 0 and limit must be between 1 and 100.");
            if (maxTextCharacters is < 256 or > 20000)
                throw new McpToolException(McpErrorCodes.InvalidParams,
                    "maxTextCharacters must be between 256 and 20000.");

            var tests = state["tests"] as JArray ?? new JArray();
            var status = (string)state["status"] ?? "unknown";
            var running = status is "running" or "cancelling" or "restoring";
            var result = new JObject
            {
                ["runId"] = state["runId"],
                ["status"] = status,
                ["mode"] = state["mode"],
                ["startedAt"] = state["startedAt"],
                ["completedCount"] = tests.Count,
                ["passCount"] = state["passCount"] ?? CountStatus(tests, "Passed"),
                ["failCount"] = state["failCount"] ?? CountStatus(tests, "Failed"),
                ["skipCount"] = state["skipCount"] ?? CountStatus(tests, "Skipped"),
                ["inconclusiveCount"] = state["inconclusiveCount"] ?? CountStatus(tests, "Inconclusive"),
            };
            CopyIfPresent(state, result, "finishedAt", "assertCount", "durationSec", "overallStatus", "message", "consoleSuppression", "sceneRestoreError", "sceneSnapshot", "testsFinishedAt");
            if (running)
                result["pollAfterMs"] = 500;

            var effectiveDetail = detail == "auto" ? running ? "summary" : "failures" : detail;
            result["detail"] = effectiveDetail;
            if (effectiveDetail == "summary")
                return result;

            var selected = tests.OfType<JObject>()
                .Where(test => effectiveDetail == "all" || string.Equals((string)test["status"], "Failed", StringComparison.Ordinal))
                .ToList();
            var page = new JArray(selected.Skip(offset).Take(limit)
                .Select(test => ProjectTestResult(test, effectiveDetail, maxTextCharacters)));
            result["totalMatching"] = selected.Count;
            result["offset"] = offset;
            result["returned"] = page.Count;
            result["truncated"] = offset + page.Count < selected.Count;
            if (offset + page.Count < selected.Count)
                result["nextOffset"] = offset + page.Count;
            if (page.Count > 0)
                result["tests"] = page;
            return result;
        }

        private static JObject ProjectTestResult(JObject test, string detail, int maxTextCharacters)
        {
            var projected = new JObject();
            CopyIfPresent(test, projected, "fullName", "status", "resultState", "durationSec");

            if (detail == "failures")
            {
                CopyBoundedText(test, projected, "message", Math.Min(maxTextCharacters, 500));
                return projected;
            }

            CopyBoundedText(test, projected, "message", maxTextCharacters);
            CopyBoundedText(test, projected, "stackTrace", maxTextCharacters);
            CopyBoundedText(test, projected, "output", maxTextCharacters);
            return projected;
        }

        private static void CopyBoundedText(JObject source, JObject destination, string name, int maxCharacters)
        {
            var value = source[name]?.Value<string>();
            if (string.IsNullOrEmpty(value)) return;

            destination[name] = value.Length <= maxCharacters ? value : value[..maxCharacters];
            if (value.Length <= maxCharacters) return;
            destination[name + "Truncated"] = true;
            destination[name + "Characters"] = value.Length;
        }

        private static int CountStatus(JArray tests, string status)
            => tests.OfType<JObject>().Count(test => string.Equals((string)test["status"], status, StringComparison.Ordinal));

        private static void CopyIfPresent(JObject source, JObject destination, params string[] names)
        {
            foreach (var name in names)
                if (source[name] != null)
                    destination[name] = source[name].DeepClone();
        }

        private static ValueTask<ToolResult> CancelRun(ToolContext ctx, CancellationToken ct)
        {
            var runId = (string)ctx.Arguments["runId"]
                ?? throw new McpToolException(McpErrorCodes.InvalidParams, "runId required.");
            return new ValueTask<ToolResult>(ToolResult.Ok(CreateResultResponse(RequestCancellation(runId), "auto", 0, 25)));
        }

        // Shared by the MCP endpoint and the user-initiated overlay button.
        internal static JObject RequestCancellation(string runId)
        {
            var key = SessionKeyPrefix + runId;
            var json = SessionState.GetString(key, "");
            if (string.IsNullOrEmpty(json))
                throw new McpToolException(McpErrorCodes.NotFound, $"No test run found with id '{runId}'.");
            var state = JObject.Parse(json);
            var status = (string)state["status"];
            if (status != "running") return state;
            var unityRunGuid = (string)state["unityRunGuid"];
            var requested = !string.IsNullOrEmpty(unityRunGuid) && TestRunnerApi.CancelTestRun(unityRunGuid);
            state["status"] = "cancelling";
            SessionState.SetString(key, state.ToString());
            if (!requested) BeginRestoration(runId, state, "cancelled");
            McpTestProgressOverlay.Publish(state);
            return state;
        }

        private static void StoreUnityRunGuid(string runId, string unityRunGuid)
        {
            var key = SessionKeyPrefix + runId;
            var state = JObject.Parse(SessionState.GetString(key, "{}"));
            state["unityRunGuid"] = unityRunGuid;
            SessionState.SetString(key, state.ToString());
            McpTestProgressOverlay.Publish(state);
            ScheduleRunWatchdog(runId);
        }

        private static bool IsFrameworkRunActive()
        {
            if (s_isRunActive == null)
                throw new InvalidOperationException("This Unity Test Framework does not expose IsRunActive; scene cleanup cannot be verified.");
            return s_isRunActive.Invoke(null, null) is true;
        }

        private static void BeginRestoration(string runId, JObject state, string terminalStatus)
        {
            state["terminalStatus"] = terminalStatus;
            state["status"] = "restoring";
            state["testsFinishedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SessionState.SetString(SessionKeyPrefix + runId, state.ToString());
            McpTestProgressOverlay.Publish(state);
            StopEditModeLogCapture(runId);
            ScheduleRunWatchdog(runId);
        }

        private static void ScheduleRunWatchdog(string runId)
        {
            if (s_watchdogs.ContainsKey(runId)) return;
            var checkAt = EditorApplication.timeSinceStartup + 0.5;
            var observedInactive = false;
            void Check()
            {
                if (EditorApplication.timeSinceStartup < checkAt) return;
                checkAt = EditorApplication.timeSinceStartup + 0.5;
                try
                {
                    PollRun(runId, EditorApplication.isCompiling || EditorApplication.isUpdating
                        || EditorApplication.isPlayingOrWillChangePlaymode || IsFrameworkRunActive(), ref observedInactive);
                }
                catch (Exception ex)
                {
                    var json = SessionState.GetString(SessionKeyPrefix + runId, "{}");
                    RecordRestoreFailure(runId, JObject.Parse(json), ex);
                }
            }
            s_watchdogs.Add(runId, Check);
            EditorApplication.update += Check;
        }

        internal static void PollRun(string runId, bool frameworkBusy, ref bool observedInactive)
        {
            // Do not deserialize the growing results array while tests are still running.
            // Keep the update subscription until the runner and its cleanup have settled.
            if (frameworkBusy)
            {
                observedInactive = false;
                return;
            }
            var json = SessionState.GetString(SessionKeyPrefix + runId, "");
            if (string.IsNullOrEmpty(json))
            {
                RemoveActiveRunId(runId);
                return;
            }
            var state = JObject.Parse(json);
            var status = (string)state["status"];
            if (status == "running"
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)state["startedAt"] < 5000)
                return;
            if (!observedInactive)
            {
                observedInactive = true;
                return;
            }
            if (status is "running" or "cancelling")
            {
                if (status == "running")
                    state["message"] = "Unity TestRunner ended before reporting results. Check the filter and Editor logs.";
                BeginRestoration(runId, state, status == "cancelling" ? "cancelled" : "failed");
            }
            CompleteRestoration(runId, state);
        }

        internal static void CompleteRestoration(string runId, JObject state)
        {
            var key = SceneSnapshotKeyPrefix + runId;
            var json = SessionState.GetString(key, "");
            try
            {
                if (!string.IsNullOrEmpty(json)) RestoreScenesNow(JObject.Parse(json));
                state["status"] = state["terminalStatus"]?.Value<string>() ?? (string)state["status"];
                state["finishedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SaveHistory(state);
                SessionState.SetString(SessionKeyPrefix + runId, state.ToString());
                McpTestProgressOverlay.Publish(state);
                SessionState.EraseString(key);
                RemoveActiveRunId(runId);
            }
            catch (Exception ex)
            {
                RecordRestoreFailure(runId, state, ex);
            }
        }

        private static void SaveHistory(JObject state)
        {
            if (!s_ownedRunIds.Contains((string)state["runId"])) return;
            try
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "Logs", "McpUsage", "test-history.json"));
                LittleBrushGames.Mcp.Editor.Diagnostics.McpTestHistory.Record(path, state, UnityEngine.Application.unityVersion);
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("MCP test history was not saved: " + ex.Message); }
        }

        private static void RecordRestoreFailure(string runId, JObject state, Exception ex)
        {
            state["status"] = "failed";
            state["sceneRestoreError"] = ex.Message;
            state["sceneSnapshot"] = SessionState.GetString(SceneSnapshotKeyPrefix + runId, "");
            state["finishedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SaveHistory(state);
            SessionState.SetString(SessionKeyPrefix + runId, state.ToString());
            McpTestProgressOverlay.Publish(state);
            RemoveActiveRunId(runId);
        }

        // ─── Persistent callback (survives domain reload) ───────

        private sealed class PersistentRunCallback : ICallbacks
        {
            private readonly string _runId;
            private readonly TestRunnerApi _api;

            public PersistentRunCallback(string runId, TestRunnerApi api)
            {
                _runId = runId;
                _api = api;
            }

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test)
            {
                if (test != null && !test.IsSuite && !test.IsTestAssembly)
                    McpTestProgressOverlay.TestStarted(_runId, test.FullName);
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result?.Test == null || result.Test.IsSuite || result.Test.IsTestAssembly) return;

                var json = SessionState.GetString(SessionKeyPrefix + _runId, "");
                if (string.IsNullOrEmpty(json))
                {
                    StopEditModeLogCapture(_runId);
                    RemoveActiveRunId(_runId);
                    UnregisterSelf();
                    return;
                }

                var state = JObject.Parse(json);
                var tests = (JArray)state["tests"];
                var test = new JObject
                {
                    ["fullName"] = result.Test.FullName,
                    ["status"] = result.TestStatus.ToString(),
                    ["resultState"] = result.ResultState,
                    ["durationSec"] = result.Duration,
                };
                if (!string.IsNullOrEmpty(result.Message)) test["message"] = result.Message;
                if (!string.IsNullOrEmpty(result.StackTrace)) test["stackTrace"] = result.StackTrace;
                if (!string.IsNullOrEmpty(result.Output)) test["output"] = result.Output;
                tests.Add(test);
                SessionState.SetString(SessionKeyPrefix + _runId, state.ToString());
                McpTestProgressOverlay.TestFinished(_runId, result.TestStatus == UnityEditor.TestTools.TestRunner.Api.TestStatus.Failed);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    var json = SessionState.GetString(SessionKeyPrefix + _runId, "");
                    if (string.IsNullOrEmpty(json))
                    {
                        RemoveActiveRunId(_runId);
                        return;
                    }

                    var state = JObject.Parse(json);
                    var noTests = state["tests"] is JArray tests && tests.Count == 0;
                    var terminalStatus = state["terminalStatus"]?.Value<string>()
                        ?? ((string)state["status"] == "cancelling" ? "cancelled" : noTests ? "failed" : "completed");
                    if (noTests && terminalStatus == "failed" && state["message"] == null)
                        state["message"] = "No tests matched the requested filters.";
                    state["passCount"] = result?.PassCount ?? 0;
                    state["failCount"] = result?.FailCount ?? 0;
                    state["skipCount"] = result?.SkipCount ?? 0;
                    state["inconclusiveCount"] = result?.InconclusiveCount ?? 0;
                    state["assertCount"] = result?.AssertCount ?? 0;
                    state["durationSec"] = result?.Duration ?? 0;
                    state["overallStatus"] = result?.TestStatus.ToString();
                    BeginRestoration(_runId, state, terminalStatus);
                }
                finally
                {
                    StopEditModeLogCapture(_runId);
                }
            }

            internal void UnregisterSelf()
            {
                try { _api?.UnregisterCallbacks(this); }
                catch { }
                if (_api != null) UnityEngine.Object.DestroyImmediate(_api);
            }
        }

        // ─── Active run tracking ────────────────────────────────

        private static void ReattachPendingRuns()
        {
            EditorApplication.update -= ReattachPendingRuns;
            var ids = SessionState.GetString(ActiveRunsKey, "");
            if (string.IsNullOrEmpty(ids)) return;

            foreach (var runId in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (s_ownedRunIds.Contains(runId))
                    continue;
                var json = SessionState.GetString(SessionKeyPrefix + runId, "");
                if (string.IsNullOrEmpty(json))
                {
                    RemoveActiveRunId(runId);
                    continue;
                }

                var state = JObject.Parse(json);
                var status = (string)state["status"];

                if (status is "running" or "cancelling")
                {
                    var api = UnityEngine.ScriptableObject.CreateInstance<TestRunnerApi>();
                    var callback = new PersistentRunCallback(runId, api);
                    api.RegisterCallbacks(callback);
                    s_callbacks[runId] = callback;
                }
                s_ownedRunIds.Add(runId);
                ScheduleRunWatchdog(runId);
            }
        }

        private static void AddActiveRunId(string runId)
        {
            var current = SessionState.GetString(ActiveRunsKey, "");
            var ids = string.IsNullOrEmpty(current) ? runId : current + "," + runId;
            SessionState.SetString(ActiveRunsKey, ids);
            s_hasActiveRun = true;
        }

        internal static string GetActiveRunId()
        {
            var current = SessionState.GetString(ActiveRunsKey, "");
            foreach (var runId in current.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var json = SessionState.GetString(SessionKeyPrefix + runId, "");
                if (!string.IsNullOrEmpty(json))
                {
                    var status = (string)JObject.Parse(json)["status"];
                    if (status is "running" or "cancelling" or "restoring")
                        return runId;
                }
            }
            return null;
        }

        private static void RemoveActiveRunId(string runId)
        {
            s_ownedRunIds.Remove(runId);
            if (s_watchdogs.Remove(runId, out var watchdog)) EditorApplication.update -= watchdog;
            if (s_callbacks.Remove(runId, out var callback)) callback.UnregisterSelf();
            StopEditModeLogCapture(runId);
            var current = SessionState.GetString(ActiveRunsKey, "");
            if (string.IsNullOrEmpty(current))
            {
                s_hasActiveRun = false;
                return;
            }
            var parts = current.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var filtered = Array.FindAll(parts, p => p != runId);
            SessionState.SetString(ActiveRunsKey, string.Join(",", filtered));
            s_hasActiveRun = filtered.Length > 0;
        }

        private static bool StartEditModeLogCapture(string runId)
        {
            var capture = McpTestLogCapture.TryInstall();
            if (capture == null) return false;
            s_editModeLogCapture = capture;
            s_editModeLogCaptureRunId = runId;
            return true;
        }

        private static void StopEditModeLogCapture(string runId)
        {
            if (!string.Equals(s_editModeLogCaptureRunId, runId, StringComparison.Ordinal))
                return;
            s_editModeLogCapture?.Dispose();
            s_editModeLogCapture = null;
            s_editModeLogCaptureRunId = null;
        }

        // ─── Helpers ────────────────────────────────────────────

        private static TestMode? ParseMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return null;
            return mode.ToLowerInvariant() switch
            {
                "editmode" => TestMode.EditMode,
                "playmode" => TestMode.PlayMode,
                "all" => TestMode.EditMode | TestMode.PlayMode,
                _ => null,
            };
        }

        private static string[] ToStringArray(JToken token)
        {
            if (token is not JArray arr || arr.Count == 0) return null;
            var result = new string[arr.Count];
            for (var i = 0; i < arr.Count; i++) result[i] = (string)arr[i];
            return result;
        }

        // ─── Scene preservation ─────────────────────────────────

        private static JArray GetUntitledLoadedScenes()
        {
            var scenes = new JArray();
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !string.IsNullOrEmpty(scene.path)) continue;
                scenes.Add(new JObject
                {
                    ["name"] = string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name,
                });
            }
            return scenes;
        }

        private static JArray GetDirtySavedScenes()
        {
            var scenes = new JArray();
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isDirty || string.IsNullOrEmpty(scene.path)) continue;
                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                });
            }
            return scenes;
        }

        private static JArray SaveDirtyScenes()
        {
            var saved = new JArray();
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isDirty) continue;
                if (string.IsNullOrEmpty(scene.path))
                    throw new McpToolException(
                        McpErrorCodes.Conflict,
                        "A loaded scene became untitled before tests.run could save it. Choose a path and retry.");
                if (!EditorSceneManager.SaveScene(scene))
                    throw new McpToolException(
                        McpErrorCodes.ToolError,
                        $"Failed to save dirty scene '{scene.path}' before running tests.");
                saved.Add(scene.path);
            }
            return saved;
        }

        private static string CaptureLoadedSceneState()
        {
            var scenes = new List<string> { $"count:{EditorSceneManager.sceneCount}" };
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                scenes.Add($"{i}:{scene.path}:{scene.name}:{scene.isLoaded}:{scene.isDirty}");
            }
            var active = SceneManager.GetActiveScene();
            scenes.Add($"active:{active.path}:{active.name}");
            return string.Join("|", scenes);
        }

        /// <summary>Serialize the current open-scene set so we can restore after the run.</summary>
        private static JObject CaptureScenes(bool includeViews = false)
        {
            var arr = new JArray();
            var active = SceneManager.GetActiveScene();
            int count = EditorSceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (!s.IsValid() || string.IsNullOrEmpty(s.path)) continue;
                arr.Add(new JObject
                {
                    ["path"] = s.path,
                    ["isLoaded"] = s.isLoaded,
                    ["isActive"] = s.path == active.path,
                });
            }
            var snapshot = new JObject { ["scenes"] = arr };
            if (includeViews) snapshot["sceneViews"] = CaptureSceneViews();
            return snapshot;
        }

        internal static JArray CaptureSceneViews(IEnumerable<SceneView> views = null)
        {
            var result = new JArray();
            foreach (var view in views ?? SceneView.sceneViews.Cast<SceneView>())
            {
                if (view == null) continue;
                var pivot = view.pivot;
                var rotation = view.rotation;
                result.Add(new JObject
                {
                    ["id"] = UnitySerializer.ToEntityIdString(view),
                    ["pivot"] = new JArray(pivot.x, pivot.y, pivot.z),
                    ["rotation"] = new JArray(rotation.x, rotation.y, rotation.z, rotation.w),
                    ["size"] = view.size,
                    ["orthographic"] = view.orthographic,
                    ["in2DMode"] = view.in2DMode,
                });
            }
            return result;
        }

        internal static void RestoreSceneViews(JArray snapshot, IEnumerable<SceneView> views = null)
        {
            if (snapshot == null) return; // Runs started before this version have no view snapshot.
            foreach (var view in views ?? SceneView.sceneViews.Cast<SceneView>())
            {
                if (view == null) continue;
                var state = snapshot.FirstOrDefault(entry =>
                    (string)entry["id"] == UnitySerializer.ToEntityIdString(view));
                if (state == null) continue; // Do not create closed windows or alter new ones.
                var pivot = (JArray)state["pivot"];
                var rotation = (JArray)state["rotation"];
                view.in2DMode = (bool)state["in2DMode"];
                view.orthographic = (bool)state["orthographic"];
                view.LookAtDirect(
                    new UnityEngine.Vector3((float)pivot[0], (float)pivot[1], (float)pivot[2]),
                    new UnityEngine.Quaternion((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]),
                    (float)state["size"]);
                view.Repaint();
            }
        }

        private static void RestoreScenesNow(JObject snapshot)
        {
            if (snapshot?["scenes"] is not JArray scenes)
                throw new InvalidOperationException("Invalid scene snapshot; restore the original scenes manually.");
            if (scenes.Count == 0)
            {
                RestoreSceneViews(snapshot["sceneViews"] as JArray);
                return;
            }
            if (JToken.DeepEquals(scenes, CaptureScenes()["scenes"]))
            {
                RestoreSceneViews(snapshot["sceneViews"] as JArray);
                return;
            }

            var active = SceneManager.GetActiveScene();
            if (!LooksLikeTestInfraScene(active.path, active.name))
                throw new InvalidOperationException("Scene setup changed outside the test runner. Original scene snapshot retained; current scenes were left untouched.");
            if (GetDirtySavedScenes().Count > 0)
                throw new InvalidOperationException("A saved scene has unsaved changes. Save it before restoring the original scene setup.");

            var setup = scenes.Select(entry => new SceneSetup
            {
                path = (string)entry["path"],
                isLoaded = (bool?)entry["isLoaded"] ?? true,
                isActive = (bool?)entry["isActive"] ?? false,
            }).ToArray();
            // Validate all paths before Unity closes any current scenes.
            foreach (var scene in setup)
                if (string.IsNullOrEmpty(scene.path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) == null)
                    throw new InvalidOperationException($"Cannot restore missing scene '{scene.path}'. Original scene snapshot retained.");
            EditorSceneManager.RestoreSceneManagerSetup(setup);
            if (!JToken.DeepEquals(scenes, CaptureScenes()["scenes"]))
                throw new InvalidOperationException("Unity did not restore the complete scene setup. Original scene snapshot retained.");
            RestoreSceneViews(snapshot["sceneViews"] as JArray);
        }

        /// <summary>
        /// Known scene names/paths that Unity's TestRunner uses while a run is in
        /// progress. Anchor the decision on these patterns so we don't clobber a
        /// user-authored scene that just happens to have an empty path.
        /// </summary>
        private static bool LooksLikeTestInfraScene(string path, string name)
        {
            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(name)) return true;

            if (!string.IsNullOrEmpty(name))
            {
                if (name.StartsWith("InitTestScene", StringComparison.Ordinal)) return true;
                if (name.StartsWith("Temp_TestScene", StringComparison.Ordinal)) return true;
                if (name.StartsWith(EditModeSceneNamePrefix, StringComparison.Ordinal)) return true;
            }

            if (!string.IsNullOrEmpty(path))
            {
                if (path.Contains("InitTestScene", StringComparison.Ordinal)) return true;
                if (path.Contains("Temp_TestScene", StringComparison.Ordinal)) return true;
                if (path.Contains("/Resources/Test/", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

    }
}
#endif
