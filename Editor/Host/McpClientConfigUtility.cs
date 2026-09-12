using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Host
{
    public static class McpClientConfigUtility
    {
        public const string DefaultServerName = "unity";

        public static string BuildClaudeCodeConfig(int port)
            => UpsertClaudeCodeConfig(string.Empty, port);

        public static string BuildCursorConfig(int port)
            => UpsertCursorConfig(string.Empty, port);

        public static string BuildVsCodeConfig(int port)
            => UpsertVsCodeConfig(string.Empty, port);

        public static string BuildWindsurfConfig(int port)
            => UpsertWindsurfConfig(string.Empty, port);

        public static string BuildCodexBlock(int port)
            => "[mcp_servers." + DefaultServerName + "]\nurl = \"" + BuildServerUrl(port) + "\"\n";

        public static string BuildOpenCodeConfig(int port)
            => UpsertOpenCodeServerConfig(string.Empty, port);

        public static string BuildServerUrl(int port)
            => "http://127.0.0.1:" + port + "/mcp";

        public static bool HasCodexServerConfig(string content)
        {
            if (string.IsNullOrEmpty(content))
                return false;

            return FindCodexBlock(content).Success;
        }

        public static string UpsertCodexServerConfig(string existingContent, int port)
        {
            var newline = DetectNewline(existingContent);
            var header = "[mcp_servers." + DefaultServerName + "]";
            var urlLine = "url = \"" + BuildServerUrl(port) + "\"";

            if (string.IsNullOrWhiteSpace(existingContent))
                return header + newline + urlLine + newline;

            var match = FindCodexBlock(existingContent);
            if (!match.Success)
            {
                var trimmed = existingContent.TrimEnd('\r', '\n');
                return trimmed + newline + newline + header + newline + urlLine + newline;
            }

            var block = existingContent.Substring(match.Index, match.Length);
            var updatedBlock = UpsertCodexUrlLine(block, header, urlLine, newline);
            return existingContent.Substring(0, match.Index) + updatedBlock + existingContent.Substring(match.Index + match.Length);
        }

        public static string RemoveCodexServerConfig(string existingContent)
        {
            if (string.IsNullOrWhiteSpace(existingContent))
                return existingContent ?? string.Empty;

            var match = FindCodexBlock(existingContent);
            if (!match.Success)
                return existingContent;

            var before = existingContent.Substring(0, match.Index).TrimEnd('\r', '\n');
            var after = existingContent.Substring(match.Index + match.Length).TrimStart('\r', '\n');
            var newline = DetectNewline(existingContent);

            if (string.IsNullOrEmpty(before))
                return string.IsNullOrEmpty(after) ? string.Empty : after.TrimEnd('\r', '\n') + newline;

            if (string.IsNullOrEmpty(after))
                return before + newline;

            return before + newline + newline + after.TrimEnd('\r', '\n') + newline;
        }

        public static void WriteCodexProjectConfig(string path, int port)
            => WriteCodexConfig(path, port);

        public static void WriteCodexConfig(string path, int port)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var content = UpsertCodexServerConfig(existing, port);
            File.WriteAllText(path, content);
        }

        public static void RemoveCodexConfig(string path)
            => WriteExistingConfig(path, RemoveCodexServerConfig);

        public static bool HasClaudeCodeServerConfig(string content)
            => HasJsonServerConfig(content, "mcpServers");

        public static string UpsertClaudeCodeConfig(string existingContent, int port)
            => UpsertHttpServerConfig(existingContent, port, "mcpServers");

        public static void WriteClaudeCodeConfig(string path, int port)
            => WriteJsonConfig(path, existing => UpsertClaudeCodeConfig(existing, port));

        public static string RemoveClaudeCodeConfig(string existingContent)
            => RemoveJsonServerConfig(existingContent, "mcpServers");

        public static void RemoveClaudeCodeConfigFile(string path)
            => WriteExistingConfig(path, RemoveClaudeCodeConfig);

        public static bool HasCursorServerConfig(string content)
            => HasJsonServerConfig(content, "mcpServers");

        public static string UpsertCursorConfig(string existingContent, int port)
            => UpsertHttpServerConfig(existingContent, port, "mcpServers");

        public static void WriteCursorConfig(string path, int port)
            => WriteJsonConfig(path, existing => UpsertCursorConfig(existing, port));

        public static string RemoveCursorConfig(string existingContent)
            => RemoveJsonServerConfig(existingContent, "mcpServers");

        public static void RemoveCursorConfigFile(string path)
            => WriteExistingConfig(path, RemoveCursorConfig);

        public static bool HasVsCodeServerConfig(string content)
            => HasJsonServerConfig(content, "servers");

        public static string UpsertVsCodeConfig(string existingContent, int port)
            => UpsertHttpServerConfig(existingContent, port, "servers");

        public static void WriteVsCodeConfig(string path, int port)
            => WriteJsonConfig(path, existing => UpsertVsCodeConfig(existing, port));

        public static string RemoveVsCodeConfig(string existingContent)
            => RemoveJsonServerConfig(existingContent, "servers");

        public static void RemoveVsCodeConfigFile(string path)
            => WriteExistingConfig(path, RemoveVsCodeConfig);

        public static bool HasWindsurfServerConfig(string content)
            => HasJsonServerConfig(content, "mcpServers");

        public static string UpsertWindsurfConfig(string existingContent, int port)
            => UpsertJsonServerConfig(existingContent, port, "mcpServers", "serverUrl", null, false, null);

        public static void WriteWindsurfConfig(string path, int port)
            => WriteJsonConfig(path, existing => UpsertWindsurfConfig(existing, port));

        public static string RemoveWindsurfConfig(string existingContent)
            => RemoveJsonServerConfig(existingContent, "mcpServers");

        public static void RemoveWindsurfConfigFile(string path)
            => WriteExistingConfig(path, RemoveWindsurfConfig);

        public static bool HasOpenCodeServerConfig(string content)
            => HasJsonServerConfig(content, "mcp");

        public static string UpsertOpenCodeServerConfig(string existingContent, int port)
            => UpsertJsonServerConfig(existingContent, port, "mcp", "url", "remote", true, "https://opencode.ai/config.json");

        public static void WriteOpenCodeConfig(string path, int port)
            => WriteJsonConfig(path, existing => UpsertOpenCodeServerConfig(existing, port));

        public static string RemoveOpenCodeServerConfig(string existingContent)
            => RemoveJsonServerConfig(existingContent, "mcp");

        public static void RemoveOpenCodeConfig(string path)
            => WriteExistingConfig(path, RemoveOpenCodeServerConfig);

        private static string UpsertHttpServerConfig(string existingContent, int port, string sectionName)
            => UpsertJsonServerConfig(existingContent, port, sectionName, "url", "http", false, null);

        private static bool HasJsonServerConfig(string content, string sectionName)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            try
            {
                var root = JObject.Parse(content);
                return root[sectionName] is JObject section && section[DefaultServerName] != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string UpsertJsonServerConfig(
            string existingContent,
            int port,
            string sectionName,
            string urlPropertyName,
            string type,
            bool enabled,
            string schema)
        {
            var root = string.IsNullOrWhiteSpace(existingContent)
                ? new JObject()
                : JObject.Parse(existingContent);

            if (!string.IsNullOrEmpty(schema) && root["$schema"] == null)
                root.AddFirst(new JProperty("$schema", schema));

            if (root[sectionName] is not JObject section)
            {
                section = new JObject();
                root[sectionName] = section;
            }

            var server = new JObject();
            if (!string.IsNullOrEmpty(type))
                server["type"] = type;

            server[urlPropertyName] = BuildServerUrl(port);

            if (enabled)
                server["enabled"] = true;

            section[DefaultServerName] = server;

            var newline = DetectNewline(existingContent);
            return NormalizeNewlines(root.ToString(Formatting.Indented), newline) + newline;
        }

        private static void WriteJsonConfig(string path, Func<string, string> upsert)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            File.WriteAllText(path, upsert(existing));
        }

        private static string RemoveJsonServerConfig(string existingContent, string sectionName)
        {
            if (string.IsNullOrWhiteSpace(existingContent))
                return existingContent ?? string.Empty;

            var root = JObject.Parse(existingContent);
            if (root[sectionName] is JObject section)
                section.Property(DefaultServerName)?.Remove();

            var newline = DetectNewline(existingContent);
            return NormalizeNewlines(root.ToString(Formatting.Indented), newline) + newline;
        }

        private static void WriteExistingConfig(string path, Func<string, string> remove)
        {
            if (!File.Exists(path))
                return;

            var existing = File.ReadAllText(path);
            File.WriteAllText(path, remove(existing));
        }

        private static Match FindCodexBlock(string content)
        {
            return Regex.Match(
                content,
                @"(?ms)^\[mcp_servers\." + DefaultServerName + @"\]\s*$.*?(?=^\[|\z)");
        }

        private static string UpsertCodexUrlLine(string block, string header, string urlLine, string newline)
        {
            var normalized = block.Replace("\r\n", "\n").Replace('\r', '\n');
            var urlPattern = new Regex(@"(?m)^\s*url\s*=.*$");
            var hasUrlLine = urlPattern.IsMatch(normalized);
            var updated = urlPattern.Replace(normalized, urlLine, 1);

            if (!hasUrlLine)
            {
                updated = normalized.TrimEnd('\n') + "\n" + urlLine + "\n";
            }

            updated = updated.Replace("\n", newline);
            if (!updated.EndsWith(newline, StringComparison.Ordinal))
                updated += newline;

            if (!updated.StartsWith(header, StringComparison.Ordinal))
                updated = header + newline + urlLine + newline;

            return updated;
        }

        private static string NormalizeNewlines(string content, string newline)
        {
            return content
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Replace("\n", newline);
        }

        private static string DetectNewline(string content)
        {
            if (string.IsNullOrEmpty(content))
                return Environment.NewLine;

            var idx = content.IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
                return "\r\n";

            return content.IndexOf('\n') >= 0 ? "\n" : Environment.NewLine;
        }
    }
}
