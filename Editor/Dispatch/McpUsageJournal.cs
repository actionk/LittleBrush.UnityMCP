using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    // Local, bounded evidence for tool-coverage reviews; independent of Console logging.
    internal sealed class McpUsageJournal : IDisposable
    {
        internal const int MaxTools = 1024;
        internal const int MaxExecutions = 64;
        private readonly object _gate = new();
        private readonly object _flushGate = new();
        private readonly string _directory;
        private readonly Action<string> _warning;
        private readonly Timer _timer;
        private readonly int _executionRetention;
        private readonly bool _retainCode;
        private JObject _statistics;
        private JObject _executions;
        private long _version, _savedVersion, _executionVersion, _savedExecutionVersion;
        private bool _warned, _disposed;

        public McpUsageJournal(string directory, Action<string> warning, bool startTimer = true,
            int executionRetention = MaxExecutions, bool retainCode = true)
        {
            _directory = directory;
            _warning = warning;
            _executionRetention = Math.Max(0, Math.Min(MaxExecutions, executionRetention));
            _retainCode = retainCode;
            _statistics = Load("statistics.json", () => new JObject
            {
                ["schemaVersion"] = 1, ["sinceUtc"] = DateTime.UtcNow.ToString("O"),
                ["tools"] = new JObject(),
            });
            JObject EmptyExecutions() => new JObject
            {
                ["schemaVersion"] = 1, ["retentionCount"] = _executionRetention, ["entries"] = new JArray(),
            };
            _executions = _executionRetention == 0 ? EmptyExecutions() : Load("execute-code.json", EmptyExecutions);
            if (_executionRetention > 0)
            {
                var entries = (JArray)_executions["entries"];
                while (entries.Count > _executionRetention) { entries.RemoveAt(0); _executionVersion++; }
                if ((int?)_executions["retentionCount"] != _executionRetention)
                { _executions["retentionCount"] = _executionRetention; _executionVersion++; }
            }
            if (startTimer) _timer = new Timer(_ => Flush(), null, 5000, 5000);
        }

        public void Record(string name, JObject arguments, JObject response, long durationMs, long writerWaitMs = 0, long dispatchMs = 0, int responseCharacters = -1)
        {
            var requestCharacters = McpToolCallLogger.CountJsonCharacters(arguments);
            if (responseCharacters < 0) responseCharacters = McpToolCallLogger.CountJsonCharacters(response);
            long imageCharacters = 0, imageBytes = 0;
            if (response?["result"]?["content"] is JArray blocks)
                foreach (var block in blocks)
                    if ((string)block["type"] == "image" && block["data"]?.Type == JTokenType.String)
                    {
                        var data = (string)block["data"];
                        imageCharacters += data.Length;
                        imageBytes += data.Length / 4L * 3 - (data.EndsWith("==", StringComparison.Ordinal) ? 2 : data.EndsWith("=", StringComparison.Ordinal) ? 1 : 0);
                    }
            lock (_gate)
            {
                if (_disposed) return;
                name = Clip(name ?? "<unknown>", 200);
                var tools = (JObject)_statistics["tools"];
                var key = tools[name] != null || tools.Count < MaxTools ? name : "<other>";
                var entry = tools[key] as JObject;
                if (entry == null) tools[key] = entry = new JObject();
                var now = DateTime.UtcNow.ToString("O");
                var error = response?["error"] as JObject;
                var result = response?["result"] as JObject;
                var structured = result?["structuredContent"] as JObject;
                var failed = error != null || result?["isError"]?.ToString() == "True"
                    || structured?["success"]?.ToString() == "False"
                    || structured?["timeout"]?.ToString() == "True"
                    || structured?["status"]?.ToString() is "failed" or "error"
                    || (structured?["failed"]?.Type == JTokenType.Integer && (long)structured["failed"] > 0);
                var outcome = response == null ? "interrupted" : failed ? "failed" : "ok";
                entry["calls"] = (long?)entry["calls"] + 1 ?? 1;
                entry[outcome] = (long?)entry[outcome] + 1 ?? 1;
                entry["totalDurationMs"] = ((long?)entry["totalDurationMs"] ?? 0) + durationMs;
                entry["maxDurationMs"] = Math.Max((long?)entry["maxDurationMs"] ?? 0, durationMs);
                entry["lastUsedUtc"] = now;
                entry["measurementCalls"] = ((long?)entry["measurementCalls"] ?? 0) + 1;
                entry["totalRequestCharacters"] = ((long?)entry["totalRequestCharacters"] ?? 0) + requestCharacters;
                entry["totalTextResponseCharacters"] = ((long?)entry["totalTextResponseCharacters"] ?? 0) + responseCharacters - imageCharacters;
                entry["totalImageBytes"] = ((long?)entry["totalImageBytes"] ?? 0) + imageBytes;
                entry["totalWriterWaitMs"] = ((long?)entry["totalWriterWaitMs"] ?? 0) + writerWaitMs;
                entry["totalDispatchMs"] = ((long?)entry["totalDispatchMs"] ?? 0) + dispatchMs;
                if (error?["code"]?.Type == JTokenType.Integer)
                {
                    var codes = entry["errorCodes"] as JObject ?? new JObject();
                    entry["errorCodes"] = codes;
                    var code = error["code"].ToString();
                    if (codes[code] == null && codes.Count >= 32) code = "other";
                    codes[code] = ((long?)codes[code] ?? 0) + 1;
                }
                _statistics["updatedUtc"] = now;
                _version++;
                if (_executionRetention == 0 || !McpToolCallLogger.IsRoslynTool(name)) return;
                var executions = (JArray)_executions["entries"];
                executions.Add(new JObject
                {
                    ["id"] = Guid.NewGuid().ToString("N"), ["utc"] = now, ["tool"] = name,
                    ["reason"] = Clip(arguments?["reason"]?.ToString(), 500),
                    ["code"] = _retainCode ? Clip(arguments?["code"]?.ToString(), 65536) : null,
                    ["codeRetained"] = _retainCode,
                    ["codeTruncated"] = (arguments?["code"]?.ToString()?.Length ?? 0) > 65536,
                    ["outcome"] = outcome, ["durationMs"] = durationMs,
                    ["writerWaitMs"] = writerWaitMs, ["dispatchMs"] = dispatchMs,
                    ["errorCode"] = error?["code"]?.DeepClone(),
                });
                while (executions.Count > _executionRetention) executions.RemoveAt(0);
                _executionVersion++;
            }
        }

        public void Flush()
        {
            lock (_flushGate)
            {
                JObject statistics, executions;
                long version, executionVersion;
                lock (_gate)
                {
                    version = _version;
                    executionVersion = _executionVersion;
                    statistics = version != _savedVersion ? (JObject)_statistics.DeepClone() : null;
                    executions = executionVersion != _savedExecutionVersion ? (JObject)_executions.DeepClone() : null;
                }
                try
                {
                    if (statistics != null) Write("statistics.json", statistics);
                    if (executions != null) Write("execute-code.json", executions);
                    _savedVersion = version;
                    _savedExecutionVersion = executionVersion;
                    _warned = false;
                }
                catch (Exception ex) { Warn(ex); }
            }
        }

        private JObject Load(string file, Func<JObject> empty)
        {
            var path = Path.Combine(_directory, file);
            try
            {
                if (!File.Exists(path)) return empty();
                if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("Usage file exceeds 32 MiB.");
                var value = JObject.Parse(File.ReadAllText(path));
                if ((int?)value["schemaVersion"] != 1
                    || (file == "statistics.json" ? value["tools"] is not JObject : value["entries"] is not JArray))
                    throw new IOException("Unsupported MCP usage file structure.");
                if (value["tools"] is JObject tools)
                {
                    foreach (var property in tools.Properties().Skip(MaxTools + 1).ToList()) property.Remove();
                    if (tools.Properties().Any(p => p.Value is not JObject)) throw new IOException("Invalid tool statistics.");
                }
                if (value["entries"] is JArray entries)
                    while (entries.Count > MaxExecutions) entries.RemoveAt(0);
                return value;
            }
            catch (Exception ex)
            {
                Warn(ex);
                // Keep the unreadable original for investigation; one bounded recovery copy.
                try { if (File.Exists(path)) File.Copy(path, path + ".invalid", true); } catch { }
                return empty();
            }
        }

        private void Write(string file, JObject value)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, file);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, value.ToString(Formatting.Indented));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private void Warn(Exception ex)
        {
            if (_warned) return;
            _warned = true;
            _warning?.Invoke($"MCP usage persistence failed at '{_directory}': {ex.Message}");
        }

        private static string Clip(string value, int length)
            => value == null || value.Length <= length ? value : value.Substring(0, length);

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            _timer?.Dispose();
            Flush();
        }
    }
}
