using System;
using System.IO;
using LittleBrushGames.Mcp.Editor.Skills;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Skills
{
    public sealed class McpSkillInstallerTests
    {
        private string _temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            _temporaryDirectory = Path.Combine(Path.GetTempPath(), "McpSkillInstallerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, true);
        }

        [Test]
        public void Install_CopiesSkillAndSkipsUnityMetadata()
        {
            var source = Path.Combine(_temporaryDirectory, "source");
            var target = Path.Combine(_temporaryDirectory, "target", "unity-mcp");
            Write(Path.Combine(source, "SKILL.md"), "skill");
            Write(Path.Combine(source, "references", "guide.md"), "guide");
            Write(Path.Combine(source, "SKILL.md.meta"), "metadata");

            McpSkillInstaller.Install(source, target);

            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(target, "references", "guide.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md.meta")), Is.False);
            Assert.That(McpSkillInstaller.IsManaged(target), Is.True);
        }

        [Test]
        public void Install_RefusesToReplaceUnmanagedDirectory()
        {
            var source = Path.Combine(_temporaryDirectory, "source");
            var target = Path.Combine(_temporaryDirectory, "target", "unity-mcp");
            Write(Path.Combine(source, "SKILL.md"), "bundled");
            Write(Path.Combine(target, "SKILL.md"), "user-owned");

            Assert.That(
                () => McpSkillInstaller.Install(source, target),
                Throws.InvalidOperationException.With.Message.Contains("unmanaged"));
            Assert.That(File.ReadAllText(Path.Combine(target, "SKILL.md")), Is.EqualTo("user-owned"));
        }

        [Test]
        public void NeedsUpdate_DetectsChangedSkillFile()
        {
            var source = Path.Combine(_temporaryDirectory, "source");
            var target = Path.Combine(_temporaryDirectory, "target", "unity-mcp");
            Write(Path.Combine(source, "SKILL.md"), "skill");
            Write(Path.Combine(source, "references", "guide.md"), "old");
            McpSkillInstaller.Install(source, target);

            Assert.That(McpSkillInstaller.NeedsUpdate(source, target), Is.False);

            Write(Path.Combine(source, "references", "guide.md"), "new");

            Assert.That(McpSkillInstaller.NeedsUpdate(source, target), Is.True);
        }

        [Test]
        public void Install_UpdatesManagedCopy()
        {
            var source = Path.Combine(_temporaryDirectory, "source");
            var installed = Path.Combine(_temporaryDirectory, "installed", "unity-mcp");
            Write(Path.Combine(source, "SKILL.md"), "old");
            McpSkillInstaller.Install(source, installed);
            Write(Path.Combine(source, "SKILL.md"), "new");

            McpSkillInstaller.Install(source, installed);

            Assert.That(File.ReadAllText(Path.Combine(installed, "SKILL.md")), Is.EqualTo("new"));
            Assert.That(McpSkillInstaller.IsManaged(installed), Is.True);
        }

        private static void Write(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
        }
    }
}
