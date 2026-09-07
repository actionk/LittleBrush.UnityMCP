using LittleBrushGames.Mcp.Editor.Host;
using LittleBrushGames.Mcp.Editor.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public sealed class McpTrustPolicyTests
    {
        [TestCase(McpTrustPreset.ReadOnly, ToolTrustCategory.Read, McpTrustDecision.Allow)]
        [TestCase(McpTrustPreset.ReadOnly, ToolTrustCategory.ProjectWrite, McpTrustDecision.Deny)]
        [TestCase(McpTrustPreset.ConfirmChanges, ToolTrustCategory.Builds, McpTrustDecision.Ask)]
        [TestCase(McpTrustPreset.ConfirmRisky, ToolTrustCategory.ProjectWrite, McpTrustDecision.Allow)]
        [TestCase(McpTrustPreset.ConfirmRisky, ToolTrustCategory.Tests, McpTrustDecision.Allow)]
        [TestCase(McpTrustPreset.ConfirmRisky, ToolTrustCategory.EditorState, McpTrustDecision.Ask)]
        [TestCase(McpTrustPreset.Collaborative, ToolTrustCategory.UnsavedWork, McpTrustDecision.Ask)]
        [TestCase(McpTrustPreset.Collaborative, ToolTrustCategory.CodeExecution, McpTrustDecision.Ask)]
        [TestCase(McpTrustPreset.FullTrust, ToolTrustCategory.CodeExecution, McpTrustDecision.Allow)]
        public void PresetsMapCategories(
            McpTrustPreset preset,
            ToolTrustCategory category,
            McpTrustDecision expected)
        {
            Assert.That(McpTrustPolicy.PresetDecision(preset, category), Is.EqualTo(expected));
        }

        [TestCase(ToolTrustCategory.EditorState, true, McpTrustDecision.Ask)]
        [TestCase(ToolTrustCategory.EditorState, false, McpTrustDecision.Allow)]
        [TestCase(ToolTrustCategory.Tests, true, McpTrustDecision.Ask)]
        [TestCase(ToolTrustCategory.Tests, false, McpTrustDecision.Allow)]
        [TestCase(ToolTrustCategory.UnsavedWork, true, McpTrustDecision.Ask)]
        [TestCase(ToolTrustCategory.UnsavedWork, false, McpTrustDecision.Allow)]
        [TestCase(ToolTrustCategory.ProjectWrite, true, McpTrustDecision.Ask)]
        [TestCase(ToolTrustCategory.CodeExecution, true, McpTrustDecision.Ask)]
        [TestCase(ToolTrustCategory.Destructive, true, McpTrustDecision.Ask)]
        [TestCase(ToolTrustCategory.Builds, true, McpTrustDecision.Ask)]
        [TestCase(ToolTrustCategory.Read, true, McpTrustDecision.Allow)]
        public void CollaborativeMode_ProtectsActiveEditorFromAllMutations(
            ToolTrustCategory category,
            bool protectActiveSession,
            McpTrustDecision expected)
        {
            Assert.That(
                McpTrustPolicy.CollaborativeDecision(category, protectActiveSession),
                Is.EqualTo(expected));
        }

        [TestCase(true, 0, true)]
        [TestCase(true, McpEditorActivity.RecentActivityWindowMs - 1, true)]
        [TestCase(true, McpEditorActivity.RecentActivityWindowMs, false)]
        [TestCase(false, 0, false)]
        [TestCase(true, -1, false)]
        public void EditorActivity_RecentFocusRequiresKnownIdleBelowThreshold(
            bool editorFocused,
            long idleForMs,
            bool expected)
        {
            Assert.That(
                McpEditorActivity.HasRecentFocusedActivity(editorFocused, idleForMs),
                Is.EqualTo(expected));
        }

        [Test]
        public void ResolveCategory_UsesHintsAndRiskyArguments()
        {
            var read = new ToolDescriptor
            {
                Name = "sample.read",
                Annotations = new JObject { ["readOnlyHint"] = true },
            };
            var write = new ToolDescriptor { Name = "sample.write" };

            Assert.That(McpTrustPolicy.ResolveCategory(read, new JObject()), Is.EqualTo(ToolTrustCategory.Read));
            Assert.That(McpTrustPolicy.ResolveCategory(write, new JObject()), Is.EqualTo(ToolTrustCategory.ProjectWrite));
            Assert.That(
                McpTrustPolicy.ResolveCategory(write, new JObject { ["overwrite"] = true }),
                Is.EqualTo(ToolTrustCategory.Destructive));
        }

        [TestCase("tests.run", ToolTrustCategory.Tests)]
        [TestCase("build.start", ToolTrustCategory.Builds)]
        [TestCase("addressables.build", ToolTrustCategory.Builds)]
        [TestCase("editor.play", ToolTrustCategory.EditorState)]
        [TestCase("scene.request_open", ToolTrustCategory.EditorState)]
        [TestCase("editor.execute_code", ToolTrustCategory.CodeExecution)]
        [TestCase("tests.result", ToolTrustCategory.Read)]
        [TestCase("scene.find", ToolTrustCategory.Read)]
        [TestCase("scene.read_object", ToolTrustCategory.Read)]
        [TestCase("asset_library.search", ToolTrustCategory.Read)]
        [TestCase("ecs.entities", ToolTrustCategory.Read)]
        [TestCase("tests.cancel", ToolTrustCategory.Tests)]
        public void ResolveCategory_ClassifiesKnownTrustBoundaries(string name, ToolTrustCategory expected)
        {
            Assert.That(
                McpTrustPolicy.ResolveCategory(new ToolDescriptor { Name = name }, new JObject()),
                Is.EqualTo(expected));
        }

        [Test]
        public void PermissionRequest_WhenConditionIsAlreadySatisfied_CompletesAsAllowed()
        {
            var request = new McpPermissionRequest(
                "title", "details", "reason", System.TimeSpan.FromMinutes(1), () => false);

            Assert.That(request.TryResolveIfNoLongerValid(), Is.True);
            Assert.That(request.IsCompleted, Is.True);
            Assert.That(request.Grant, Is.EqualTo(McpPermissionGrant.AllowOnce));
        }

        [TestCase("editor.auto_stop_for_tool", true)]
        [TestCase("editor.request_stop_play_mode", true)]
        [TestCase("editor.play", false)]
        public void PlayModeStopRequests_CloseWhenPlayModeStops(string toolName, bool expected)
        {
            Assert.That(McpTrustAuthorizer.IsPlayModeStopRequest(toolName), Is.EqualTo(expected));
        }

        [TestCase((int)McpExecuteCodeConsentState.Disabled, McpTrustDecision.Deny)]
        [TestCase((int)McpExecuteCodeConsentState.Enabled, McpTrustDecision.Allow)]
        public void LegacyExecuteCodeChoice_MigratesToCodeExecutionOverride(
            int legacyState,
            McpTrustDecision expected)
        {
            var legacyKey = McpTrustPolicy.LegacyProjectPreferenceKey("execute-code");
            var currentKey = McpTrustPolicy.ProjectPreferenceKey("trust.CodeExecution");
            var hadLegacy = EditorPrefs.HasKey(legacyKey);
            var legacyValue = EditorPrefs.GetInt(legacyKey);
            var hadCurrent = EditorPrefs.HasKey(currentKey);
            var currentValue = EditorPrefs.GetInt(currentKey);

            try
            {
                EditorPrefs.DeleteKey(currentKey);
                EditorPrefs.SetInt(legacyKey, legacyState);

                McpTrustPolicy.MigrateLegacyPreferences();

                Assert.That(EditorPrefs.GetInt(currentKey), Is.EqualTo((int)expected));
            }
            finally
            {
                if (hadLegacy) EditorPrefs.SetInt(legacyKey, legacyValue);
                else EditorPrefs.DeleteKey(legacyKey);
                if (hadCurrent) EditorPrefs.SetInt(currentKey, currentValue);
                else EditorPrefs.DeleteKey(currentKey);
            }
        }
    }
}
