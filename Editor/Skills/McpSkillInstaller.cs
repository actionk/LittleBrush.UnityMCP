using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LittleBrushGames.Mcp.Editor.Skills
{
    public static class McpSkillInstaller
    {
        public const string ManagedMarkerFileName = ".littlebrush-unity-mcp-managed";

        public static bool IsInstalled(string targetDirectory)
            => File.Exists(Path.Combine(targetDirectory, "SKILL.md"));

        public static bool IsManaged(string targetDirectory)
            => IsInstalled(targetDirectory) && File.Exists(Path.Combine(targetDirectory, ManagedMarkerFileName));

        public static bool NeedsUpdate(string sourceDirectory, string targetDirectory)
            => IsManaged(targetDirectory) && !IsSamePath(sourceDirectory, targetDirectory) &&
               ComputeSkillHash(sourceDirectory) != ComputeSkillHash(targetDirectory);

        public static bool IsSamePath(string left, string right)
            => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
               string.Equals(
                   Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);

        public static void Install(string sourceDirectory, string targetDirectory)
        {
            if (!File.Exists(Path.Combine(sourceDirectory, "SKILL.md")))
                throw new FileNotFoundException("Skill source must contain SKILL.md.", sourceDirectory);

            var target = Path.GetFullPath(targetDirectory);
            if (IsSamePath(sourceDirectory, target))
                throw new InvalidOperationException("Skill source and target must be different directories.");
            if (Directory.Exists(target) && !IsManaged(target))
                throw new InvalidOperationException($"Refusing to replace an unmanaged skill directory: {target}");

            var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            var backup = target + ".backup-" + Guid.NewGuid().ToString("N");
            try
            {
                CopyDirectory(sourceDirectory, temporary);
                File.WriteAllText(Path.Combine(temporary, ManagedMarkerFileName), "Managed by LittleBrushGames.UnityMCP.\n");
                if (Directory.Exists(target))
                    Directory.Move(target, backup);
                Directory.Move(temporary, target);
                if (Directory.Exists(backup))
                    Directory.Delete(backup, true);
            }
            catch
            {
                if (!Directory.Exists(target) && Directory.Exists(backup))
                    Directory.Move(backup, target);
                throw;
            }
            finally
            {
                if (Directory.Exists(temporary))
                    Directory.Delete(temporary, true);
                if (Directory.Exists(backup) && Directory.Exists(target))
                    Directory.Delete(backup, true);
            }
        }

        private static void CopyDirectory(string sourceDirectory, string targetDirectory)
        {
            var sourceRoot = EnsureTrailingSeparator(Path.GetFullPath(sourceDirectory));
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                         .Where(ShouldCopy))
            {
                var destination = Path.Combine(targetDirectory, file.Substring(sourceRoot.Length));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }

        private static bool ShouldCopy(string path)
            => !string.Equals(Path.GetFileName(path), ManagedMarkerFileName, StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
               !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Any(part => string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase));

        private static string EnsureTrailingSeparator(string path)
            => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        private static string ComputeSkillHash(string directory)
        {
            var root = EnsureTrailingSeparator(Path.GetFullPath(directory));
            var manifest = string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(ShouldCopy)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => path.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/') + ":" + ComputeFileHash(path)));

            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(manifest))).Replace("-", "").ToLowerInvariant();
        }

        private static string ComputeFileHash(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
