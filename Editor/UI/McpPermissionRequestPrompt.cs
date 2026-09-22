using System;
using System.Threading;
using UnityEditor;

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

    internal static class McpPermissionRequestPrompt
    {
        internal const int DefaultTimeoutSeconds = 300;

        private static McpPermissionRequest s_request;
        private static int s_progressId = -1;
        private static int s_lastRemainingSeconds = -1;

        internal static McpPermissionRequest Current =>
            s_request != null && !s_request.IsCompleted ? s_request : null;

        internal static McpPermissionRequest ShowRequest(
            string title,
            string details,
            string reason,
            TimeSpan timeout,
            Func<bool> isStillValid = null)
        {
            if (UnityEngine.Application.isBatchMode)
                throw new McpToolException(McpErrorCodes.ToolUnavailable,
                    "This operation requires interactive approval. Configure the local trust policy in the Editor before using batch mode.",
                    new Newtonsoft.Json.Linq.JObject { ["reason"] = "approval_required_in_batch" });
            if (s_request != null)
            {
                s_request.TryResolve(McpPermissionGrant.Deny);
                ClearCurrent();
            }

            var request = new McpPermissionRequest(title, details, reason, timeout, isStillValid);
            s_request = request;
            s_progressId = Progress.Start("MCP permission requested", StatusDescription(request));
            Progress.RegisterCancelCallback(s_progressId, () => request.TryResolve(McpPermissionGrant.Deny));
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += ClearCurrent;
            EditorApplication.quitting += ClearCurrent;
            return request;
        }

        internal static void Cancel(McpPermissionRequest request)
        {
            request?.TryResolve(McpPermissionGrant.Deny);
        }

        internal static bool ResolveCurrent(McpPermissionGrant grant)
        {
            var request = Current;
            if (request == null || !request.TryResolve(grant))
                return false;

            ClearCurrent();
            return true;
        }

        private static void Tick()
        {
            var request = s_request;
            if (request == null || request.IsCompleted || request.TryResolveIfNoLongerValid())
            {
                ClearCurrent();
                return;
            }

            var remainingSeconds = RemainingSeconds(request);
            if (remainingSeconds <= 0)
            {
                request.TryResolve(McpPermissionGrant.Deny);
                ClearCurrent();
                return;
            }

            if (remainingSeconds == s_lastRemainingSeconds || !Progress.Exists(s_progressId))
                return;

            s_lastRemainingSeconds = remainingSeconds;
            Progress.SetDescription(s_progressId, StatusDescription(request));
        }

        private static void ClearCurrent()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= ClearCurrent;
            EditorApplication.quitting -= ClearCurrent;
            if (s_progressId >= 0)
            {
                Progress.UnregisterCancelCallback(s_progressId);
                if (Progress.Exists(s_progressId))
                    Progress.Remove(s_progressId);
            }

            s_request = null;
            s_progressId = -1;
            s_lastRemainingSeconds = -1;
        }

        private static string StatusDescription(McpPermissionRequest request)
            => $"{request.Reason} Open Window > LittleBrushGames > Unity MCP to respond. " +
               $"Expires in {RemainingSeconds(request)}s.";

        private static int RemainingSeconds(McpPermissionRequest request)
            => Math.Max(0, (int)Math.Ceiling((request.DeadlineUtc - DateTime.UtcNow).TotalSeconds));
    }
}
