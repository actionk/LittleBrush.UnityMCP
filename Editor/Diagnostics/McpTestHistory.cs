using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp.Editor.Diagnostics
{
    internal static class McpTestHistory
    {
        internal const int MaxRuns = 20;
        internal const int MaxTestsPerRun = 10000;
        internal const int MaxBytes = 16 * 1024 * 1024;
        private static readonly object Gate = new();

        internal static JObject Read(string path)
        {
            lock (Gate)
            {
                if (!File.Exists(path)) return new JObject { ["schemaVersion"] = 1, ["runs"] = new JArray() };
                if (new FileInfo(path).Length > MaxBytes) throw new IOException("Test history exceeds 16 MiB. Archive it before continuing.");
                var data = JObject.Parse(File.ReadAllText(path));
                if ((int?)data["schemaVersion"] != 1 || data["runs"] is not JArray runs || runs.Count > MaxRuns
                    || runs.Any(r => r is not JObject || r["tests"] is not JArray tests || tests.Count > MaxTestsPerRun))
                    throw new IOException("Unsupported test history. Repair or archive the file; it has not been overwritten.");
                return data;
            }
        }

        internal static void Record(string path, JObject state, string unityVersion)
        {
            // Do not retain failure output or stack traces; the runner owns those diagnostics.
            var tests = state["tests"] as JArray ?? new JArray();
            if (tests.Count > MaxTestsPerRun) throw new IOException("Test history supports at most 10,000 cases per run; this run was not archived.");
            var run = new JObject();
            foreach (var key in new[] { "runId", "mode", "status", "startedAt", "finishedAt", "durationSec", "passCount", "failCount" })
                run[key] = state[key]?.DeepClone();
            run["unityVersion"] = unityVersion;
            run["wallDurationSec"] = Math.Max(0, ((long?)state["finishedAt"] - (long?)state["startedAt"] ?? 0) / 1000.0);
            run["tests"] = new JArray(tests.Select(t => new JObject
            {
                ["fullName"] = t["fullName"]?.DeepClone(), ["status"] = t["status"]?.DeepClone(),
                ["durationSec"] = t["durationSec"]?.DeepClone(),
            }));
            lock (Gate)
            {
                var data = Read(path);
                var runs = (JArray)data["runs"];
                foreach (var old in runs.Where(r => (string)r["runId"] == (string)run["runId"]).ToList()) old.Remove();
                runs.Add(run);
                while (runs.Count > MaxRuns) runs.RemoveAt(0);
                var text = data.ToString(Formatting.None);
                while (Encoding.UTF8.GetByteCount(text) > MaxBytes && runs.Count > 1)
                {
                    runs.RemoveAt(0);
                    text = data.ToString(Formatting.None);
                }
                if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new IOException("This run exceeds the 16 MiB history limit; existing history was preserved.");
                McpLocalStorage.EnsureDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                else File.Move(path + ".tmp", path);
            }
        }

        internal static JArray Summarize(JObject data)
        {
            var samples = ((JArray)data["runs"]).SelectMany(r => ((JArray)r["tests"]).Select(t => new { Run = r, Test = t }));
            return new JArray(samples.GroupBy(x => ((string)x.Run["mode"], (string)x.Run["unityVersion"], (string)x.Test["fullName"]))
                .Select(group =>
                {
                    var last = group.Last();
                    var passed = group.Where(x => (string)x.Test["status"] == "Passed")
                        .Select(x => (double?)x.Test["durationSec"] ?? 0).OrderBy(x => x).ToArray();
                    return new JObject
                    {
                        ["fullName"] = group.Key.Item3, ["mode"] = group.Key.Item1, ["unityVersion"] = group.Key.Item2,
                        ["status"] = last.Test["status"]?.DeepClone(), ["lastSec"] = last.Test["durationSec"]?.DeepClone(),
                        ["samples"] = group.Count(), ["successfulSamples"] = passed.Length,
                        ["medianSec"] = passed.Length == 0 ? JValue.CreateNull() : new JValue((passed[(passed.Length - 1) / 2] + passed[passed.Length / 2]) / 2),
                    };
                }).OrderByDescending(r => (double?)r["lastSec"] ?? 0).ThenBy(r => (string)r["fullName"], StringComparer.Ordinal));
        }
    }
}
