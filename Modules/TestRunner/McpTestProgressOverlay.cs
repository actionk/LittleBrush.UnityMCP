#if UNITY_TESTS_FRAMEWORK
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace LittleBrushGames.Mcp.Modules.TestRunner
{
    [Serializable]
    internal sealed class TestRunProgressState
    {
        public string RunId, Mode, Status, CurrentTest, Error;
        public int Completed, Failed, Revision;
        public long StartedAt;
        public bool CanCancel;
        internal bool IsTerminal => Status is "completed" or "failed" or "cancelled";
    }

    /// <summary>Compact, reload-persistent UI state; never polls or deserializes test results per frame.</summary>
    [InitializeOnLoad]
    internal static class McpTestProgressOverlay
    {
        private const string SessionKey = "McpTestRun_ProgressOverlay";
        private static readonly Dictionary<SceneView, TestRunProgressView> s_views = new();
        private static readonly List<SceneView> s_closedViews = new();
        private static TestRunProgressState s_state;
        private static double s_nextUpdate;

        static McpTestProgressOverlay()
        {
            // Restore compact state before test callbacks can resume after a reload.
            var json = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try { s_state = JsonUtility.FromJson<TestRunProgressState>(json); }
            catch { SessionState.EraseString(SessionKey); }
            if (s_state != null) Subscribe();
        }

        internal static void Show(JObject state)
        {
            s_state = new TestRunProgressState
            {
                RunId = (string)state["runId"], Mode = (string)state["mode"],
                Status = (string)state["status"], StartedAt = (long)state["startedAt"],
            };
            Save();
            Subscribe();
        }

        internal static void Publish(JObject state)
        {
            if (s_state == null || s_state.RunId != (string)state["runId"]) return;
            s_state.Status = (string)state["status"];
            if (s_state.IsTerminal)
            {
                Hide();
                return;
            }
            s_state.Completed = (state["tests"] as JArray)?.Count ?? s_state.Completed;
            s_state.Failed = state["failCount"]?.Value<int>() ?? s_state.Failed;
            s_state.Error = (string)state["sceneRestoreError"] ?? (string)state["message"];
            s_state.CanCancel = s_state.Status == "running" && !string.IsNullOrEmpty((string)state["unityRunGuid"]);
            if (s_state.Status != "running") s_state.CurrentTest = null;
            Save();
        }

        internal static void TestStarted(string runId, string name)
        {
            if (s_state == null || s_state.RunId != runId) return;
            s_state.CurrentTest = name?.Length > 512 ? name[..512] + "…" : name;
            Save();
        }

        internal static void TestFinished(string runId, bool failed)
        {
            if (s_state == null || s_state.RunId != runId) return;
            s_state.Completed++;
            if (failed) s_state.Failed++;
            Save();
        }

        private static void Save()
        {
            s_state.Revision++;
            SessionState.SetString(SessionKey, JsonUtility.ToJson(s_state));
        }

        private static void Subscribe()
        {
            // Also discard final reports persisted by older versions.
            if (s_state.IsTerminal)
            {
                Hide();
                return;
            }
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            s_nextUpdate = 0;
        }

        private static void Update()
        {
            if (s_state == null || EditorApplication.timeSinceStartup < s_nextUpdate) return;
            s_nextUpdate = EditorApplication.timeSinceStartup + 0.1;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (s_state.IsTerminal)
            {
                Hide();
                return;
            }

            s_closedViews.Clear();
            foreach (var pair in s_views)
                if (pair.Key == null) s_closedViews.Add(pair.Key);
            foreach (var closed in s_closedViews)
            {
                s_views[closed].RemoveFromHierarchy();
                s_views.Remove(closed);
            }
            foreach (SceneView scene in SceneView.sceneViews)
            {
                if (!s_views.TryGetValue(scene, out var view))
                {
                    view = new TestRunProgressView(RequestCancel);
                    scene.rootVisualElement.Q<VisualElement>("mcp-test-progress")?.RemoveFromHierarchy();
                    scene.rootVisualElement.Add(view);
                    s_views.Add(scene, view);
                }
                view.UpdateState(s_state, now);
            }
        }

        private static void RequestCancel()
        {
            if (s_state == null || !s_state.CanCancel) return;
            try { TestRunnerProvider.RequestCancellation(s_state.RunId); }
            catch (Exception ex)
            {
                s_state.Error = "Could not cancel: " + ex.Message;
                Save();
            }
        }

        private static void Hide()
        {
            if (s_state != null && !s_state.IsTerminal) return;
            foreach (var view in s_views.Values) view.RemoveFromHierarchy();
            foreach (SceneView scene in SceneView.sceneViews)
                scene.rootVisualElement.Q<VisualElement>("mcp-test-progress")?.RemoveFromHierarchy();
            s_views.Clear();
            s_closedViews.Clear();
            s_state = null;
            SessionState.EraseString(SessionKey);
            EditorApplication.update -= Update;
        }
    }

    internal sealed class TestRunProgressView : VisualElement
    {
        private readonly Label _phase, _current, _counts, _elapsed, _hint;
        private readonly VisualElement _track, _activity;
        private readonly Button _cancel;
        private readonly Color _text, _muted, _error;
        private int _revision = -1;
        private long _elapsedSeconds = -1;
        private string _runId;

        internal TestRunProgressView(Action cancel)
        {
            name = "mcp-test-progress";
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = style.right = style.top = style.bottom = 0;
            style.paddingLeft = style.paddingRight = style.paddingTop = style.paddingBottom = 12;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            _text = new Color(0.94f, 0.95f, 0.97f);
            _muted = new Color(0.71f, 0.75f, 0.81f);
            _error = new Color(1f, 0.63f, 0.58f);
            var card = new ScrollView(ScrollViewMode.Vertical) { name = "card" };
            // Text changes never resize the panel; only a smaller viewport clamps it.
            card.style.width = 440;
            card.style.height = 280;
            card.style.maxWidth = Length.Percent(100);
            card.style.maxHeight = Length.Percent(100);
            card.style.flexShrink = 1;
            card.style.paddingLeft = card.style.paddingRight = 20;
            card.style.paddingTop = card.style.paddingBottom = 16;
            card.style.backgroundColor = new Color(0.12f, 0.14f, 0.17f, 0.91f);
            card.style.borderTopLeftRadius = card.style.borderTopRightRadius = card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = 10;
            card.style.borderLeftWidth = card.style.borderRightWidth = card.style.borderTopWidth = card.style.borderBottomWidth = 1;
            var border = new Color(0.45f, 0.53f, 0.64f, 0.45f);
            card.style.borderLeftColor = card.style.borderRightColor = card.style.borderTopColor = card.style.borderBottomColor = border;
            card.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            card.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            card.RegisterCallback<WheelEvent>(evt => evt.StopPropagation());
            Add(card);

            var eyebrow = Text("AI TEST RUN", 10, _muted);
            eyebrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(eyebrow);
            _phase = Text("Preparing tests…", 19, _text); _phase.name = "phase";
            _phase.style.unityFontStyleAndWeight = FontStyle.Bold;
            _phase.style.marginTop = 5;
            _phase.style.height = 26;
            _phase.style.whiteSpace = WhiteSpace.NoWrap;
            _phase.style.overflow = Overflow.Hidden;
            _phase.style.textOverflow = TextOverflow.Ellipsis;
            card.Add(_phase);
            _current = Text("Waiting for the test runner…", 11, _muted); _current.name = "current-test";
            _current.style.marginTop = 9;
            _current.style.height = 32;
            _current.style.overflow = Overflow.Hidden;
            card.Add(_current);

            _track = new VisualElement();
            _track.style.height = 3;
            _track.style.flexShrink = 0;
            _track.style.marginTop = _track.style.marginBottom = 14;
            _track.style.backgroundColor = border;
            _track.style.overflow = Overflow.Hidden;
            _activity = new VisualElement();
            _activity.style.position = Position.Absolute;
            _activity.style.width = Length.Percent(25);
            _activity.style.top = _activity.style.bottom = 0;
            _activity.style.backgroundColor = new Color(0.45f, 0.74f, 1f);
            _track.Add(_activity); card.Add(_track);

            _counts = Text("0 completed", 12, _text); _counts.name = "counts";
            card.Add(_counts);
            _elapsed = Text("", 11, _muted); _elapsed.name = "elapsed";
            _elapsed.style.marginTop = 4;
            _elapsed.style.height = 16;
            card.Add(_elapsed);
            _hint = Text("", 11, _muted); _hint.name = "hint";
            _hint.style.marginTop = 12;
            _hint.style.height = 32;
            _hint.style.overflow = Overflow.Hidden;
            card.Add(_hint);
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.justifyContent = Justify.FlexEnd;
            actions.style.marginTop = 14;
            actions.style.flexShrink = 0;
            _cancel = new Button(cancel) { text = "Cancel tests", name = "cancel", tooltip = "Request cancellation, then restore the original scenes.", tabIndex = 0 };
            _cancel.style.width = 180;
            _cancel.style.maxWidth = Length.Percent(100);
            _cancel.style.height = _cancel.style.minHeight = 26;
            _cancel.style.flexShrink = 0;
            _cancel.style.paddingLeft = _cancel.style.paddingRight = 12;
            _cancel.style.color = _text;
            _cancel.style.backgroundColor = new Color(0.23f, 0.27f, 0.33f);
            actions.Add(_cancel);
            card.Add(actions);
        }

        private static Label Text(string text, int size, Color color)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginLeft = label.style.marginRight = label.style.marginTop = label.style.marginBottom = 0;
            label.style.flexShrink = 0;
            return label;
        }

        internal void UpdateState(TestRunProgressState state, long now)
        {
            style.display = state.IsTerminal ? DisplayStyle.None : DisplayStyle.Flex;
            if (state.IsTerminal) return;
            _activity.style.left = Length.Percent((now / 100 % 20) * (75f / 19));
            var seconds = Math.Max(0, (now - state.StartedAt) / 1000);
            if (_runId == state.RunId && _revision == state.Revision && _elapsedSeconds == seconds) return;
            _runId = state.RunId; _revision = state.Revision; _elapsedSeconds = seconds;
            _phase.text = state.Status switch
            {
                "restoring" => "Restoring scenes…",
                "cancelling" => "Cancelling tests…",
                _ => string.IsNullOrEmpty(state.CurrentTest) && state.Completed == 0 ? "Preparing tests…" : "Running tests…",
            };
            _phase.style.color = _text;
            _current.text = state.CurrentTest ?? (state.Status == "restoring" ? "Restoring your original scene setup." : "Waiting for the test runner…");
            _current.tooltip = state.CurrentTest;
            _counts.text = $"{state.Completed} completed · {state.Failed} failed";
            _counts.style.color = state.Failed > 0 ? _error : _text;
            _elapsed.text = $"{(state.Mode == "PlayMode" ? "Play Mode" : "Edit Mode")} · {seconds / 60:00}:{seconds % 60:00} elapsed";
            _hint.text = state.Error ?? "Your scene will return when this run finishes.";
            _hint.style.color = !string.IsNullOrEmpty(state.Error) ? _error : _muted;
            _hint.tooltip = state.Error;
            _cancel.SetEnabled(state.Status == "running" && state.CanCancel);
            _cancel.text = state.Status == "cancelling" ? "Cancellation requested" : "Cancel tests";
        }
    }
}
#endif
