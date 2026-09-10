using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Providers;
using LittleBrushGames.Mcp.Editor.UI;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Host
{
    public enum McpTrustPreset
    {
        ReadOnly = 0,
        ConfirmChanges = 1,
        ConfirmRisky = 2,
        FullTrust = 3,
        Collaborative = 4,
    }

    public enum McpTrustDecision
    {
        Deny,
        Ask,
        Allow,
    }

    [InitializeOnLoad]
    internal static class McpTrustPolicy
    {
        private const int ContractVersion = 1;
        private static string s_keyPrefix;
        private static readonly object s_gate = new();
        private static readonly Dictionary<ToolTrustCategory, McpTrustDecision> s_overrides = new();
        private static readonly HashSet<ToolTrustCategory> s_sessionAllows = new();
        private static McpTrustPreset s_preset;
        private static bool s_hasSelectedPreset;

        static McpTrustPolicy()
        {
            MigrateLegacyPreferences();
            var value = EditorPrefs.GetInt(Key("preset"), (int)McpTrustPreset.ConfirmRisky);
            s_preset = Enum.IsDefined(typeof(McpTrustPreset), value)
                ? (McpTrustPreset)value
                : McpTrustPreset.ConfirmRisky;
            s_hasSelectedPreset = EditorPrefs.GetBool(Key("preset-selected"), EditorPrefs.HasKey(Key("preset")));
            foreach (ToolTrustCategory category in Enum.GetValues(typeof(ToolTrustCategory)))
            {
                if (category == ToolTrustCategory.Auto) continue;
                var key = OverrideKey(category);
                if (EditorPrefs.HasKey(key))
                {
                    var decision = EditorPrefs.GetInt(key);
                    if (Enum.IsDefined(typeof(McpTrustDecision), decision))
                        s_overrides[category] = (McpTrustDecision)decision;
                }
                if (SessionState.GetBool(SessionKey(category), false))
                    s_sessionAllows.Add(category);
            }
        }

        internal static event Action Changed;

        internal static McpTrustPreset Preset
        {
            get { lock (s_gate) return s_preset; }
            set
            {
                EditorPrefs.SetInt(Key("preset"), (int)value);
                EditorPrefs.SetBool(Key("preset-selected"), true);
                lock (s_gate)
                {
                    s_preset = value;
                    s_hasSelectedPreset = true;
                }
                ClearOverrides(false);
                Changed?.Invoke();
            }
        }

        internal static bool HasSelectedPreset
        {
            get { lock (s_gate) return s_hasSelectedPreset; }
        }

        internal static bool HasOverrides
        {
            get
            {
                lock (s_gate) return s_overrides.Count > 0;
            }
        }

        internal static bool HasSessionGrants
        {
            get
            {
                lock (s_gate) return s_sessionAllows.Count > 0;
            }
        }

        internal static string DisplayPreset => HasOverrides ? "Custom" : Preset.ToString();

        internal static McpTrustDecision GetDecision(ToolTrustCategory category)
        {
            if (category == ToolTrustCategory.Auto)
                category = ToolTrustCategory.ProjectWrite;

            lock (s_gate)
            {
                if (s_sessionAllows.Contains(category)) return McpTrustDecision.Allow;
                if (s_overrides.TryGetValue(category, out var value)) return value;
                if (s_preset == McpTrustPreset.Collaborative)
                    return CollaborativeDecision(category, IsProtectingActiveSession);
                return PresetDecision(s_preset, category);
            }
        }

        internal static bool IsProtectingActiveSession =>
            McpEditorActivity.HasRecentFocusedActivity(
                McpEditorActivity.IsEditorFocused,
                McpEditorActivity.IdleForMs)
            || McpBridgeHost.CachedIsPlaying && !McpPlayModeOwnership.IsOwnedCached;

        internal static void SetOverride(ToolTrustCategory category, McpTrustDecision decision)
        {
            EditorPrefs.SetInt(OverrideKey(category), (int)decision);
            SessionState.EraseBool(SessionKey(category));
            lock (s_gate)
            {
                s_overrides[category] = decision;
                s_sessionAllows.Remove(category);
            }
            Changed?.Invoke();
        }

        internal static bool TryGetOverride(ToolTrustCategory category, out McpTrustDecision decision)
        {
            lock (s_gate) return s_overrides.TryGetValue(category, out decision);
        }

        internal static bool IsAllowedForSession(ToolTrustCategory category)
        {
            lock (s_gate) return s_sessionAllows.Contains(category);
        }

        internal static void ClearOverride(ToolTrustCategory category)
        {
            EditorPrefs.DeleteKey(OverrideKey(category));
            lock (s_gate) s_overrides.Remove(category);
            Changed?.Invoke();
        }

        internal static void ClearOverrides(bool notify = true)
        {
            foreach (ToolTrustCategory category in Enum.GetValues(typeof(ToolTrustCategory)))
            {
                if (category == ToolTrustCategory.Auto) continue;
                EditorPrefs.DeleteKey(OverrideKey(category));
                SessionState.EraseBool(SessionKey(category));
            }
            lock (s_gate)
            {
                s_overrides.Clear();
                s_sessionAllows.Clear();
            }
            if (notify) Changed?.Invoke();
        }

        internal static void AllowForSession(ToolTrustCategory category)
        {
            SessionState.SetBool(SessionKey(category), true);
            lock (s_gate) s_sessionAllows.Add(category);
            Changed?.Invoke();
        }

        internal static ToolTrustCategory ResolveCategory(ToolDescriptor tool, JObject arguments)
        {
            var category = tool.TrustCategoryResolver?.Invoke(arguments) ?? tool.TrustCategory;
            if (category != ToolTrustCategory.Auto) return category;
            if (tool.Name == "tests.run") return ToolTrustCategory.Tests;
            if (tool.Name == "tests.cancel") return ToolTrustCategory.Tests;
            if (tool.Name == "build.start" || tool.Name == "addressables.build") return ToolTrustCategory.Builds;
            if (tool.Name == "editor.execute_code") return ToolTrustCategory.CodeExecution;
            if (tool.Name == "editor.play"
                || tool.Name == "editor.stop"
                || tool.Name == "editor.request_stop_play_mode"
                || tool.Name == "editor.pause"
                || tool.Name == "editor.resume"
                || tool.Name == "scene.request_open")
                return ToolTrustCategory.EditorState;
            if (tool.Name == "asset.move") return ToolTrustCategory.Destructive;
            if (tool.Annotations?["readOnlyHint"]?.Value<bool>() == true) return ToolTrustCategory.Read;
            if (IsReadToolName(tool.Name)) return ToolTrustCategory.Read;
            if (tool.Annotations?["destructiveHint"]?.Value<bool>() == true) return ToolTrustCategory.Destructive;
            if (arguments?["overwrite"]?.Value<bool>() == true
                || tool.Name.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0
                || tool.Name.IndexOf("remove", StringComparison.OrdinalIgnoreCase) >= 0)
                return ToolTrustCategory.Destructive;
            return ToolTrustCategory.ProjectWrite;
        }

        private static bool IsReadToolName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var suffixes = new[]
            {
                ".read", ".list", ".result", ".status", ".metrics", ".find", ".get", ".inspect",
                ".search", ".history", ".schema", ".catalog", ".preview", ".preview_screenshot",
                ".prefab_read", ".prefab_diff", ".prefab_preview", ".prefab_preview_matrix", ".prefab_reference_compare",
            };
            foreach (var suffix in suffixes)
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            var separator = name.LastIndexOf('.');
            var operation = separator >= 0 ? name.Substring(separator + 1) : name;
            if (operation.StartsWith("read_", StringComparison.OrdinalIgnoreCase)
                || operation.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
                return true;
            return name == "editor.compile.errors"
                || name == "editor.tools.list"
                || name == "editor.logs.tail"
                || name == "project.guid_to_path"
                || name == "project.path_to_guid"
                || name.StartsWith("ecs.", StringComparison.OrdinalIgnoreCase);
        }

        internal static JObject Snapshot()
        {
            var decisions = new JObject();
            foreach (ToolTrustCategory category in Enum.GetValues(typeof(ToolTrustCategory)))
                if (category != ToolTrustCategory.Auto)
                    decisions[category.ToString()] = GetDecision(category).ToString();
            return new JObject
            {
                ["preset"] = DisplayPreset,
                ["protectingActiveSession"] = Preset == McpTrustPreset.Collaborative && IsProtectingActiveSession,
                ["decisions"] = decisions,
            };
        }

        internal static async ValueTask AuthorizeOnMainThreadAsync(
            ToolContext ctx,
            ToolTrustCategory category,
            string title,
            string details,
            string reason,
            CancellationToken ct,
            Func<bool> isStillValid = null)
        {
            var decision = GetDecision(category);
            if (decision == McpTrustDecision.Allow) return;
            if (decision == McpTrustDecision.Deny)
                throw new McpToolException(McpErrorCodes.ToolUnavailable,
                    $"Action is not allowed by the local {category} trust policy.",
                    new JObject { ["trustCategory"] = category.ToString(), ["reason"] = "denied_by_policy" });

            var request = McpPermissionRequestWindow.ShowRequest(
                title, details, reason, TimeSpan.FromSeconds(McpPermissionRequestWindow.DefaultTimeoutSeconds), isStillValid);
            try
            {
                await ctx.Frames.WaitUntilAsync(
                    () => request.IsCompleted,
                    TimeSpan.FromSeconds(McpPermissionRequestWindow.DefaultTimeoutSeconds + 5), ct);
            }
            catch (TimeoutException)
            {
                McpPermissionRequestWindow.Cancel(request);
                throw new McpToolException(McpErrorCodes.ToolUnavailable,
                    $"The {category} permission request timed out.",
                    new JObject { ["trustCategory"] = category.ToString(), ["reason"] = "timeout" });
            }
            catch
            {
                McpPermissionRequestWindow.Cancel(request);
                throw;
            }

            switch (request.Grant)
            {
                case McpPermissionGrant.AllowOnce: return;
                case McpPermissionGrant.AllowForSession: AllowForSession(category); return;
                case McpPermissionGrant.AlwaysAllow: SetOverride(category, McpTrustDecision.Allow); return;
                default:
                    throw new McpToolException(McpErrorCodes.ToolUnavailable,
                        $"User denied the {category} action.",
                        new JObject { ["trustCategory"] = category.ToString(), ["reason"] = "user_rejected_or_timeout" });
            }
        }

        internal static McpTrustDecision PresetDecision(McpTrustPreset preset, ToolTrustCategory category)
        {
            if (category == ToolTrustCategory.Read) return McpTrustDecision.Allow;
            return preset switch
            {
                McpTrustPreset.ReadOnly => McpTrustDecision.Deny,
                McpTrustPreset.ConfirmChanges => McpTrustDecision.Ask,
                McpTrustPreset.Collaborative => CollaborativeDecision(category, true),
                McpTrustPreset.FullTrust => McpTrustDecision.Allow,
                _ => category == ToolTrustCategory.ProjectWrite || category == ToolTrustCategory.Tests
                    ? McpTrustDecision.Allow
                    : McpTrustDecision.Ask,
            };
        }

        internal static McpTrustDecision CollaborativeDecision(
            ToolTrustCategory category,
            bool protectActiveSession)
        {
            if (protectActiveSession && category != ToolTrustCategory.Read)
                return McpTrustDecision.Ask;
            return McpTrustDecision.Allow;
        }

        private static string OverrideKey(ToolTrustCategory category) => Key("trust." + category);
        private static string SessionKey(ToolTrustCategory category) => Key("session." + category);
        private static string Key(string suffix) => $"{KeyPrefix}.{suffix}";

        internal static string ProjectPreferenceKey(string suffix) => Key(suffix);

        internal static void MigrateLegacyPreferences()
        {
            var legacyExecuteCode = LegacyKey("execute-code");
            var codeExecution = OverrideKey(ToolTrustCategory.CodeExecution);
            if (!EditorPrefs.HasKey(codeExecution) && EditorPrefs.HasKey(legacyExecuteCode))
            {
                var value = EditorPrefs.GetInt(legacyExecuteCode, (int)McpExecuteCodeConsentState.Undecided);
                if (value == (int)McpExecuteCodeConsentState.Enabled)
                    EditorPrefs.SetInt(codeExecution, (int)McpTrustDecision.Allow);
                else if (value == (int)McpExecuteCodeConsentState.Disabled)
                    EditorPrefs.SetInt(codeExecution, (int)McpTrustDecision.Deny);
            }

            var gettingStarted = Key("getting-started-shown");
            if (!EditorPrefs.HasKey(gettingStarted))
            {
                var legacyGettingStarted = LegacyKey("getting-started-shown");
                if (EditorPrefs.GetBool(legacyGettingStarted, false)
                    || EditorPrefs.GetBool("LittleBrushGames.UnityMCP.getting-started-shown", false))
                    EditorPrefs.SetBool(gettingStarted, true);
            }
        }

        internal static string LegacyProjectPreferenceKey(string suffix) => LegacyKey(suffix);

        private static string LegacyKey(string suffix) =>
            $"LittleBrushGames.UnityMCP.v1.{ProjectHash}.{suffix}";

        private static string KeyPrefix
        {
            get
            {
                if (!string.IsNullOrEmpty(s_keyPrefix)) return s_keyPrefix;
                s_keyPrefix = $"LittleBrushGames.UnityMCP.Trust.v{ContractVersion}.{ProjectHash}";
                return s_keyPrefix;
            }
        }

        private static Hash128 ProjectHash
        {
            get
            {
                var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                    .Replace('\\', '/');
                if (Application.platform == RuntimePlatform.WindowsEditor)
                    projectPath = projectPath.ToLowerInvariant();
                return Hash128.Compute(projectPath);
            }
        }
    }

    internal sealed class McpTrustAuthorizer : IToolAuthorizer
    {
        private readonly IMainThreadScheduler _mainThread;
        private readonly IFrameWaiter _frames;

        internal McpTrustAuthorizer(IMainThreadScheduler mainThread, IFrameWaiter frames)
        {
            _mainThread = mainThread;
            _frames = frames;
        }

        public async ValueTask AuthorizeAsync(ToolDescriptor tool, JObject arguments, CancellationToken ct)
        {
            var category = McpTrustPolicy.ResolveCategory(tool, arguments);
            var decision = McpTrustPolicy.GetDecision(category);
            if (decision == McpTrustDecision.Allow) return;
            if (decision == McpTrustDecision.Deny)
                throw Denied(tool, category, "denied_by_policy");
            if (McpBatchWorker.IsBatchMode)
                throw Denied(tool, category, "approval_required_in_batch");

            McpPermissionRequest request = null;
            await _mainThread.RunAsync(_ =>
            {
                request = McpPermissionRequestWindow.ShowRequest(
                    $"AI requests {category}",
                    tool.Name,
                    arguments.Value<string>("reason") ?? tool.Description,
                    TimeSpan.FromSeconds(McpPermissionRequestWindow.DefaultTimeoutSeconds),
                    IsPlayModeStopRequest(tool.Name)
                        ? () => EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode
                        : null);
                return default;
            }, ct);

            try
            {
                await _frames.WaitUntilAsync(
                    () => request.IsCompleted,
                    TimeSpan.FromSeconds(McpPermissionRequestWindow.DefaultTimeoutSeconds + 5),
                    ct);
            }
            catch (TimeoutException)
            {
                McpPermissionRequestWindow.Cancel(request);
                throw Denied(tool, category, "timeout");
            }
            catch
            {
                McpPermissionRequestWindow.Cancel(request);
                throw;
            }

            switch (request.Grant)
            {
                case McpPermissionGrant.AllowOnce:
                    return;
                case McpPermissionGrant.AllowForSession:
                    await _mainThread.RunAsync(_ => { McpTrustPolicy.AllowForSession(category); return new ValueTask(); }, ct);
                    return;
                case McpPermissionGrant.AlwaysAllow:
                    await _mainThread.RunAsync(_ => { McpTrustPolicy.SetOverride(category, McpTrustDecision.Allow); return new ValueTask(); }, ct);
                    return;
                default:
                    throw Denied(tool, category, "user_rejected_or_timeout");
            }
        }

        internal static bool IsPlayModeStopRequest(string toolName)
            => toolName == "editor.auto_stop_for_tool" || toolName == "editor.request_stop_play_mode";

        private static McpToolException Denied(ToolDescriptor tool, ToolTrustCategory category, string reason)
            => new McpToolException(
                McpErrorCodes.ToolUnavailable,
                $"Tool '{tool.Name}' is not allowed by the local {category} trust policy.",
                new JObject { ["trustCategory"] = category.ToString(), ["reason"] = reason, ["preset"] = McpTrustPolicy.DisplayPreset });
    }
}
