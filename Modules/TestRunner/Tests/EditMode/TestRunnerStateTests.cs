#if UNITY_TESTS_FRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace LittleBrushGames.Mcp.Modules.TestRunner.Tests
{
    public sealed class TestRunnerStateTests
    {
        [TestCase("running", true, "Running tests…")]
        [TestCase("cancelling", false, "Cancelling tests…")]
        [TestCase("restoring", false, "Restoring scenes…")]
        public void ProgressPanel_ReflectsLifecycleAndOnlyAllowsSafeCancellation(string status, bool canCancel, string phase)
        {
            var state = new TestRunProgressState
            {
                RunId = "view-test", Status = status, Mode = "EditMode", CurrentTest = "Example.Test",
                StartedAt = 1000, CanCancel = true, Completed = 18,
            };
            var view = new TestRunProgressView(() => { });
            view.UpdateState(state, 4000);
            Assert.That(view.Q<Label>("phase").text, Is.EqualTo(phase));
            Assert.That(view.Q<Button>("cancel").enabledSelf, Is.EqualTo(canCancel));
            Assert.That(view.Q<Button>("cancel").focusable, Is.True);
            Assert.That(view.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(view.Q<Button>("dismiss"), Is.Null);
            Assert.That(view.Q<Label>("counts").text, Does.Contain("18 completed"));
        }

        [TestCase("completed", 0)]
        [TestCase("completed", 1)]
        [TestCase("failed", 1)]
        [TestCase("cancelled", 0)]
        public void ProgressPanel_HidesImmediatelyForEveryFinalOutcome(string status, int failed)
        {
            var state = new TestRunProgressState { RunId = "view-test", Status = "running", CanCancel = true };
            var view = new TestRunProgressView(() => { });
            view.UpdateState(state, 1000);
            Assert.That(view.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            state.Status = status;
            state.Failed = failed;
            view.UpdateState(state, 1000);
            Assert.That(view.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(view.Q<Button>("dismiss"), Is.Null);
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator ProgressPanel_LayoutAndPaletteStayStableOffscreen()
        {
            var pumpPanels = typeof(LittleBrushGames.Mcp.Editor.Providers.UiPreviewProvider)
                .GetMethod("PumpRuntimePanels", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(pumpPanels, Is.Not.Null);
            var target = new RenderTexture(800, 600, 0) { hideFlags = HideFlags.HideAndDontSave };
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject host = null;
            try
            {
                Assert.That(target.Create(), Is.True);
                settings.hideFlags = HideFlags.HideAndDontSave;
                settings.targetTexture = target;
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                settings.scale = 1;
                host = EditorUtility.CreateGameObjectWithHideFlags("MCP panel layout test", HideFlags.HideAndDontSave, typeof(UIDocument));
                var document = host.GetComponent<UIDocument>();
                document.panelSettings = settings;
                var root = document.rootVisualElement;
                root.style.width = 800;
                root.style.height = 600;
                root.style.unityFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                var view = new TestRunProgressView(() => { });
                root.Add(view);
                var state = new TestRunProgressState { RunId = "layout", Status = "running", CanCancel = true, CurrentTest = "Short" };
                view.UpdateState(state, 1000);
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
                yield return null;
                pumpPanels.Invoke(null, null);
                var card = view.Q<ScrollView>("card");
                var bounds = card.worldBound;
                var background = card.style.backgroundColor.value;
                Assert.That(bounds.width, Is.EqualTo(440).Within(0.1));
                Assert.That(bounds.height, Is.EqualTo(280).Within(0.1));
                Assert.That(background.r, Is.LessThan(0.2f));
                foreach (var phase in new[] { "running", "cancelling", "restoring" })
                {
                    state.Status = phase;
                    state.CurrentTest = new string('W', 512);
                    state.Error = new string('E', 512);
                    state.Failed = 12;
                    state.Revision++;
                    view.UpdateState(state, 2000);
                    EditorApplication.QueuePlayerLoopUpdate();
                    yield return null;
                    pumpPanels.Invoke(null, null);
                    Assert.That(card.worldBound, Is.EqualTo(bounds), "Changing progress must not move or resize the card.");
                    Assert.That(card.style.backgroundColor.value, Is.EqualTo(background));
                    Assert.That(view.Q<Label>("current-test").layout.height, Is.EqualTo(32).Within(0.1));
                }
                root.style.width = 320;
                root.style.height = 280;
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
                yield return null;
                pumpPanels.Invoke(null, null);
                var narrowBounds = card.worldBound;
                Assert.That(narrowBounds.width, Is.LessThanOrEqualTo(296.1f));
                Assert.That(narrowBounds.height, Is.LessThanOrEqualTo(256.1f));
                state.CurrentTest = "Short";
                state.Error = null;
                state.Revision++;
                view.UpdateState(state, 3000);
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
                pumpPanels.Invoke(null, null);
                Assert.That(card.worldBound, Is.EqualTo(narrowBounds));
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(settings);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void ProgressPanel_PreservesCompactStateAcrossReloadAndDisplaysFailure()
        {
            var state = new TestRunProgressState
            {
                RunId = "reload-test", Status = "restoring", Mode = "PlayMode", Completed = 62,
                Failed = 1, StartedAt = 1000, CurrentTest = "Example.Test", Error = "Original scene could not be opened.",
            };
            var restored = JsonUtility.FromJson<TestRunProgressState>(JsonUtility.ToJson(state));
            Assert.That(restored.RunId, Is.EqualTo(state.RunId));
            Assert.That(restored.Completed, Is.EqualTo(62));
            Assert.That(restored.CurrentTest, Is.EqualTo(state.CurrentTest));
            var view = new TestRunProgressView(() => { });
            view.UpdateState(restored, 66000);
            Assert.That(view.Q<Label>("hint").text, Is.EqualTo(state.Error));
            Assert.That(view.Q<Label>("elapsed").text, Does.Contain("01:05"));
            Assert.That(view.Q<ScrollView>("card").style.backgroundColor.value.a, Is.LessThan(1));
            Assert.That(view.pickingMode, Is.EqualTo(PickingMode.Ignore));
        }

        [Test]
        public void CreateRunningState_StoresRunIdAndMode()
        {
            var state = TestRunnerProvider.CreateRunningState("abc123", "EditMode");

            Assert.That((string)state["runId"], Is.EqualTo("abc123"));
            Assert.That((string)state["status"], Is.EqualTo("running"));
            Assert.That((string)state["mode"], Is.EqualTo("EditMode"));
            Assert.That(state["tests"], Is.TypeOf<JArray>());
        }

        [Test]
        public void CreateStartedResponse_ReturnsPollableEnvelope()
        {
            var response = TestRunnerProvider.CreateStartedResponse("abc123", "EditMode");

            Assert.That((string)response["runId"], Is.EqualTo("abc123"));
            Assert.That((string)response["status"], Is.EqualTo("started"));
            Assert.That((string)response["mode"], Is.EqualTo("EditMode"));
            Assert.That((string)response["message"], Does.Contain("Poll tests.result"));
            Assert.That((int)response["pollAfterMs"], Is.EqualTo(500));
        }

        [Test]
        public void McpTestLogCapture_ForwardsFormattedLogWithoutUsingFallback()
        {
            var fallback = new RecordingLogHandler();
            string message = null;
            LogType? type = null;
            bool? invokedOnMainThread = null;
            var capture = new McpTestLogCapture(
                fallback,
                (value, _, logType, onMainThread) =>
                {
                    message = value;
                    type = logType;
                    invokedOnMainThread = onMainThread;
                },
                Thread.CurrentThread.ManagedThreadId);

            capture.LogFormat(LogType.Warning, null, "Expected {0}", "warning");

            Assert.That(message, Is.EqualTo("Expected warning"));
            Assert.That(type, Is.EqualTo(LogType.Warning));
            Assert.That(invokedOnMainThread, Is.True);
            Assert.That(fallback.FormatCalls, Is.Zero);
        }

        [Test]
        public void McpTestLogCapture_FallsBackWhenManagedCallbackFails()
        {
            var fallback = new RecordingLogHandler();
            var capture = new McpTestLogCapture(
                fallback,
                (_, _, _, _) => throw new InvalidOperationException("callback failed"),
                Thread.CurrentThread.ManagedThreadId);

            capture.LogFormat(LogType.Error, null, "Error {0}", 42);

            Assert.That(fallback.FormatCalls, Is.EqualTo(1));
            Assert.That(fallback.LastFormat, Is.EqualTo("Error {0}"));
        }

        [Test]
        public void CreateListResponse_FiltersSortsAndPages()
        {
            var entries = new JArray
            {
                TestEntry("Game.Tests.Zeta", "Game.Tests.EditMode", "Slow"),
                TestEntry("Game.Tests.Alpha", "Game.Tests.EditMode", "Fast"),
                TestEntry("Plugin.Tests.Alpha", "Plugin.Tests", "Fast"),
            };
            var result = TestRunnerProvider.CreateListResponse(entries, new JObject(), new JObject
            {
                ["query"] = "Alpha",
                ["assemblyNames"] = new JArray("Game.Tests.EditMode"),
                ["detail"] = "metadata",
                ["limit"] = 1,
            });

            Assert.That((int)result["total"], Is.EqualTo(3));
            Assert.That((int)result["filtered"], Is.EqualTo(1));
            Assert.That((int)result["returned"], Is.EqualTo(1));
            Assert.That((string)result["tests"][0]["fullName"], Is.EqualTo("Game.Tests.Alpha"));
            Assert.That(result["nextOffset"], Is.Null);

            var compact = TestRunnerProvider.CreateListResponse(entries, new JObject(), new JObject());
            Assert.That((string)compact["detail"], Is.EqualTo("names"));
            Assert.That(compact["tests"][0].Type, Is.EqualTo(JTokenType.String));
        }

        [Test]
        public void CreateResultResponse_AutoReturnsSummaryWhileRunning()
        {
            var state = TestRunnerProvider.CreateRunningState("run", "EditMode");
            ((JArray)state["tests"]).Add(new JObject { ["fullName"] = "Pass", ["status"] = "Passed" });

            var result = TestRunnerProvider.CreateResultResponse(state, "auto", 0, 100);

            Assert.That((string)result["detail"], Is.EqualTo("summary"));
            Assert.That((int)result["completedCount"], Is.EqualTo(1));
            Assert.That((int)result["passCount"], Is.EqualTo(1));
            Assert.That(result["tests"], Is.Null);
        }

        [Test]
        public void CreateResultResponse_AutoReturnsOnlyFailuresWhenComplete()
        {
            var state = TestRunnerProvider.CreateRunningState("run", "EditMode");
            state["status"] = "completed";
            state["passCount"] = 1;
            state["failCount"] = 1;
            var tests = (JArray)state["tests"];
            tests.Add(new JObject { ["fullName"] = "Pass", ["status"] = "Passed" });
            tests.Add(new JObject
            {
                ["fullName"] = "Fail",
                ["status"] = "Failed",
                ["message"] = "boom",
                ["stackTrace"] = "large stack",
                ["output"] = "large output",
            });

            var result = TestRunnerProvider.CreateResultResponse(state, "auto", 0, 100);

            Assert.That((string)result["detail"], Is.EqualTo("failures"));
            Assert.That((int)result["totalMatching"], Is.EqualTo(1));
            Assert.That((string)result["tests"][0]["fullName"], Is.EqualTo("Fail"));
            Assert.That(result["tests"][0]["stackTrace"], Is.Null);
            Assert.That(result["tests"][0]["output"], Is.Null);
        }

        [Test]
        public void CreateResultResponse_DiagnosticsBoundsLargeTextFields()
        {
            var state = TestRunnerProvider.CreateRunningState("run", "EditMode");
            state["status"] = "completed";
            ((JArray)state["tests"]).Add(new JObject
            {
                ["fullName"] = "Fail",
                ["status"] = "Failed",
                ["message"] = new string('m', 400),
                ["stackTrace"] = new string('s', 400),
                ["output"] = new string('o', 400),
            });

            var result = TestRunnerProvider.CreateResultResponse(state, "diagnostics", 0, 10, 256);
            var failure = result["tests"][0];

            Assert.That(((string)failure["message"]).Length, Is.EqualTo(256));
            Assert.That(((string)failure["stackTrace"]).Length, Is.EqualTo(256));
            Assert.That(((string)failure["output"]).Length, Is.EqualTo(256));
            Assert.That((bool)failure["stackTraceTruncated"], Is.True);
            Assert.That((int)failure["stackTraceCharacters"], Is.EqualTo(400));
        }

        [Test]
        public void CreateResultResponse_BoundsFailureDetails()
        {
            var state = TestRunnerProvider.CreateRunningState("run", "EditMode");
            state["status"] = "completed";
            var tests = (JArray)state["tests"];
            for (var i = 0; i < 30; i++)
                tests.Add(new JObject { ["fullName"] = $"Fail{i:00}", ["status"] = "Failed", ["message"] = "boom" });

            var result = TestRunnerProvider.CreateResultResponse(state, "auto", 0, 25);

            Assert.That((int)result["totalMatching"], Is.EqualTo(30));
            Assert.That((int)result["returned"], Is.EqualTo(25));
            Assert.That((bool)result["truncated"], Is.True);
            Assert.That((int)result["nextOffset"], Is.EqualTo(25));
        }

        [Test]
        public void CreateStartedResponse_UsesStartedStatusForEditMode()
        {
            var response = TestRunnerProvider.CreateStartedResponse("run-edit", "EditMode");

            Assert.That((string)response["status"], Is.EqualTo("started"));
            Assert.That((string)response["mode"], Is.EqualTo("EditMode"));
        }

        [Test]
        public void GetActiveRunId_ReturnsRunningRun()
        {
            const string runId = "active-run-test";
            var activeRunsKey = GetPrivateConstString("ActiveRunsKey");
            var sessionKey = GetPrivateConstString("SessionKeyPrefix") + runId;
            var previousIds = SessionState.GetString(activeRunsKey, "");
            SessionState.SetString(activeRunsKey, runId);
            SessionState.SetString(sessionKey, TestRunnerProvider.CreateRunningState(runId, "EditMode").ToString());
            try
            {
                Assert.That(TestRunnerProvider.GetActiveRunId(), Is.EqualTo(runId));
            }
            finally
            {
                SessionState.SetString(activeRunsKey, previousIds);
                SessionState.EraseString(sessionKey);
            }
        }

        [Test]
        public void RegisterTools_ResultRunsOnMainThreadAndRemainsAlwaysAvailable()
        {
            var sink = new CollectingSink();
            new TestRunnerProvider().RegisterTools(sink);

            var descriptor = sink.Tools.Single(tool => tool.Name == "tests.result");
            var cancel = sink.Tools.Single(tool => tool.Name == "tests.cancel");

            Assert.That(descriptor.RequiresMainThread, Is.True);
            Assert.That(descriptor.RequiresWriterLease, Is.False);
            Assert.That(cancel.RequiresWriterLease, Is.True);
            Assert.That(descriptor.Availability, Is.EqualTo(ToolAvailability.Always));
            Assert.That(cancel.RequiresMainThread, Is.True);
            Assert.That(cancel.Availability, Is.EqualTo(ToolAvailability.Always));
        }

        [Test]
        public void RegisterTools_RunAdvertisesSceneIsolation()
        {
            var sink = new CollectingSink();
            new TestRunnerProvider().RegisterTools(sink);

            var run = sink.Tools.Single(tool => tool.Name == "tests.run");

            foreach (var tool in sink.Tools)
                Assert.That(LittleBrushGames.Mcp.Editor.Dispatch.SchemaValidator.ValidateSchema(tool.InputSchema), Is.Empty, tool.Name);
            Assert.That((bool)run.Annotations["destructiveHint"], Is.True);
            Assert.That(run.Description, Does.Contain("saved under the UnsavedWork policy"));
            Assert.That(run.Description, Does.Contain("untitled scenes remain blocked"));
            Assert.That(run.Description, Does.Contain("temporary empty scene"));
        }

        [TestCase("cancelling", null, "cancelled")]
        [TestCase("restoring", "Original startup failure", "failed")]
        public void LateFinish_PreservesCancellationAndStartupFailure(string status, string message, string terminal)
        {
            const string runId = "late-finish-test";
            var key = GetPrivateConstString("SessionKeyPrefix") + runId;
            var snapshotKey = GetPrivateConstString("SceneSnapshotKeyPrefix") + runId;
            var state = TestRunnerProvider.CreateRunningState(runId, "EditMode");
            state["status"] = status;
            if (message != null)
            {
                state["message"] = message;
                state["terminalStatus"] = terminal;
            }
            SessionState.SetString(key, state.ToString());
            SessionState.SetString(snapshotKey, new JObject { ["scenes"] = new JArray() }.ToString());
            try
            {
                var type = typeof(TestRunnerProvider).GetNestedType("PersistentRunCallback", BindingFlags.NonPublic);
                var callback = (UnityEditor.TestTools.TestRunner.Api.ICallbacks)Activator.CreateInstance(type, runId, null);
                callback.RunFinished(null);
                state = JObject.Parse(SessionState.GetString(key, ""));
                Assert.That((string)state["status"], Is.EqualTo("restoring"));
                Assert.That((string)state["terminalStatus"], Is.EqualTo(terminal));
                Assert.That((string)state["message"], Is.EqualTo(message));
            }
            finally
            {
                TestRunnerProvider.CompleteRestoration(runId, JObject.Parse(SessionState.GetString(key, "")));
                SessionState.EraseString(key);
                SessionState.EraseString(snapshotKey);
            }
        }

        [TestCase("running", "failed")]
        [TestCase("cancelling", "cancelled")]
        public void Watchdog_RecoversMissingCallbackAfterLongRunAndWaitsForCleanup(string status, string expected)
        {
            const string runId = "watchdog-recovery-test";
            var key = GetPrivateConstString("SessionKeyPrefix") + runId;
            var snapshotKey = GetPrivateConstString("SceneSnapshotKeyPrefix") + runId;
            var state = TestRunnerProvider.CreateRunningState(runId, "EditMode");
            state["status"] = status;
            state["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60000;
            SessionState.SetString(key, state.ToString());
            SessionState.SetString(snapshotKey, new JObject { ["scenes"] = new JArray() }.ToString());
            var inactive = false;
            try
            {
                TestRunnerProvider.PollRun(runId, true, ref inactive);
                Assert.That((string)JObject.Parse(SessionState.GetString(key, ""))["status"], Is.EqualTo(status));
                TestRunnerProvider.PollRun(runId, false, ref inactive);
                Assert.That(SessionState.GetString(snapshotKey, ""), Is.Not.Empty);
                // A resumed framework must invalidate the previously observed idle tick.
                TestRunnerProvider.PollRun(runId, true, ref inactive);
                Assert.That(inactive, Is.False);
                TestRunnerProvider.PollRun(runId, false, ref inactive);
                Assert.That(SessionState.GetString(snapshotKey, ""), Is.Not.Empty);
                TestRunnerProvider.PollRun(runId, false, ref inactive);
                Assert.That((string)JObject.Parse(SessionState.GetString(key, ""))["status"], Is.EqualTo(expected));
                Assert.That(SessionState.GetString(snapshotKey, ""), Is.Empty);
            }
            finally
            {
                SessionState.EraseString(key);
                SessionState.EraseString(snapshotKey);
            }
        }

        [TestCase("restoring")]
        [TestCase("cancelling")]
        public void Result_RemainsPollableUntilSceneCleanupFinishes(string status)
        {
            var state = TestRunnerProvider.CreateRunningState("pending", "EditMode");
            state["status"] = status;
            var response = TestRunnerProvider.CreateResultResponse(state, "auto", 0, 10);
            Assert.That((string)response["status"], Is.EqualTo(status));
            Assert.That((string)response["detail"], Is.EqualTo("summary"));
            Assert.That((int)response["pollAfterMs"], Is.EqualTo(500));
            Assert.That(response["finishedAt"], Is.Null);
        }

        [TestCase("completed")]
        [TestCase("failed")]
        [TestCase("cancelled")]
        public void CompleteRestoration_PublishesTerminalStateAndClearsSnapshot(string terminal)
        {
            const string runId = "cleanup-state-test";
            var key = GetPrivateConstString("SceneSnapshotKeyPrefix") + runId;
            var stateKey = GetPrivateConstString("SessionKeyPrefix") + runId;
            var state = TestRunnerProvider.CreateRunningState(runId, "EditMode");
            state["status"] = "restoring";
            state["terminalStatus"] = terminal;
            SessionState.SetString(key, new JObject { ["scenes"] = new JArray() }.ToString());
            try
            {
                TestRunnerProvider.CompleteRestoration(runId, state);
                Assert.That((string)state["status"], Is.EqualTo(terminal));
                Assert.That(state["finishedAt"], Is.Not.Null);
                Assert.That(SessionState.GetString(key, ""), Is.Empty);
            }
            finally
            {
                SessionState.EraseString(key);
                SessionState.EraseString(stateKey);
            }
        }

        [TestCase("not-json")]
        [TestCase("{}")]
        public void CompleteRestoration_PreservesInvalidSnapshotAndReportsFailure(string snapshot)
        {
            const string runId = "invalid-cleanup-test";
            var key = GetPrivateConstString("SceneSnapshotKeyPrefix") + runId;
            var stateKey = GetPrivateConstString("SessionKeyPrefix") + runId;
            var state = TestRunnerProvider.CreateRunningState(runId, "EditMode");
            state["status"] = "restoring";
            state["terminalStatus"] = "completed";
            SessionState.SetString(key, snapshot);
            try
            {
                TestRunnerProvider.CompleteRestoration(runId, state);
                Assert.That((string)state["status"], Is.EqualTo("failed"));
                Assert.That((string)state["sceneRestoreError"], Is.Not.Empty);
                Assert.That(SessionState.GetString(key, ""), Is.EqualTo(snapshot));
                var response = TestRunnerProvider.CreateResultResponse(state, "auto", 0, 10);
                Assert.That((string)response["sceneSnapshot"], Is.EqualTo(snapshot));
            }
            finally
            {
                SessionState.EraseString(key);
                SessionState.EraseString(stateKey);
            }
        }

        [Test]
        public async System.Threading.Tasks.Task LostLaunchResponse_CanRecoverCompletedRunWithoutStartingAgain()
        {
            var runId = Guid.NewGuid().ToString("N");
            var historyKey = GetPrivateConstString("RecentRunsKey");
            var previous = SessionState.GetString(historyKey, "[]");
            var stateKey = GetPrivateConstString("SessionKeyPrefix") + runId;
            var arguments = new JObject { ["runId"] = runId, ["testNames"] = new JArray("Example.Test") };
            try
            {
                SessionState.SetString(historyKey, "[]");
                Assert.That(TestRunnerProvider.ResolveRunId(arguments), Is.EqualTo(runId));
                var state = TestRunnerProvider.CreateRunningState(runId, "EditMode");
                TestRunnerProvider.RecordRun(state, arguments);
                state["status"] = "completed";
                state["tests"] = new JArray(new JObject { ["fullName"] = "Example.Test", ["status"] = "Passed" });
                SessionState.SetString(stateKey, state.ToString());

                // Recreate the provider: recovery uses persisted SessionState, not an instance cache.
                var sink = new CollectingSink();
                new TestRunnerProvider().RegisterTools(sink);
                var runsTool = sink.Tools.Single(tool => tool.Name == "tests.runs");
                var context = new ToolContext(new JObject(), null, null, null, null, null, "recovery-test");
                var history = await runsTool.Handler(context, CancellationToken.None);
                Assert.That((string)history.StructuredContent["runs"][0]["runId"], Is.EqualTo(runId));
                Assert.That((string)history.StructuredContent["runs"][0]["filterSummary"], Does.Contain("Example.Test"));
                var resultTool = sink.Tools.Single(tool => tool.Name == "tests.result");
                context = new ToolContext(new JObject { ["runId"] = runId }, null, null, null, null, null, "recovery-test");
                var result = await resultTool.Handler(context, CancellationToken.None);
                Assert.That((string)result.StructuredContent["status"], Is.EqualTo("completed"));
                Assert.That((int)result.StructuredContent["passCount"], Is.EqualTo(1));
                foreach (var tool in new[] { runsTool, resultTool })
                {
                    Assert.That(tool.ReloadSafe, Is.True);
                    Assert.That(tool.RequiresWriterLease, Is.False);
                    Assert.That(tool.ExclusiveGroup, Is.EqualTo("test"));
                }

                // Exercise the public launch handler: duplicate detection must precede scene work.
                var runTool = sink.Tools.Single(tool => tool.Name == "tests.run");
                context = new ToolContext(arguments, null, null, null, null, null, "recovery-test");
                var error = Assert.ThrowsAsync<McpToolException>(async () => await runTool.Handler(context, CancellationToken.None));
                Assert.That(error.Code, Is.EqualTo(McpErrorCodes.Conflict));
                Assert.That((string)error.Data["runId"], Is.EqualTo(runId));
                Assert.That(SessionState.GetString(stateKey, ""), Is.EqualTo(state.ToString()));
                Assert.That(TestRunnerProvider.ReadRecentRuns().Count, Is.EqualTo(1));
            }
            finally
            {
                SessionState.SetString(historyKey, previous);
                SessionState.EraseString(stateKey);
            }
        }

        [TestCase("")]
        [TestCase("RecentRuns")]
        [TestCase("0123456789012345678901234567890Z")]
        [TestCase("012345678901234567890123456789012")]
        public void RunId_RejectsInvalidClientIds(string runId)
        {
            var error = Assert.Throws<McpToolException>(() => TestRunnerProvider.ResolveRunId(new JObject { ["runId"] = runId }));
            Assert.That(error.Code, Is.EqualTo(McpErrorCodes.InvalidParams));
        }

        [Test]
        public void RecentRuns_BoundsMetadataAndRetainsNewestLaunches()
        {
            var key = GetPrivateConstString("RecentRunsKey");
            var previous = SessionState.GetString(key, "[]");
            try
            {
                SessionState.SetString(key, "[]");
                for (var i = 0; i < 100; i++)
                    TestRunnerProvider.RecordRun(TestRunnerProvider.CreateRunningState(i.ToString(), "EditMode"),
                        new JObject { ["testNames"] = new JArray(new string('x', 2000)) });
                var runs = TestRunnerProvider.ReadRecentRuns();
                Assert.That(runs.Count, Is.EqualTo(32));
                Assert.That((string)runs[0]["runId"], Is.EqualTo("99"));
                Assert.That((string)runs[31]["runId"], Is.EqualTo("68"));
                Assert.That(runs.All(run => ((string)run["filterSummary"]).Length == 1000 && (bool)run["filterSummaryTruncated"]), Is.True);
                Assert.That(runs.All(run => run["tests"] == null), Is.True);
            }
            finally { SessionState.SetString(key, previous); }
        }

        private static string GetPrivateConstString(string name)
        {
            var field = typeof(TestRunnerProvider).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null);
            return (string)field.GetRawConstantValue();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SceneViews_RestoreSerializedCameraStateWithoutTouchingNewWindows(bool orthographic)
        {
            var first = ScriptableObject.CreateInstance<SceneView>();
            var second = ScriptableObject.CreateInstance<SceneView>();
            var newlyOpened = ScriptableObject.CreateInstance<SceneView>();
            try
            {
                first.orthographic = orthographic;
                first.LookAtDirect(new Vector3(12, 8, -23), Quaternion.Euler(25, 140, 0), 17);
                second.in2DMode = true;
                second.orthographic = true;
                second.LookAtDirect(new Vector3(-4, 6, 0), Quaternion.identity, 9);
                var expected = TestRunnerProvider.CaptureSceneViews(new[] { first, second });
                var persisted = JArray.Parse(expected.ToString());
                first.orthographic = !orthographic;
                first.LookAtDirect(Vector3.zero, Quaternion.identity, 1);
                second.in2DMode = false;
                second.orthographic = false;
                second.LookAtDirect(Vector3.one, Quaternion.Euler(45, 45, 0), 3);
                var newState = TestRunnerProvider.CaptureSceneViews(new[] { newlyOpened });

                TestRunnerProvider.RestoreSceneViews(persisted, new[] { first, second, newlyOpened });

                Assert.That(JToken.DeepEquals(expected, TestRunnerProvider.CaptureSceneViews(new[] { first, second })), Is.True);
                Assert.That(JToken.DeepEquals(newState, TestRunnerProvider.CaptureSceneViews(new[] { newlyOpened })), Is.True);
                TestRunnerProvider.RestoreSceneViews(persisted, System.Array.Empty<SceneView>());
                TestRunnerProvider.RestoreSceneViews(null, new[] { first });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                UnityEngine.Object.DestroyImmediate(newlyOpened);
            }
        }

        private static JObject TestEntry(string fullName, string assembly, string category)
            => new()
            {
                ["fullName"] = fullName,
                ["assembly"] = assembly,
                ["categories"] = new JArray(category),
            };

        private sealed class CollectingSink : IToolRegistration
        {
            public readonly List<ToolDescriptor> Tools = new();

            public void Register(ToolDescriptor descriptor) => Tools.Add(descriptor);
        }

        private sealed class RecordingLogHandler : ILogHandler
        {
            internal int FormatCalls { get; private set; }
            internal string LastFormat { get; private set; }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                FormatCalls++;
                LastFormat = format;
            }

            public void LogException(Exception exception, UnityEngine.Object context) { }
        }
    }
}
#endif
