using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Diagnostics;
using LittleBrushGames.Mcp.Editor.Host;
using LittleBrushGames.Mcp.Editor.Skills;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace LittleBrushGames.Mcp.Editor.UI
{
    public sealed class McpBridgePanel : VisualElement
    {
        public const string McpRootPath = "Assets/Plugins/LittleBrushGames/Mcp";
        public const string McpSkillName = "unity-mcp";
        private static readonly McpTrustPreset[] s_trustPresets =
        {
            McpTrustPreset.ReadOnly,
            McpTrustPreset.ConfirmChanges,
            McpTrustPreset.ConfirmRisky,
            McpTrustPreset.Collaborative,
            McpTrustPreset.FullTrust,
        };
        private static McpBridgePanel s_instance;
        private static bool s_gettingStartedRequested;
        private Label _statusPill;
        private VisualElement _statusTab;
        private VisualElement _trustTab;
        private VisualElement _settingsTab;
        private McpTestHistoryPanel _testsTab;
        private VisualElement _gettingStartedTab;
        private readonly List<Button> _tabButtons = new();

        // Status tab
        private Label _portValue;
        private Label _urlValue;
        private Label _playingValue;
        private Label _compilingValue;
        private Label _toolCountValue;
        private VisualElement _toolList;
        private TextField _toolSearch;
        private int _activeTab;
        private string _toolRevision;

        // Settings tab
        private VisualElement _settingsContainer;
        private SerializedObject _settingsSo;

        // Trust tab
        private VisualElement _trustContainer;

        // Getting Started tab
        private VisualElement _trustPresetChoice;
        private readonly HashSet<string> _expandedTargets = new();

        public McpBridgePanel()
        {
            s_instance = this;
            style.flexGrow = 1;
            RegisterCallback<AttachToPanelEvent>(_ => EditorApplication.update += RefreshLive);
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                EditorApplication.update -= RefreshLive;
                if (ReferenceEquals(s_instance, this)) s_instance = null;
            });

            var root = this;
            root.AddToClassList("mcp-root");
            root.EnableInClassList("light-theme", !EditorGUIUtility.isProSkin);
            RegisterCallback<GeometryChangedEvent>(evt => EnableInClassList("compact", evt.newRect.width < 560));

            // Load USS
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/Plugins/LittleBrushGames/Mcp/Editor/UI/McpBridgePanel.uss");
            if (uss != null) root.styleSheets.Add(uss);

            // Tab bar
            var tabBar = new VisualElement();
            tabBar.AddToClassList("tab-bar");
            tabBar.Add(TabButton("Getting Started", () => ShowTab(0)));
            tabBar.Add(TabButton("Status", () => ShowTab(1)));
            tabBar.Add(TabButton("Trust & Automation", () => ShowTab(2)));
            tabBar.Add(TabButton("Settings", () => ShowTab(3)));
            tabBar.Add(TabButton("Tests", () => ShowTab(4)));
            tabBar.style.flexWrap = Wrap.Wrap;
            tabBar.Add(new VisualElement { name = "spacer" }.With(e => e.AddToClassList("tab-spacer")));
            _statusPill = new Label("...");
            _statusPill.AddToClassList("status-pill");
            tabBar.Add(_statusPill);
            root.Add(tabBar);

            // Tabs
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;

            _gettingStartedTab = BuildGettingStartedTab();
            _statusTab = BuildStatusTab();
            _trustTab = BuildTrustTab();
            _settingsTab = BuildSettingsTab();
            _testsTab = new McpTestHistoryPanel();

            scroll.Add(_gettingStartedTab);
            scroll.Add(_statusTab);
            scroll.Add(_trustTab);
            scroll.Add(_settingsTab);
            scroll.Add(_testsTab);
            root.Add(scroll);

            ShowTab(s_gettingStartedRequested || !McpTrustPolicy.HasSelectedPreset ? 0 : 1);
            s_gettingStartedRequested = false;
            RefreshLive();
        }

        internal static void RequestGettingStarted()
        {
            if (s_instance != null)
                s_instance.ShowTab(0);
            else
                s_gettingStartedRequested = true;
        }

        // ─── Tab switching ──────────────────────────────────────

        private Button TabButton(string label, Action onClick)
        {
            var btn = new Button(onClick) { text = label };
            btn.AddToClassList("tab-btn");
            _tabButtons.Add(btn);
            return btn;
        }

        private void ShowTab(int idx)
        {
            _activeTab = idx;
            _lastRefresh = -1;
            var tabs = new[] { _gettingStartedTab, _statusTab, _trustTab, _settingsTab, _testsTab };
            for (var i = 0; i < tabs.Length; i++)
            {
                tabs[i].EnableInClassList("hidden", i != idx);
                _tabButtons[i].EnableInClassList("tab-active", i == idx);
            }
            if (idx == 0) RefreshGettingStarted();
            if (idx == 2) RebuildTrust();
            if (idx == 3) RebuildSettings();
            if (idx == 4) _testsTab.Refresh();
        }

        // ─── Status Tab ─────────────────────────────────────────

        private VisualElement BuildStatusTab()
        {
            var tab = new VisualElement();
            tab.AddToClassList("tab-content");

            // Server section
            tab.Add(SectionHeader("Server"));
            tab.Add(InfoRow("Port", out _portValue));
            tab.Add(InfoRow("Endpoint", out _urlValue, mono: true));
            tab.Add(InfoRow("Playing", out _playingValue));
            tab.Add(InfoRow("Compiling", out _compilingValue));

            // Tools section
            tab.Add(SectionHeader("Tools"));
            _toolSearch = new TextField("Find tool");
            _toolSearch.RegisterValueChangedCallback(_ => FilterTools());
            tab.Add(_toolSearch);
            tab.Add(InfoRow("Total", out _toolCountValue));

            var toolScroll = new ScrollView(ScrollViewMode.Vertical);
            toolScroll.AddToClassList("tool-scroll");
            _toolList = toolScroll.contentContainer;
            tab.Add(toolScroll);

            // Actions
            var actions = new VisualElement();
            actions.AddToClassList("action-row");
            var restartBtn = new Button(McpBridgeHost.Restart) { text = "Restart Bridge" };
            restartBtn.AddToClassList("btn-primary");
            actions.Add(restartBtn);

            var copyBtn = new Button(() =>
                EditorGUIUtility.systemCopyBuffer = $"http://127.0.0.1:{McpBridgeHost.Port}/mcp")
            { text = "Copy Endpoint" };
            copyBtn.AddToClassList("btn-secondary");
            actions.Add(copyBtn);

            var healthBtn = new Button(() =>
                EditorGUIUtility.systemCopyBuffer = $"http://127.0.0.1:{McpBridgeHost.Port}/mcp/health")
            { text = "Copy Health URL" };
            healthBtn.AddToClassList("btn-secondary");
            actions.Add(healthBtn);

            tab.Add(actions);
            return tab;
        }

        // ─── Settings Tab ───────────────────────────────────────

        private VisualElement BuildSettingsTab()
        {
            var tab = new VisualElement();
            tab.AddToClassList("tab-content");
            _settingsContainer = tab;
            return tab;
        }

        private void RebuildSettings()
        {
            _settingsContainer.Clear();
            var settings = McpBridgeSettings.GetOrLoad();

            if (settings == null)
            {
                var box = new VisualElement();
                box.AddToClassList("no-settings-box");
                box.Add(new Label("No McpBridgeSettings asset found.\nThe bridge is running with defaults. " +
                    "Create a settings asset to customize the server, transport, toolbar, limits, and logging."));

                var createBtn = new Button(() => { McpBridgeSettings.GetOrCreate(); RebuildSettings(); })
                { text = "Create Settings Asset" };
                createBtn.AddToClassList("btn-primary");
                createBtn.style.marginTop = 8;
                box.Add(createBtn);
                _settingsContainer.Add(box);

                _settingsContainer.Add(SectionHeader("Current Defaults"));
                Label _;
                _settingsContainer.Add(InfoRow("Port", out _, "48765"));
                _settingsContainer.Add(InfoRow("Scene toolbar", out _, "Off"));
                _settingsContainer.Add(InfoRow("Standalone bridge", out _, "On"));
                _settingsContainer.Add(InfoRow("Unity transport port", out _, "48766"));
                _settingsContainer.Add(InfoRow("Tool-call logging", out _, "Summary"));
                _settingsContainer.Add(InfoRow("Log buffer", out _, "1000"));
                _settingsContainer.Add(InfoRow("Max scene nodes", out _, "5000"));
                _settingsContainer.Add(InfoRow("Max scene depth", out _, "10"));
                return;
            }

            _settingsSo = new SerializedObject(settings);
            _settingsSo.Update();

            _settingsContainer.Add(SectionHeader("Settings Asset"));
            var assetGroup = new VisualElement();
            assetGroup.AddToClassList("settings-group");
            Label assetLabel;
            assetGroup.Add(InfoRow("Asset", out assetLabel, AssetDatabase.GetAssetPath(settings)));
            assetLabel.tooltip = AssetDatabase.GetAssetPath(settings);
            _settingsContainer.Add(assetGroup);

            _settingsContainer.Add(SectionHeader("General"));
            var general = new VisualElement();
            general.AddToClassList("settings-group");
            general.Add(new PropertyField(_settingsSo.FindProperty("m_ServerEnabled"), "Enabled"));
            general.Add(new PropertyField(_settingsSo.FindProperty("m_AutoStart"), "Auto-start"));
            general.Add(new PropertyField(_settingsSo.FindProperty("m_ShowSceneToolbar"), "Show Scene Toolbar"));
            general.Add(new PropertyField(_settingsSo.FindProperty("m_Port"), "Port"));
            general.Bind(_settingsSo);
            _settingsContainer.Add(general);

            var logging = new Foldout { text = "Logging & execution history", value = false };
            logging.AddToClassList("settings-group");
            logging.Add(new PropertyField(_settingsSo.FindProperty("m_DebugLogging"), "Debug Logging"));
            logging.Add(new PropertyField(_settingsSo.FindProperty("m_ExecutionHistoryRetention"), "Execution History Limit"));
            logging.Add(new PropertyField(_settingsSo.FindProperty("m_RetainExecutionCode"), "Retain Executed Code"));
            logging.Add(new PropertyField(_settingsSo.FindProperty("m_ToolCallLogMode"), "Tool-call Logging"));
            logging.Add(new PropertyField(_settingsSo.FindProperty("m_MaxToolCallLogCharacters"), "Max Compact Response Characters"));
            logging.Bind(_settingsSo);
            _settingsContainer.Add(logging);

            _settingsContainer.Add(SectionHeader("Transport"));
            var transport = new VisualElement();
            transport.AddToClassList("settings-group");
            transport.Add(new PropertyField(_settingsSo.FindProperty("m_UseBridge"), "Use Standalone Bridge"));
            transport.Add(new PropertyField(_settingsSo.FindProperty("m_BridgeUnityPort"), "Unity Transport Port"));
            transport.Bind(_settingsSo);
            _settingsContainer.Add(transport);

            _settingsContainer.Add(SectionHeader("Limits"));
            var limits = new VisualElement();
            limits.AddToClassList("settings-group");
            limits.Add(new PropertyField(_settingsSo.FindProperty("m_LogBufferSize"), "Log Buffer Size"));
            limits.Add(new PropertyField(_settingsSo.FindProperty("m_DefaultToolTimeoutSeconds"), "Tool Timeout (s)"));
            limits.Add(new PropertyField(_settingsSo.FindProperty("m_MaxSceneNodes"), "Max Scene Nodes"));
            limits.Add(new PropertyField(_settingsSo.FindProperty("m_MaxSceneDepth"), "Max Scene Depth"));
            limits.Bind(_settingsSo);
            _settingsContainer.Add(limits);

            var hint = new Label("Changes apply after restarting the bridge.");
            hint.AddToClassList("settings-hint");
            _settingsContainer.Add(hint);

            var actions = new VisualElement();
            actions.AddToClassList("action-row");
            var applyBtn = new Button(() =>
            {
                _settingsSo.ApplyModifiedProperties();
                McpToolbarOverlay.ApplyConfiguredVisibility();
                McpBridgeHost.Restart();
            })
            { text = "Apply & Restart" };
            applyBtn.AddToClassList("btn-primary");
            actions.Add(applyBtn);

            var pingBtn = new Button(() => EditorGUIUtility.PingObject(settings)) { text = "Ping Asset" };
            pingBtn.AddToClassList("btn-secondary");
            actions.Add(pingBtn);
            _settingsContainer.Add(actions);
        }

        private VisualElement BuildTrustTab()
        {
            var tab = new VisualElement();
            tab.AddToClassList("tab-content");
            _trustContainer = tab;
            return tab;
        }

        private void RebuildTrust()
        {
            _trustContainer.Clear();
            _trustContainer.Add(SectionHeader("Trust mode"));
            var group = new VisualElement();
            group.AddToClassList("settings-group");

            var preset = new EnumField("Mode", McpTrustPolicy.Preset);
            preset.RegisterValueChangedCallback(evt =>
            {
                McpTrustPolicy.Preset = (McpTrustPreset)evt.newValue;
                RebuildTrust();
                RefreshTrustPresetChoice();
            });
            group.Add(preset);

            var hint = new Label(
                $"Effective mode: {McpTrustPolicy.DisplayPreset}. Selecting a mode resets category overrides and session grants. " +
                "These choices are stored locally for this project and are never committed to Git.");
            hint.AddToClassList("settings-hint");
            group.Add(hint);
            _trustContainer.Add(group);

            var modeHelp = new Foldout { text = "Compare trust modes", value = false };
            var presetGrid = new VisualElement();
            presetGrid.AddToClassList("trust-preset-grid");
            foreach (var value in s_trustPresets)
                presetGrid.Add(TrustPresetCard(value, selectable: false));
            modeHelp.Add(presetGrid);
            _trustContainer.Add(modeHelp);

            _trustContainer.Add(SectionHeader("Category rules"));
            var decisions = new Label(
                "Deny blocks the action. Ask pauses and lets you allow it once, for this Unity session, or always. " +
                "Allow runs an explicitly requested action without an extra trust prompt. Hard validation and safety checks still apply.");
            decisions.AddToClassList("settings-hint");
            _trustContainer.Add(decisions);

            var categories = new VisualElement();
            categories.AddToClassList("settings-group");
            foreach (ToolTrustCategory category in Enum.GetValues(typeof(ToolTrustCategory)))
            {
                if (category == ToolTrustCategory.Auto) continue;
                var captured = category;
                var row = new VisualElement();
                row.AddToClassList("trust-category-row");
                var text = new VisualElement();
                text.AddToClassList("trust-category-text");
                var title = new Label(TrustCategoryTitle(category));
                title.AddToClassList("trust-category-title");
                text.Add(title);
                var description = new Label(TrustCategoryDescription(category));
                description.AddToClassList("trust-category-description");
                text.Add(description);
                row.Add(text);

                var field = new EnumField(McpTrustPolicy.GetDecision(category));
                field.AddToClassList("trust-category-decision");
                field.RegisterValueChangedCallback(evt =>
                {
                    McpTrustPolicy.SetOverride(captured, (McpTrustDecision)evt.newValue);
                    RebuildTrust();
                    RefreshTrustPresetChoice();
                });
                row.Add(field);

                var source = new Label(McpTrustPolicy.IsAllowedForSession(category)
                    ? "Session grant"
                    : McpTrustPolicy.TryGetOverride(category, out _) ? "Override" : "Mode default");
                source.AddToClassList("trust-category-source");
                row.Add(source);
                categories.Add(row);
            }
            _trustContainer.Add(categories);

            var reset = new Button(() =>
            {
                McpTrustPolicy.ClearOverrides();
                RebuildTrust();
                RefreshTrustPresetChoice();
            }) { text = "Reset overrides & session grants" };
            reset.AddToClassList("btn-secondary");
            reset.SetEnabled(McpTrustPolicy.HasOverrides || McpTrustPolicy.HasSessionGrants);
            _trustContainer.Add(reset);
        }

        // ─── Install Tab ────────────────────────────────────────

        private readonly struct AiTarget
        {
            public readonly string Name;
            public readonly int IconIndex;
            public readonly string Desc;
            public readonly InstallOption[] Options;

            public AiTarget(
                string name,
                int iconIndex,
                string desc,
                params InstallOption[] options)
            {
                Name = name;
                IconIndex = iconIndex;
                Desc = desc;
                Options = options;
            }
        }

        private readonly struct InstallOption
        {
            public readonly string Label;
            public readonly string RestartName;
            public readonly Func<string> PathFunc;
            public readonly Func<string, bool> IsInstalledFunc;
            public readonly Func<string, string> StatusTextFunc;
            public readonly Action<string, int> InstallAction;
            public readonly Action<string> RemoveAction;

            public InstallOption(
                string label,
                string restartName,
                Func<string> path,
                Func<string, bool> isInstalled,
                Func<string, string> statusText,
                Action<string, int> installAction,
                Action<string> removeAction)
            {
                Label = label;
                RestartName = restartName;
                PathFunc = path;
                IsInstalledFunc = isInstalled;
                StatusTextFunc = statusText;
                InstallAction = installAction;
                RemoveAction = removeAction;
            }
        }

        private static string ProjectRoot => Path.GetFullPath(Application.dataPath + "/..");

        private static readonly AiTarget[] s_targets =
        {
            new("Codex", 0, "Adds a project-scoped .codex/config.toml entry for trusted Codex projects.",
                new InstallOption("Project", "Codex",
                    () => Path.Combine(ProjectRoot, ".codex", "config.toml"),
                    path => File.Exists(path) && McpClientConfigUtility.HasCodexServerConfig(File.ReadAllText(path)),
                    path => BuildInstallStatusText(path, "unity", InstallConfigKind.Codex, "Project"),
                    (path, port) => McpClientConfigUtility.WriteCodexConfig(path, port),
                    path => McpClientConfigUtility.RemoveCodexConfig(path)),
                new InstallOption("Global", "Codex",
                    () => Path.Combine(UserProfile, ".codex", "config.toml"),
                    path => File.Exists(path) && McpClientConfigUtility.HasCodexServerConfig(File.ReadAllText(path)),
                    path => BuildInstallStatusText(path, "unity", InstallConfigKind.Codex, "Global"),
                    (path, port) => McpClientConfigUtility.WriteCodexConfig(path, port),
                    path => McpClientConfigUtility.RemoveCodexConfig(path))),

            new("Claude Code", 1, "Creates .mcp.json in project root.",
                new InstallOption("Project", "Claude Code",
                    () => Path.Combine(ProjectRoot, ".mcp.json"),
                    path => File.Exists(path) && McpClientConfigUtility.HasClaudeCodeServerConfig(File.ReadAllText(path)),
                    path => BuildInstallStatusText(path, "unity", InstallConfigKind.ClaudeCode, "Project"),
                    (path, port) => McpClientConfigUtility.WriteClaudeCodeConfig(path, port),
                    path => McpClientConfigUtility.RemoveClaudeCodeConfigFile(path)),
                new InstallOption("Global", "Claude Code",
                    () => Path.Combine(UserProfile, ".claude.json"),
                    path => File.Exists(path) && McpClientConfigUtility.HasClaudeCodeServerConfig(File.ReadAllText(path)),
                    path => BuildInstallStatusText(path, "unity", InstallConfigKind.ClaudeCode, "Global"),
                    (path, port) => McpClientConfigUtility.WriteClaudeCodeConfig(path, port),
                    path => McpClientConfigUtility.RemoveClaudeCodeConfigFile(path))),

            new("OpenCode", 2, "Adds Unity MCP to OpenCode as a remote server. Project config wins over global config.",
                new InstallOption("Project", "OpenCode",
                    () => Path.Combine(ProjectRoot, "opencode.json"),
                    path => File.Exists(path) && McpClientConfigUtility.HasOpenCodeServerConfig(File.ReadAllText(path)),
                    path => BuildInstallStatusText(path, "unity", InstallConfigKind.OpenCode, "Project"),
                    (path, port) => McpClientConfigUtility.WriteOpenCodeConfig(path, port),
                    path => McpClientConfigUtility.RemoveOpenCodeConfig(path)),
                new InstallOption("Global", "OpenCode",
                    () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".config", "opencode", "opencode.json"),
                    path => File.Exists(path) && McpClientConfigUtility.HasOpenCodeServerConfig(File.ReadAllText(path)),
                    path => BuildInstallStatusText(path, "unity", InstallConfigKind.OpenCode, "Global"),
                    (path, port) => McpClientConfigUtility.WriteOpenCodeConfig(path, port),
                    path => McpClientConfigUtility.RemoveOpenCodeConfig(path))),

            new("Cursor", 3, "Creates .cursor/mcp.json in project root.",
                new InstallOption("Project", "Cursor",
                    () => Path.Combine(ProjectRoot, ".cursor", "mcp.json"),
                    path => File.Exists(path) && McpClientConfigUtility.HasCursorServerConfig(File.ReadAllText(path)),
                    path => BuildInstallStatusText(path, "unity", InstallConfigKind.Cursor, "Project"),
                    (path, port) => McpClientConfigUtility.WriteCursorConfig(path, port),
                    path => McpClientConfigUtility.RemoveCursorConfigFile(path)),
                new InstallOption("Global", "Cursor",
                    () => Path.Combine(UserProfile, ".cursor", "mcp.json"),
                    path => File.Exists(path) && McpClientConfigUtility.HasCursorServerConfig(File.ReadAllText(path)),
                    path => BuildInstallStatusText(path, "unity", InstallConfigKind.Cursor, "Global"),
                    (path, port) => McpClientConfigUtility.WriteCursorConfig(path, port),
                    path => McpClientConfigUtility.RemoveCursorConfigFile(path))),
        };

        private VisualElement _installGrid;

        private VisualElement BuildGettingStartedTab()
        {
            var tab = new VisualElement();
            tab.AddToClassList("tab-content");

            var hint = new Label(
                "Choose how independently trusted AI clients may operate in this Unity project, then connect a client and install its optional agent skill. " +
                "Only Unity MCP-owned files and entries are changed; restart clients after configuration changes.");
            hint.AddToClassList("settings-hint");
            hint.style.marginBottom = 12;
            tab.Add(hint);

            tab.Add(SectionHeader("1. Choose a trust mode"));
            _trustPresetChoice = new VisualElement();
            tab.Add(_trustPresetChoice);

            tab.Add(SectionHeader("2. Connect an AI client and skills"));
            _installGrid = new VisualElement();
            _installGrid.AddToClassList("install-grid");
            tab.Add(_installGrid);

            // Manual
            var manual = new Foldout { text = "Manual connection details", value = false };
            manual.AddToClassList("manual-box");
            Label _;
            manual.Add(InfoRow("Endpoint", out _, $"http://127.0.0.1:{McpBridgeHost.Port}/mcp", mono: true));
            manual.Add(InfoRow("Health", out _, $"http://127.0.0.1:{McpBridgeHost.Port}/mcp/health", mono: true));
            manual.Add(InfoRow("Protocol", out _, "JSON-RPC 2.0 / HTTP POST"));
            manual.Add(InfoRow("MCP Version", out _, "2025-06-18"));

            tab.Add(manual);

            return tab;
        }

        private void RefreshGettingStarted()
        {
            RefreshInstallCards();
            RefreshTrustPresetChoice();
        }

        private void RefreshTrustPresetChoice()
        {
            if (_trustPresetChoice == null) return;
            _trustPresetChoice.Clear();
            var choices = s_trustPresets.Select(TrustPresetTitle).ToList();
            var selected = Array.IndexOf(s_trustPresets, McpTrustPolicy.Preset);
            var field = new DropdownField("Trust mode", choices, Math.Max(0, selected));
            field.tooltip = "Selecting a mode resets category overrides and session grants.";
            field.RegisterValueChangedCallback(evt =>
            {
                McpTrustPolicy.Preset = s_trustPresets[choices.IndexOf(evt.newValue)];
                RefreshTrustPresetChoice();
            });
            _trustPresetChoice.Add(field);
            if (!McpTrustPolicy.HasSelectedPreset)
            {
                var confirm = new Button(() =>
                {
                    McpTrustPolicy.Preset = s_trustPresets[field.index];
                    RefreshTrustPresetChoice();
                }) { text = "Use this mode" };
                confirm.AddToClassList("btn-primary");
                _trustPresetChoice.Add(confirm);
            }
            var note = new Label(TrustPresetDescription(McpTrustPolicy.Preset));
            note.AddToClassList("settings-hint");
            _trustPresetChoice.Add(note);
            var rules = new Button(() => ShowTab(2)) { text = "Compare modes & edit rules" };
            rules.AddToClassList("btn-secondary");
            _trustPresetChoice.Add(rules);
        }

        private VisualElement TrustPresetCard(McpTrustPreset preset, bool selectable)
        {
            var selected = McpTrustPolicy.HasSelectedPreset && McpTrustPolicy.Preset == preset;
            var card = new VisualElement();
            card.AddToClassList("trust-preset-card");
            card.EnableInClassList("selected", selected);

            var title = new Label(TrustPresetTitle(preset) + (preset == McpTrustPreset.Collaborative ? "  ·  Recommended" : string.Empty));
            title.AddToClassList("trust-preset-title");
            card.Add(title);
            var description = new Label(TrustPresetDescription(preset));
            description.AddToClassList("trust-preset-description");
            card.Add(description);

            if (selectable)
            {
                var choose = new Button(() =>
                {
                    McpTrustPolicy.Preset = preset;
                    RefreshTrustPresetChoice();
                }) { text = selected ? "Selected" : "Select" };
                choose.AddToClassList(selected ? "btn-secondary" : "btn-primary");
                choose.SetEnabled(!selected);
                card.Add(choose);
            }
            return card;
        }

        private static string TrustPresetTitle(McpTrustPreset preset) => preset switch
        {
            McpTrustPreset.ReadOnly => "Read Only",
            McpTrustPreset.ConfirmChanges => "Confirm Changes",
            McpTrustPreset.ConfirmRisky => "Confirm Risky",
            McpTrustPreset.FullTrust => "Full Trust",
            McpTrustPreset.Collaborative => "Collaborative / Don't Interrupt Me",
            _ => preset.ToString(),
        };

        private static string TrustPresetDescription(McpTrustPreset preset) => preset switch
        {
            McpTrustPreset.ReadOnly => "The agent can inspect the project. All changes, tests, builds, Editor-state changes, and code execution are blocked.",
            McpTrustPreset.ConfirmChanges => "Inspection is automatic. Every action that changes project or Editor state asks first.",
            McpTrustPreset.ConfirmRisky => "Normal project changes and tests run automatically. Editor-state, unsaved-work, destructive, build, and code-execution actions ask first.",
            McpTrustPreset.FullTrust => "All explicitly requested actions run without trust prompts. Tool validation and hard safety checks still apply.",
            McpTrustPreset.Collaborative => "Read-only inspection stays automatic. Changes, tests, builds, code execution, and Editor-state actions ask while you were recently active in Unity or own Play Mode.",
            _ => string.Empty,
        };

        private static string TrustCategoryTitle(ToolTrustCategory category) => category switch
        {
            ToolTrustCategory.ProjectWrite => "Project changes",
            ToolTrustCategory.EditorState => "Editor state",
            ToolTrustCategory.UnsavedWork => "Unsaved work",
            ToolTrustCategory.CodeExecution => "Execute Code",
            _ => category.ToString(),
        };

        private static string TrustCategoryDescription(ToolTrustCategory category) => category switch
        {
            ToolTrustCategory.Read => "Inspect project, Editor, tools, logs, status, previews, and results without changing them.",
            ToolTrustCategory.ProjectWrite => "Create or edit normal project assets and settings using the owning typed tools.",
            ToolTrustCategory.EditorState => "Enter or stop Play Mode, pause/resume, or switch the loaded scene set.",
            ToolTrustCategory.UnsavedWork => "Replace, close, or run operations that can affect dirty scenes or other unsaved Editor work.",
            ToolTrustCategory.Destructive => "Delete, remove, overwrite, move, or perform another explicitly destructive replacement.",
            ToolTrustCategory.Tests => "Start or cancel Unity test runs. Reading existing results remains a read action.",
            ToolTrustCategory.Builds => "Start player or Addressables builds, which can take time and write generated output.",
            ToolTrustCategory.CodeExecution => "Compile and run arbitrary unsandboxed C# inside Unity with your user permissions; an infinite loop cannot be forcibly interrupted.",
            _ => string.Empty,
        };

        private void RefreshInstallCards()
        {
            _installGrid.Clear();
            var port = McpBridgeHost.Port;

            var icons = AssetDatabase.LoadAssetAtPath<Texture2D>($"{McpRootPath}/Editor/UI/Icons/client-icons.png");
            foreach (var t in s_targets)
            {
                var card = new VisualElement();
                card.AddToClassList("install-card");

                var body = new VisualElement();
                body.AddToClassList("install-card-body");
                body.EnableInClassList("hidden", !_expandedTargets.Contains(t.Name));

                var chevron = new Label(_expandedTargets.Contains(t.Name) ? "\u25BE" : "\u25B8");
                chevron.AddToClassList("install-chevron");
                var header = new Button(() =>
                {
                    var expanded = !_expandedTargets.Contains(t.Name);
                    if (expanded) _expandedTargets.Add(t.Name);
                    else _expandedTargets.Remove(t.Name);
                    body.EnableInClassList("hidden", !expanded);
                    chevron.text = expanded ? "\u25BE" : "\u25B8";
                });
                header.AddToClassList("install-card-toggle");
                var icon = new Image
                {
                    image = icons,
                    uv = new Rect(t.IconIndex * 0.25f, 0.25f, 0.25f, 0.5f),
                    scaleMode = ScaleMode.ScaleToFit,
                };
                icon.AddToClassList("install-icon");
                header.Add(icon);
                var title = new Label(t.Name);
                title.AddToClassList("install-title");
                header.Add(title);
                var configured = t.Options.Count(option => option.IsInstalledFunc(option.PathFunc()));
                var summary = new Label(configured == 0 ? "Not configured" : $"{configured} configured");
                summary.AddToClassList("install-card-summary");
                header.Add(summary);
                header.Add(chevron);
                card.Add(header);

                var desc = new Label(t.Desc);
                desc.AddToClassList("install-desc");
                body.Add(desc);

                body.Add(InstallSectionTitle("MCP Server"));

                foreach (var option in t.Options)
                {
                    var filePath = option.PathFunc();
                    var exists = option.IsInstalledFunc(filePath);

                    var row = new VisualElement();
                    row.AddToClassList("install-scope-row");
                    row.EnableInClassList("installed", exists);
                    row.EnableInClassList("not-installed", !exists);

                    var left = new VisualElement();
                    left.AddToClassList("install-scope-info");

                    var scopeHeader = new VisualElement();
                    scopeHeader.AddToClassList("install-scope-header");

                    var scopeName = new Label(option.Label);
                    scopeName.AddToClassList("install-scope-name");
                    scopeHeader.Add(scopeName);

                    var status = new Label(exists ? "Installed" : "Not installed");
                    status.AddToClassList("install-status");
                    status.EnableInClassList("installed", exists);
                    status.EnableInClassList("not-installed", !exists);
                    scopeHeader.Add(status);
                    left.Add(scopeHeader);

                    var pathLabel = new Label(BuildInstallPathText(filePath, exists, option.StatusTextFunc(filePath)));
                    pathLabel.tooltip = filePath;
                    pathLabel.AddToClassList("install-path");
                    pathLabel.EnableInClassList("not-installed", !exists);
                    left.Add(pathLabel);
                    row.Add(left);

                    var actions = new VisualElement();
                    actions.AddToClassList("install-actions");

                    var installBtn = new Button(() =>
                    {
                        option.InstallAction(filePath, port);
                        Debug.Log($"[MCP] Wrote {filePath}");
                        EditorUtility.DisplayDialog("MCP Install",
                            $"Config written to:\n{filePath}\n\nRestart {option.RestartName} to pick up the server.", "OK");
                        RefreshInstallCards();
                    });
                    installBtn.text = exists
                        ? "Reinstall"
                        : "Install";
                    installBtn.AddToClassList("install-btn");
                    installBtn.EnableInClassList("install-primary", !exists);
                    installBtn.EnableInClassList("install-secondary", exists);
                    actions.Add(installBtn);

                    var removeBtn = new Button(() =>
                    {
                        option.RemoveAction(filePath);
                        Debug.Log($"[MCP] Removed unity server from {filePath}");
                        EditorUtility.DisplayDialog("MCP Remove",
                            $"Unity MCP removed from:\n{filePath}\n\nRestart {option.RestartName} to unload the server.", "OK");
                        RefreshInstallCards();
                    });
                    removeBtn.text = "Remove";
                    removeBtn.AddToClassList("install-btn");
                    removeBtn.AddToClassList("install-danger");
                    removeBtn.style.display = exists ? DisplayStyle.Flex : DisplayStyle.None;
                    actions.Add(removeBtn);

                    row.Add(actions);
                    body.Add(row);
                }

                if (t.Name == "Codex")
                    AddSkillRows(body, "Codex", ".agents");
                else if (t.Name == "Claude Code")
                    AddSkillRows(body, "Claude Code", ".claude");

                card.Add(body);
                _installGrid.Add(card);
            }
        }

        private void AddSkillRows(VisualElement card, string agentName, string agentDirectory)
        {
            card.Add(InstallSectionTitle("Agent Skill · manual updates only"));
            var source = Path.Combine(ProjectRoot, McpRootPath, "skills", McpSkillName);
            var targets = new[]
            {
                (Label: "Project", Path: Path.Combine(ProjectRoot, agentDirectory, "skills", McpSkillName)),
                (Label: "User", Path: Path.Combine(UserProfile, agentDirectory, "skills", McpSkillName)),
            };

            foreach (var target in targets)
            {
                var installed = McpSkillInstaller.IsInstalled(target.Path);
                var managed = McpSkillInstaller.IsManaged(target.Path);
                var updateAvailable = installed && McpSkillInstaller.NeedsUpdate(source, target.Path);
                var statusText = !installed ? "Not installed" : !managed ? "External copy" : updateAvailable ? "Update available" : "Installed";

                var row = new VisualElement();
                row.AddToClassList("install-scope-row");
                row.EnableInClassList("installed", installed && !updateAvailable);
                row.EnableInClassList("update-available", updateAvailable);

                var left = new VisualElement();
                left.AddToClassList("install-scope-info");
                var scopeHeader = new VisualElement();
                scopeHeader.AddToClassList("install-scope-header");
                var scopeName = new Label(target.Label);
                scopeName.AddToClassList("install-scope-name");
                scopeHeader.Add(scopeName);
                var status = new Label(statusText);
                status.AddToClassList("install-status");
                status.EnableInClassList("installed", installed && !updateAvailable);
                status.EnableInClassList("not-installed", !installed || updateAvailable);
                status.EnableInClassList("update-available", updateAvailable);
                scopeHeader.Add(status);
                left.Add(scopeHeader);

                var pathLabel = new Label(target.Path);
                pathLabel.tooltip = target.Path;
                pathLabel.AddToClassList("install-path");
                pathLabel.EnableInClassList("not-installed", !installed);
                left.Add(pathLabel);
                row.Add(left);

                var installButton = new Button(() =>
                {
                    try
                    {
                        McpSkillInstaller.Install(source, target.Path);
                        EditorUtility.DisplayDialog($"{agentName} Skill Installed",
                            $"{McpSkillName} installed to:\n{target.Path}\n\nRestart {agentName} to load it.", "OK");
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        EditorUtility.DisplayDialog($"{agentName} Skill Install Failed", exception.Message, "OK");
                    }
                    RefreshInstallCards();
                }) { text = updateAvailable ? "Update" : installed ? managed ? "Installed" : "External copy" : "Install" };
                installButton.SetEnabled(!installed || updateAvailable);
                installButton.tooltip = installed && !managed
                    ? "This directory was not installed by Unity MCP and will not be overwritten."
                    : string.Empty;
                installButton.AddToClassList("install-btn");
                installButton.EnableInClassList("install-primary", !installed || updateAvailable);
                installButton.EnableInClassList("install-secondary", installed && !updateAvailable);
                row.Add(installButton);
                card.Add(row);
            }
        }

        private static Label InstallSectionTitle(string text)
        {
            var label = new Label(text);
            label.AddToClassList("install-section-title");
            return label;
        }

        // ─── Live Refresh ───────────────────────────────────────

        private double _lastRefresh;

        private void RefreshLive()
        {
            if (EditorApplication.timeSinceStartup - _lastRefresh < 1.0) return;
            _lastRefresh = EditorApplication.timeSinceStartup;

            if (_statusPill == null) return;

            var running = McpBridgeHost.IsRunning;
            _statusPill.text = running ? "Running" : "Stopped";
            _statusPill.EnableInClassList("running", running);
            _statusPill.EnableInClassList("stopped", !running);

            if (_activeTab != 1 || _portValue == null) return;
            _portValue.text = McpBridgeHost.Port.ToString();
            _urlValue.text = $"http://127.0.0.1:{McpBridgeHost.Port}/mcp";
            _playingValue.text = McpBridgeHost.CachedIsPlaying ? "Yes" : "No";
            _compilingValue.text = McpBridgeHost.CachedIsCompiling ? "Yes" : "No";
            var registry = GetRegistry();
            if (registry != null)
            {
                var all = registry.Enumerate().ToList();
                var available = registry.EnumerateAvailable(
                    McpBridgeHost.CachedIsPlaying, McpBridgeHost.CachedIsCompiling).ToList();
                var availableSet = new System.Collections.Generic.HashSet<string>(
                    available.Select(a => a.Name));
                _toolCountValue.text = $"{availableSet.Count} / {all.Count} available";

                // Rebuild tool list if count changed
                if (_toolRevision != registry.Revision)
                {
                    _toolRevision = registry.Revision;
                    _toolList.Clear();
                    foreach (var t in all.OrderBy(x => x.Name))
                    {
                        var row = new VisualElement();
                        row.AddToClassList("tool-row");
                        row.userData = t.Name;
                        row.tooltip = t.Name + " — " + t.Description;

                        var dot = new VisualElement();
                        dot.AddToClassList("tool-dot");
                        row.Add(dot);

                        var name = new Label(t.Name);
                        name.AddToClassList("tool-name");
                        row.Add(name);

                        var desc = new Label(t.Description);
                        desc.AddToClassList("tool-desc");
                        row.Add(desc);

                        _toolList.Add(row);
                    }
                    FilterTools();
                }

                // Update availability dots every refresh (state can change without tool count changing)
                foreach (var row in _toolList.Children())
                {
                    var dot = row.ElementAt(0);
                    var isAvail = availableSet.Contains((string)row.userData);
                    dot.EnableInClassList("available", isAvail);
                    dot.EnableInClassList("unavailable", !isAvail);
                    dot.tooltip = isAvail ? "Available" : "Unavailable in current Editor state";
                }
            }
            else
            {
                _toolCountValue.text = "—";
            }
        }

        private void FilterTools()
        {
            foreach (var row in _toolList.Children())
                row.EnableInClassList("hidden", row.tooltip.IndexOf(_toolSearch.value ?? "", StringComparison.OrdinalIgnoreCase) < 0);
        }

        // ─── Helpers ────────────────────────────────────────────

        private static VisualElement SectionHeader(string text)
        {
            var lbl = new Label(text);
            lbl.AddToClassList("section-header");
            return lbl;
        }

        private static VisualElement InfoRow(string label, out Label valueLabel, string initialValue = "—", bool mono = false)
        {
            var row = new VisualElement();
            row.AddToClassList("info-row");
            var lbl = new Label(label);
            lbl.AddToClassList("info-label");
            row.Add(lbl);
            valueLabel = new Label(initialValue);
            valueLabel.AddToClassList("info-value");
            if (mono) valueLabel.AddToClassList("mono");
            row.Add(valueLabel);
            return row;
        }

        private static ToolRegistry GetRegistry()
        {
            var field = typeof(McpBridgeHost).GetField("s_registry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return field?.GetValue(null) as ToolRegistry;
        }

        private enum InstallConfigKind
        {
            FileExists,
            Codex,
            OpenCode,
            ClaudeCode,
            Cursor
        }

        private static string BuildInstallStatusText(
            string path,
            string serverName,
            InstallConfigKind kind = InstallConfigKind.FileExists,
            string label = null)
        {
            var prefix = string.IsNullOrEmpty(label) ? string.Empty : label + ": ";

            if (!File.Exists(path))
                return prefix + "Not installed";

            if (kind == InstallConfigKind.Codex)
            {
                var hasServer = McpClientConfigUtility.HasCodexServerConfig(File.ReadAllText(path));
                return hasServer
                    ? $"{prefix}\u2713 {path}"
                    : $"{prefix}Config exists, {serverName} is not configured: {path}";
            }

            if (kind == InstallConfigKind.OpenCode)
            {
                var hasServer = McpClientConfigUtility.HasOpenCodeServerConfig(File.ReadAllText(path));
                return hasServer
                    ? $"{prefix}\u2713 {path}"
                    : $"{prefix}Config exists, {serverName} is not configured: {path}";
            }

            if (kind == InstallConfigKind.ClaudeCode)
                return BuildJsonInstallStatusText(path, serverName, prefix, McpClientConfigUtility.HasClaudeCodeServerConfig);

            if (kind == InstallConfigKind.Cursor)
                return BuildJsonInstallStatusText(path, serverName, prefix, McpClientConfigUtility.HasCursorServerConfig);

            return $"{prefix}\u2713 {path}";
        }

        private static string BuildJsonInstallStatusText(
            string path,
            string serverName,
            string prefix,
            Func<string, bool> hasServer)
        {
            var configured = hasServer(File.ReadAllText(path));
            return configured
                ? $"{prefix}\u2713 {path}"
                : $"{prefix}Config exists, {serverName} is not configured: {path}";
        }

        private static string BuildInstallPathText(string path, bool installed, string statusText)
        {
            if (installed)
                return path;

            return statusText.Contains("Config exists", StringComparison.Ordinal)
                ? "Config file exists without unity server"
                : path;
        }

        private static string UserProfile
            => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    }

    internal static class VisualElementExt
    {
        public static T With<T>(this T element, Action<T> configure) where T : VisualElement
        {
            configure(element);
            return element;
        }
    }
}
