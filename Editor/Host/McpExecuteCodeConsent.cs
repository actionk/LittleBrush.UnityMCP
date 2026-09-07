using System;
using System.Runtime.CompilerServices;
using UnityEditor;

[assembly: InternalsVisibleTo("LittleBrushGames.UnityMCP.Tests")]
[assembly: InternalsVisibleTo("LittleBrushGames.UnityMCP.Modules.TestRunner")]

namespace LittleBrushGames.Mcp.Editor.Host
{
    internal enum McpExecuteCodeConsentState
    {
        Undecided = -1,
        Disabled = 0,
        Enabled = 1,
    }

    internal static class McpExecuteCodeConsent
    {
        static McpExecuteCodeConsent()
        {
            McpTrustPolicy.Changed += () => StateChanged?.Invoke();
        }

        internal static McpExecuteCodeConsentState State
        {
            get
            {
                return McpTrustPolicy.GetDecision(ToolTrustCategory.CodeExecution) switch
                {
                    McpTrustDecision.Allow => McpExecuteCodeConsentState.Enabled,
                    McpTrustDecision.Deny => McpExecuteCodeConsentState.Disabled,
                    _ => McpExecuteCodeConsentState.Undecided,
                };
            }
        }

        internal static bool IsEnabled => State == McpExecuteCodeConsentState.Enabled;
        internal static bool HasShownGettingStarted =>
            EditorPrefs.GetBool(McpTrustPolicy.ProjectPreferenceKey("getting-started-shown"), false);
        internal static event Action StateChanged;

        internal static void SetState(McpExecuteCodeConsentState state)
        {
            if (state == McpExecuteCodeConsentState.Undecided)
            {
                McpTrustPolicy.ClearOverride(ToolTrustCategory.CodeExecution);
                return;
            }
            McpTrustPolicy.SetOverride(ToolTrustCategory.CodeExecution, state switch
            {
                McpExecuteCodeConsentState.Enabled => McpTrustDecision.Allow,
                McpExecuteCodeConsentState.Disabled => McpTrustDecision.Deny,
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
            });
        }

        internal static void SetGettingStartedShown(bool shown)
        {
            if (shown)
                EditorPrefs.SetBool(McpTrustPolicy.ProjectPreferenceKey("getting-started-shown"), true);
            else
                EditorPrefs.DeleteKey(McpTrustPolicy.ProjectPreferenceKey("getting-started-shown"));
        }
    }
}
