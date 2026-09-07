using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    /// <summary>
    /// Captures compiler messages from <see cref="CompilationPipeline"/> and persists them to
    /// <c>Library/LittleBrushGames.Mcp/CompileState.json</c> so they survive domain reloads and
    /// Editor restarts. Also tracks an input hash of the script set compiled in the last pass,
    /// enabling callers to decide whether a fresh compile is required without guessing.
    /// </summary>
    [InitializeOnLoad]
    public static class CompileErrorStore
    {
        public readonly struct Message
        {
            public string Assembly { get; }
            public string File { get; }
            public int Line { get; }
            public int Column { get; }
            public string Text { get; }
            public CompilerMessageType Type { get; }
            public long Timestamp { get; }

            public Message(string assembly, CompilerMessage m, long timestamp)
            {
                Assembly = assembly;
                File = m.file;
                Line = m.line;
                Column = m.column;
                Text = m.message;
                Type = m.type;
                Timestamp = timestamp;
            }

            public Message(string assembly, string file, int line, int column, string text, CompilerMessageType type, long timestamp)
            {
                Assembly = assembly;
                File = file;
                Line = line;
                Column = column;
                Text = text;
                Type = type;
                Timestamp = timestamp;
            }
        }

        private const int StateVersion = 1;

        private static readonly object s_lock = new();
        private static readonly List<Message> s_current = new();
        private static List<Message> s_lastCompleted = new();
        private static long s_lastFinishedAt;
        private static long s_lastStartedAt;
        private static int s_passCounter;
        private static string s_lastInputHash;
        private static string s_pendingInputHash;

        public static long LastFinishedAt { get { lock (s_lock) return s_lastFinishedAt; } }
        public static long LastStartedAt { get { lock (s_lock) return s_lastStartedAt; } }
        public static int PassCounter { get { lock (s_lock) return Volatile.Read(ref s_passCounter); } }
        public static string LastInputHash { get { lock (s_lock) return s_lastInputHash; } }

        static CompileErrorStore()
        {
            LoadFromDisk();
            CompilationPipeline.compilationStarted += OnStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
            CompilationPipeline.compilationFinished += OnFinished;
            AssemblyReloadEvents.beforeAssemblyReload += PersistToDisk;
        }

        public static Message[] Snapshot()
        {
            lock (s_lock)
                return s_lastCompleted.ToArray();
        }

        /// <summary>
        /// Fingerprints imported sources and on-disk compilation inputs, including files
        /// added before AssetDatabase refresh. Must be called on the main thread.
        /// </summary>
        public static string ComputeInputHash()
        {
            var inputs = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                foreach (var source in assembly.sourceFiles ?? Array.Empty<string>())
                    inputs.Add(Path.GetFullPath(source));
                string definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                if (!string.IsNullOrEmpty(definition))
                    inputs.Add(Path.GetFullPath(definition));
            }
            CollectCompilationInputs("Assets", inputs);
            CollectCompilationInputs("Packages", inputs);
            foreach (string path in new[] { "Packages/manifest.json", "Packages/packages-lock.json",
                         "ProjectSettings/ProjectSettings.asset", "ProjectSettings/ProjectVersion.txt" })
                inputs.Add(Path.GetFullPath(path));
            return HashInputs(inputs);
        }

        private static void CollectCompilationInputs(string directory, ISet<string> inputs)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string source = file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(0, file.Length - 5) : file;
                string extension = Path.GetExtension(source).ToLowerInvariant();
                if (extension is ".cs" or ".asmdef" or ".asmref" or ".rsp" or ".dll")
                    inputs.Add(Path.GetFullPath(file));
            }
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (!name.StartsWith(".", StringComparison.Ordinal) && !name.EndsWith("~", StringComparison.Ordinal))
                    CollectCompilationInputs(child, inputs);
            }
        }

        private static string HashInputs(IEnumerable<string> inputs)
        {
            var sb = new StringBuilder(64 * 1024);
            foreach (string input in inputs)
            {
                var file = new FileInfo(input);
                sb.Append(input).Append('|');
                if (file.Exists)
                    sb.Append(file.LastWriteTimeUtc.Ticks).Append('|').Append(file.Length);
                sb.Append('\n');
            }
            using var sha = SHA1.Create();
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        private static void OnStarted(object _)
        {
            string hash;
            try { hash = ComputeInputHash(); }
            catch { hash = null; }
            lock (s_lock)
            {
                s_current.Clear();
                s_lastStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                s_pendingInputHash = hash;
            }
        }

        private static void OnAssemblyFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var assembly = Path.GetFileNameWithoutExtension(assemblyPath);
            lock (s_lock)
            {
                foreach (var m in messages)
                    s_current.Add(new Message(assembly, m, ts));
            }
        }

        private static void OnFinished(object _)
        {
            lock (s_lock)
            {
                s_lastCompleted = new List<Message>(s_current);
                s_current.Clear();
                s_lastFinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                s_lastInputHash = s_pendingInputHash;
                Interlocked.Increment(ref s_passCounter);
            }
            PersistToDisk();
        }

        // ---- Persistence -------------------------------------------------------

        private static string StatePath
        {
            get
            {
                var project = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
                return Path.Combine(project, "Library", "LittleBrushGames.Mcp", "CompileState.json");
            }
        }

        private static void PersistToDisk()
        {
            try
            {
                JObject payload;
                lock (s_lock)
                {
                    var messages = new JArray();
                    foreach (var m in s_lastCompleted)
                    {
                        messages.Add(new JObject
                        {
                            ["assembly"] = m.Assembly,
                            ["file"] = m.File,
                            ["line"] = m.Line,
                            ["column"] = m.Column,
                            ["message"] = m.Text,
                            ["type"] = m.Type.ToString(),
                            ["timestamp"] = m.Timestamp,
                        });
                    }
                    payload = new JObject
                    {
                        ["version"] = StateVersion,
                        ["passCounter"] = s_passCounter,
                        ["lastStartedAt"] = s_lastStartedAt,
                        ["lastFinishedAt"] = s_lastFinishedAt,
                        ["lastInputHash"] = s_lastInputHash,
                        ["messages"] = messages,
                    };
                }

                var path = StatePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                // Best-effort persistence; never surface errors into the Editor UI.
            }
        }

        private static void LoadFromDisk()
        {
            try
            {
                var path = StatePath;
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path, Encoding.UTF8);
                var json = JObject.Parse(text);
                if ((int?)json["version"] != StateVersion) return;

                var restored = new List<Message>();
                if (json["messages"] is JArray arr)
                {
                    foreach (var t in arr)
                    {
                        var typeStr = (string)t["type"];
                        if (!Enum.TryParse<CompilerMessageType>(typeStr, out var type)) continue;
                        restored.Add(new Message(
                            assembly: (string)t["assembly"] ?? string.Empty,
                            file: (string)t["file"] ?? string.Empty,
                            line: (int?)t["line"] ?? 0,
                            column: (int?)t["column"] ?? 0,
                            text: (string)t["message"] ?? string.Empty,
                            type: type,
                            timestamp: (long?)t["timestamp"] ?? 0L));
                    }
                }

                lock (s_lock)
                {
                    s_passCounter = (int?)json["passCounter"] ?? 0;
                    s_lastStartedAt = (long?)json["lastStartedAt"] ?? 0;
                    s_lastFinishedAt = (long?)json["lastFinishedAt"] ?? 0;
                    s_lastInputHash = (string)json["lastInputHash"];
                    s_lastCompleted = restored;
                }
            }
            catch
            {
                // Corrupt or unreadable state is not fatal — next compile will rewrite it.
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var c = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                c[i * 2] = Hex(b >> 4);
                c[i * 2 + 1] = Hex(b & 0xF);
            }
            return new string(c);
            static char Hex(int v) => (char)(v < 10 ? '0' + v : 'a' + (v - 10));
        }
    }
}
