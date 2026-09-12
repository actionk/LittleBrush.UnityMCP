using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace LittleBrushGames.Mcp.Editor.UI
{
    internal enum McpPermissionGrant
    {
        Deny,
        AllowOnce,
        AllowForSession,
        AlwaysAllow,
    }

    internal sealed class McpPermissionRequest
    {
        private int _completed;

        internal McpPermissionRequest(
            string title,
            string details,
            string reason,
            TimeSpan timeout,
            Func<bool> isStillValid)
        {
            Title = title;
            Details = details;
            Reason = reason;
            DeadlineUtc = DateTime.UtcNow + timeout;
            IsStillValid = isStillValid;
        }

        internal string Title { get; }
        internal string Details { get; }
        internal string Reason { get; }
        internal DateTime DeadlineUtc { get; }
        internal Func<bool> IsStillValid { get; }
        internal bool IsCompleted => Volatile.Read(ref _completed) != 0;
        internal McpPermissionGrant Grant { get; private set; }
        internal bool Approved => Grant != McpPermissionGrant.Deny;

        internal bool TryResolve(McpPermissionGrant grant)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
                return false;

            Grant = grant;
            return true;
        }

        internal bool TryResolve(bool approved)
            => TryResolve(approved ? McpPermissionGrant.AllowOnce : McpPermissionGrant.Deny);

        internal bool TryResolveIfNoLongerValid()
        {
            if (IsStillValid == null || IsStillValid())
                return false;

            return TryResolve(McpPermissionGrant.AllowOnce);
        }
    }

    internal sealed class McpPermissionRequestWindow : EditorWindow
    {
        internal const int DefaultTimeoutSeconds = 300;
        private const float Width = 430f;
        private const float DefaultHeight = 196f;
        private const float DetailsHeight = 286f;

        private static McpPermissionRequestWindow s_window;
        private McpPermissionRequest _request;
        private Label _countdown;

        internal static McpPermissionRequest ShowRequest(
            string title,
            string details,
            string reason,
            TimeSpan timeout,
            Func<bool> isStillValid = null)
        {
            if (s_window != null)
            {
                s_window._request?.TryResolve(false);
                s_window.Close();
            }

            var request = new McpPermissionRequest(title, details, reason, timeout, isStillValid);
            var window = CreateInstance<McpPermissionRequestWindow>();
            window._request = request;
            window.titleContent = new GUIContent("MCP Permission");
            var height = string.IsNullOrWhiteSpace(details) ? DefaultHeight : DetailsHeight;
            window.position = BottomRightRect(Width, height);
            s_window = window;
            window.ShowPopup();
            return request;
        }

        internal static void Cancel(McpPermissionRequest request)
        {
            request?.TryResolve(false);
        }

        private void OnEnable()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            if (ReferenceEquals(s_window, this))
                s_window = null;
            _request?.TryResolve(false);
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 16;
            root.style.paddingRight = 16;
            root.style.paddingTop = 14;
            root.style.paddingBottom = 14;
            root.style.backgroundColor = new Color(0.08f, 0.10f, 0.14f, 0.98f);
            root.style.borderTopWidth = 2;
            root.style.borderBottomWidth = 2;
            root.style.borderLeftWidth = 2;
            root.style.borderRightWidth = 2;
            root.style.borderTopColor = new Color(0.95f, 0.68f, 0.16f);
            root.style.borderBottomColor = new Color(0.95f, 0.68f, 0.16f);
            root.style.borderLeftColor = new Color(0.95f, 0.68f, 0.16f);
            root.style.borderRightColor = new Color(0.95f, 0.68f, 0.16f);
            root.style.borderTopLeftRadius = 6;
            root.style.borderTopRightRadius = 6;
            root.style.borderBottomLeftRadius = 6;
            root.style.borderBottomRightRadius = 6;

            var title = new Label(_request?.Title ?? "AI requests permission");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 16;
            title.style.color = Color.white;
            root.Add(title);

            if (!string.IsNullOrWhiteSpace(_request?.Details))
            {
                var details = new Label(_request.Details)
                {
                    style = { whiteSpace = WhiteSpace.Normal, marginTop = 8, fontSize = 13, color = new Color(0.84f, 0.88f, 0.94f) }
                };
                root.Add(details);
            }

            var reason = new Label($"Reason: {_request?.Reason ?? "No reason supplied."}")
            {
                style = { whiteSpace = WhiteSpace.Normal, marginTop = 8, flexGrow = 1, fontSize = 13, color = new Color(0.92f, 0.94f, 0.98f) }
            };
            root.Add(reason);

            _countdown = new Label();
            _countdown.style.fontSize = 12;
            _countdown.style.unityFontStyleAndWeight = FontStyle.Bold;
            _countdown.style.color = new Color(1f, 0.82f, 0.36f);
            _countdown.style.marginTop = 8;
            root.Add(_countdown);

            var actions = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginTop = 10 }
            };
            var approve = new Button(() => Resolve(McpPermissionGrant.AllowOnce)) { text = "Allow Once" };
            approve.style.flexGrow = 1;
            approve.style.height = 32;
            approve.style.fontSize = 13;
            approve.style.unityFontStyleAndWeight = FontStyle.Bold;
            approve.style.backgroundColor = new Color(0.16f, 0.52f, 0.28f);
            approve.style.color = Color.white;
            var session = new Button(() => Resolve(McpPermissionGrant.AllowForSession)) { text = "Session" };
            session.style.flexGrow = 1;
            session.style.height = 32;
            var always = new Button(() => Resolve(McpPermissionGrant.AlwaysAllow)) { text = "Always" };
            always.style.flexGrow = 1;
            always.style.height = 32;
            var reject = new Button(() => Resolve(McpPermissionGrant.Deny)) { text = "Deny" };
            reject.style.flexGrow = 1;
            reject.style.height = 32;
            reject.style.fontSize = 13;
            reject.style.unityFontStyleAndWeight = FontStyle.Bold;
            reject.style.backgroundColor = new Color(0.48f, 0.18f, 0.20f);
            reject.style.color = Color.white;
            actions.Add(approve);
            actions.Add(session);
            actions.Add(always);
            actions.Add(reject);
            root.Add(actions);
            UpdateCountdown();
        }

        private void Tick()
        {
            if (_request == null || _request.IsCompleted)
            {
                Close();
                return;
            }

            if (_request.TryResolveIfNoLongerValid())
            {
                Close();
                return;
            }

            if (DateTime.UtcNow >= _request.DeadlineUtc)
                Resolve(McpPermissionGrant.Deny);
            else
                UpdateCountdown();
        }

        private void UpdateCountdown()
        {
            if (_countdown == null || _request == null) return;
            var remaining = _request.DeadlineUtc - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            _countdown.text = $"Expires in {Math.Ceiling(remaining.TotalSeconds):0}s";
        }

        private void Resolve(McpPermissionGrant grant)
        {
            _request?.TryResolve(grant);
            Close();
        }

        private static Rect BottomRightRect(float width, float height)
        {
            var main = EditorGUIUtility.GetMainWindowPosition();
            return new Rect(main.xMax - width - 18f, main.yMax - height - 42f, width, height);
        }
    }
}
