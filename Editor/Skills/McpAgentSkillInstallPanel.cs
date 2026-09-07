using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace LittleBrushGames.Mcp.Editor.Skills
{
    public sealed class McpAgentSkillInstallPanel : VisualElement
    {
        private readonly string _skillName;
        private readonly string _sourceDirectory;

        public McpAgentSkillInstallPanel(string skillName, string sourceDirectory)
        {
            _skillName = skillName;
            _sourceDirectory = sourceDirectory;
            AddToClassList("lbg-skill-panel");
            Build();
        }

        private void Build()
        {
            Clear();
            var sourceExists = File.Exists(Path.Combine(_sourceDirectory, "SKILL.md"));

            var header = new VisualElement();
            header.AddToClassList("lbg-skill-panel__header");
            var titleGroup = new VisualElement();
            var title = new Label("Agent Skills");
            title.AddToClassList("lbg-skill-panel__title");
            titleGroup.Add(title);
            var subtitle = new Label("Install bundled skills into Codex or Claude Code.");
            subtitle.AddToClassList("lbg-skill-panel__subtitle");
            titleGroup.Add(subtitle);
            header.Add(titleGroup);
            var hint = new Label("Copies the full skill folder and skips Unity .meta files.");
            hint.AddToClassList("lbg-skill-panel__hint");
            header.Add(hint);
            Add(header);

            var targets = new[]
            {
                new SkillTarget("Codex", "Project", Path.Combine(ProjectRoot, ".agents", "skills", _skillName)),
                new SkillTarget("Codex", "User", Path.Combine(UserProfile, ".agents", "skills", _skillName)),
                new SkillTarget("Claude Code", "Project", Path.Combine(ProjectRoot, ".claude", "skills", _skillName)),
                new SkillTarget("Claude Code", "User", Path.Combine(UserProfile, ".claude", "skills", _skillName))
            };

            var table = new VisualElement();
            table.AddToClassList("lbg-skill-table");
            table.Add(HeaderRow(targets));

            var row = new VisualElement();
            row.AddToClassList("lbg-skill-table__row");
            row.Add(SkillCell(sourceExists));
            foreach (var target in targets)
                row.Add(ScopeCell(target, sourceExists));
            table.Add(row);
            Add(table);

            var footer = new VisualElement();
            footer.AddToClassList("lbg-skill-panel__footer");
            var refresh = new Button(Build) { text = "Refresh" };
            refresh.AddToClassList("lbg-btn");
            footer.Add(refresh);
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            footer.Add(spacer);
            foreach (var target in targets)
                footer.Add(FolderButton("Open " + DisplayAgentName(target) + " " + target.Scope, Path.GetDirectoryName(target.Path)));
            Add(footer);
        }

        private VisualElement HeaderRow(SkillTarget[] targets)
        {
            var row = new VisualElement();
            row.AddToClassList("lbg-skill-table__header");
            row.Add(Cell("Skill", "lbg-skill-table__skill"));
            foreach (var target in targets)
            {
                var cell = new VisualElement();
                cell.AddToClassList("lbg-skill-table__scope");
                var title = new Label(DisplayAgentName(target) + " " + target.Scope);
                title.AddToClassList("lbg-skill-scope-header__title");
                cell.Add(title);
                var path = new Label(ShortPath(Path.GetDirectoryName(target.Path)));
                path.AddToClassList("lbg-skill-scope-header__path");
                path.tooltip = Path.GetDirectoryName(target.Path);
                cell.Add(path);
                row.Add(cell);
            }
            return row;
        }

        private VisualElement SkillCell(bool sourceExists)
        {
            var cell = new VisualElement();
            cell.AddToClassList("lbg-skill-table__skill");
            var top = new VisualElement();
            top.AddToClassList("lbg-skill-cell__top");
            var icon = new Label("{}");
            icon.AddToClassList("lbg-skill-cell__icon");
            top.Add(icon);
            var text = new VisualElement();
            text.AddToClassList("lbg-skill-cell__text");
            var name = new Label("LittleBrush Unity MCP");
            name.AddToClassList("lbg-skill-cell__name");
            text.Add(name);
            var path = new Label(sourceExists ? ShortPath(_sourceDirectory) : "Missing SKILL.md");
            path.AddToClassList("lbg-skill-cell__path");
            path.tooltip = _sourceDirectory;
            text.Add(path);
            top.Add(text);
            var open = new Button(() => OpenSkill(_sourceDirectory)) { text = "Open Skill" };
            open.SetEnabled(sourceExists);
            open.AddToClassList("lbg-btn");
            open.AddToClassList("lbg-skill-cell__open");
            top.Add(open);
            cell.Add(top);
            return cell;
        }

        private VisualElement ScopeCell(SkillTarget target, bool sourceExists)
        {
            var installed = McpSkillInstaller.IsInstalled(target.Path);
            var managed = McpSkillInstaller.IsManaged(target.Path);
            var updateAvailable = sourceExists && installed && McpSkillInstaller.NeedsUpdate(_sourceDirectory, target.Path);
            var cell = new VisualElement();
            cell.AddToClassList("lbg-skill-table__scope");
            var status = new Label(!installed ? "Not installed" : !managed ? "External copy" : updateAvailable ? "Update available" : "Installed");
            status.AddToClassList("lbg-skill-status");
            status.AddToClassList(updateAvailable ? "lbg-skill-status--update" : installed ? "lbg-skill-status--installed" : "lbg-skill-status--missing");
            status.tooltip = target.Path;
            cell.Add(status);
            var actions = new VisualElement();
            actions.AddToClassList("lbg-skill-actions");
            var canInstall = sourceExists && (!installed || updateAvailable);
            var install = new Button(() => TryInstall(target)) { text = updateAvailable ? "Update" : installed ? managed ? "Installed" : "External copy" : "Install" };
            install.SetEnabled(canInstall);
            install.tooltip = canInstall
                ? string.Empty
                : installed && !managed
                    ? "This directory was not installed by Unity MCP and will not be overwritten."
                    : installed
                        ? "This skill is already installed in this scope."
                        : "Skill source is missing.";
            install.AddToClassList("lbg-btn");
            install.AddToClassList("lbg-btn--primary");
            actions.Add(install);
            var copy = new Button(() => EditorGUIUtility.systemCopyBuffer = target.Path) { text = "Copy Path" };
            copy.AddToClassList("lbg-btn");
            actions.Add(copy);
            cell.Add(actions);
            return cell;
        }

        private void TryInstall(SkillTarget target)
        {
            try
            {
                McpSkillInstaller.Install(_sourceDirectory, target.Path);
                EditorUtility.DisplayDialog(target.Agent + " Skill Installed", $"{_skillName} installed to:\n{target.Path}\n\nRestart {target.Agent} to load it.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(target.Agent + " Skill Install Failed", exception.Message, "OK");
            }
            Build();
        }

        private static VisualElement Cell(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        private static Button FolderButton(string text, string path)
        {
            var button = new Button(() =>
            {
                Directory.CreateDirectory(path);
                EditorUtility.RevealInFinder(path);
            }) { text = text };
            button.AddToClassList("lbg-btn");
            return button;
        }

        private static void OpenSkill(string sourceDirectory)
        {
            var skillPath = Path.Combine(sourceDirectory, "SKILL.md");
            if (File.Exists(skillPath))
                UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(skillPath, 1);
        }

        private static string ShortPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var projectRoot = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullPath.StartsWith(UserProfile, StringComparison.OrdinalIgnoreCase)
                ? "~" + fullPath.Substring(UserProfile.Length)
                : fullPath;
        }

        private static string DisplayAgentName(SkillTarget target)
            => string.Equals(target.Agent, "Claude Code", StringComparison.Ordinal) ? "Claude" : target.Agent;

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private readonly struct SkillTarget
        {
            public readonly string Agent;
            public readonly string Scope;
            public readonly string Path;

            public SkillTarget(string agent, string scope, string path)
            {
                Agent = agent;
                Scope = scope;
                Path = path;
            }
        }
    }
}
