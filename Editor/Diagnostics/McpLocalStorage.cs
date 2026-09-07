using System.IO;
using System.Text;

namespace LittleBrushGames.Mcp.Editor.Diagnostics
{
    internal static class McpLocalStorage
    {
        // Establish the exclusion before creating any journal, temporary or recovery file.
        internal static void EnsureDirectory(string directory)
        {
            Directory.CreateDirectory(directory);
            var ignore = Path.Combine(directory, ".gitignore");
            if (!File.Exists(ignore) || File.ReadAllText(ignore).Trim() != "*")
                File.WriteAllText(ignore, "*\n", new UTF8Encoding(false));
        }
    }
}
