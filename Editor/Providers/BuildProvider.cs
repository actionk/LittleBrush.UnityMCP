using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    [InitializeOnLoad]
    public sealed class BuildProvider : IToolProvider
    {
        private const string StatePrefix = "McpBuild_";
        private const string ActiveKey = "McpBuild_Active";

        static BuildProvider()
        {
            EditorApplication.delayCall += RecoverQueuedBuild;
        }

        public string Namespace => "build";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "build.settings",
                Description = "Read active build target and enabled build scenes.",
                Availability = ToolAvailability.EditMode,
                InputSchema = new JObject { ["type"] = "object" },
                Handler = Settings,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "build.player_settings.read",
                Description = "Read common PlayerSettings values with a concurrency hash.",
                Availability = ToolAvailability.EditMode,
                InputSchema = new JObject { ["type"] = "object" },
                Handler = ReadPlayerSettings,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
            reg.Register(new ToolDescriptor
            {
                Name = "build.player_settings.write",
                Description = "Write common PlayerSettings values with expectedHash and dryRun validation.",
                Availability = ToolAvailability.EditMode,
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""expectedHash"", ""properties""],
                    ""properties"": {
                        ""expectedHash"": { ""type"": ""string"" },
                        ""properties"": { ""type"": ""object"" },
                        ""dryRun"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = WritePlayerSettings,
            });
            reg.Register(new ToolDescriptor
            {
                Name = "build.start",
                Description = "Queue a Unity player build and return a runId before the blocking build begins. Governed by the local Builds trust policy; poll build.result.",
                Availability = ToolAvailability.EditMode,
                ExclusiveGroup = "build",
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"", ""required"": [""outputPath""],
                    ""properties"": {
                        ""outputPath"": { ""type"": ""string"", ""minLength"": 1 },
                        ""target"": { ""type"": ""string"" },
                        ""development"": { ""type"": ""boolean"" },
                        ""cleanBuildCache"": { ""type"": ""boolean"" }
                    }
                }"),
                Handler = Start,
            });
            reg.Register(new ToolDescriptor
            {
                Name = "build.result",
                Description = "Poll a queued Unity player build by runId.",
                Availability = ToolAvailability.Always,
                InputSchema = JObject.Parse(@"{ ""type"": ""object"", ""required"": [""runId""], ""properties"": { ""runId"": { ""type"": ""string"" } } }"),
                Handler = Result,
                Annotations = new JObject { ["readOnlyHint"] = true },
            });
        }

        private static ValueTask<ToolResult> Settings(ToolContext ctx, CancellationToken ct)
            => new(ToolResult.Ok(new JObject
            {
                ["activeTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["isBuilding"] = BuildPipeline.isBuildingPlayer,
                ["scenes"] = new JArray(EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path)),
            }));

        private static ValueTask<ToolResult> ReadPlayerSettings(ToolContext ctx, CancellationToken ct)
        {
            var properties = GetPlayerSettings();
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["hash"] = Hash(properties), ["properties"] = properties }));
        }

        private static ValueTask<ToolResult> WritePlayerSettings(ToolContext ctx, CancellationToken ct)
        {
            var current = GetPlayerSettings();
            var currentHash = Hash(current);
            if (!string.Equals((string)ctx.Arguments["expectedHash"], currentHash, StringComparison.Ordinal))
                throw new McpToolException(McpErrorCodes.Conflict, "PlayerSettings changed since read.", new JObject { ["currentHash"] = currentHash });
            var properties = ctx.Arguments["properties"] as JObject
                ?? throw new McpToolException(McpErrorCodes.ValidationFailed, "properties required.");
            ValidatePlayerSettings(properties);
            if (ctx.Arguments["dryRun"]?.Value<bool>() != true)
            {
                ApplyPlayerSettings(properties);
                AssetDatabase.SaveAssets();
                current = GetPlayerSettings();
                currentHash = Hash(current);
            }
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["hash"] = currentHash, ["dryRun"] = ctx.Arguments["dryRun"]?.Value<bool>() == true, ["properties"] = new JArray(properties.Properties().Select(p => p.Name)) }));
        }

        private static ValueTask<ToolResult> Start(ToolContext ctx, CancellationToken ct)
        {
            var active = SessionState.GetString(ActiveKey, "");
            if (BuildPipeline.isBuildingPlayer || !string.IsNullOrEmpty(active))
                throw new McpToolException(McpErrorCodes.Conflict, $"A build is already active: '{active}'.");

            var targetName = (string)ctx.Arguments["target"];
            var target = string.IsNullOrEmpty(targetName) ? EditorUserBuildSettings.activeBuildTarget : ParseEnum<BuildTarget>("target", targetName);
            var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (scenes.Length == 0)
                throw new McpToolException(McpErrorCodes.ValidationFailed, "No enabled scenes exist in Build Settings.");

            var runId = Guid.NewGuid().ToString("N")[..12];
            var state = new JObject
            {
                ["runId"] = runId,
                ["status"] = "queued",
                ["outputPath"] = (string)ctx.Arguments["outputPath"],
                ["target"] = target.ToString(),
                ["development"] = ctx.Arguments["development"]?.Value<bool>() == true,
                ["cleanBuildCache"] = ctx.Arguments["cleanBuildCache"]?.Value<bool>() == true,
                ["scenes"] = new JArray(scenes),
                ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            SessionState.SetString(StatePrefix + runId, state.ToString());
            SessionState.SetString(ActiveKey, runId);
            EditorApplication.delayCall += () => RunBuild(runId);
            return new ValueTask<ToolResult>(ToolResult.Ok(new JObject { ["runId"] = runId, ["status"] = "queued" }));
        }

        private static ValueTask<ToolResult> Result(ToolContext ctx, CancellationToken ct)
        {
            var runId = (string)ctx.Arguments["runId"];
            var json = SessionState.GetString(StatePrefix + runId, "");
            if (string.IsNullOrEmpty(json))
                throw new McpToolException(McpErrorCodes.NotFound, $"Build run not found: '{runId}'.");
            return new ValueTask<ToolResult>(ToolResult.Ok(JObject.Parse(json)));
        }

        private static void RunBuild(string runId)
        {
            var key = StatePrefix + runId;
            var json = SessionState.GetString(key, "");
            if (string.IsNullOrEmpty(json)) return;
            var state = JObject.Parse(json);
            state["status"] = "running";
            SessionState.SetString(key, state.ToString());
            try
            {
                var options = BuildOptions.None;
                if ((bool)state["development"]) options |= BuildOptions.Development;
                if ((bool)state["cleanBuildCache"]) options |= BuildOptions.CleanBuildCache;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = ((JArray)state["scenes"]).Values<string>().ToArray(),
                    locationPathName = (string)state["outputPath"],
                    target = ParseEnum<BuildTarget>("target", (string)state["target"]),
                    options = options,
                });
                var summary = report.summary;
                state["status"] = summary.result == BuildResult.Succeeded ? "completed" : "failed";
                state["result"] = summary.result.ToString();
                state["errors"] = summary.totalErrors;
                state["warnings"] = summary.totalWarnings;
                state["sizeBytes"] = (long)summary.totalSize;
                state["durationMs"] = (long)summary.totalTime.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                state["status"] = "failed";
                state["message"] = ex.Message;
            }
            finally
            {
                state["finishedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SessionState.SetString(key, state.ToString());
                SessionState.EraseString(ActiveKey);
            }
        }

        private static void RecoverQueuedBuild()
        {
            var runId = SessionState.GetString(ActiveKey, "");
            if (string.IsNullOrEmpty(runId)) return;
            var key = StatePrefix + runId;
            var json = SessionState.GetString(key, "");
            if (string.IsNullOrEmpty(json)) { SessionState.EraseString(ActiveKey); return; }
            var state = JObject.Parse(json);
            if ((string)state["status"] == "queued") EditorApplication.delayCall += () => RunBuild(runId);
            else if (!BuildPipeline.isBuildingPlayer)
            {
                state["status"] = "failed";
                state["message"] = "Editor reloaded while the build was running.";
                SessionState.SetString(key, state.ToString());
                SessionState.EraseString(ActiveKey);
            }
        }

        private static JObject GetPlayerSettings()
        {
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            return new JObject
            {
                ["companyName"] = PlayerSettings.companyName,
                ["productName"] = PlayerSettings.productName,
                ["bundleVersion"] = PlayerSettings.bundleVersion,
                ["applicationIdentifier"] = PlayerSettings.GetApplicationIdentifier(namedTarget),
                ["runInBackground"] = PlayerSettings.runInBackground,
                ["defaultScreenWidth"] = PlayerSettings.defaultScreenWidth,
                ["defaultScreenHeight"] = PlayerSettings.defaultScreenHeight,
                ["fullScreenMode"] = PlayerSettings.fullScreenMode.ToString(),
                ["scriptingBackend"] = PlayerSettings.GetScriptingBackend(namedTarget).ToString(),
            };
        }

        private static void ValidatePlayerSettings(JObject properties)
        {
            foreach (var property in properties)
            {
                switch (property.Key)
                {
                    case "companyName": case "productName": case "bundleVersion": case "applicationIdentifier": _ = property.Value.Value<string>(); break;
                    case "runInBackground": _ = property.Value.Value<bool>(); break;
                    case "defaultScreenWidth": case "defaultScreenHeight": _ = property.Value.Value<int>(); break;
                    case "fullScreenMode": _ = ParseEnum<FullScreenMode>(property.Key, (string)property.Value); break;
                    case "scriptingBackend": _ = ParseEnum<ScriptingImplementation>(property.Key, (string)property.Value); break;
                    default: throw new McpToolException(McpErrorCodes.ValidationFailed, $"Unsupported PlayerSettings property: '{property.Key}'.");
                }
            }
        }

        private static void ApplyPlayerSettings(JObject properties)
        {
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));
            foreach (var property in properties)
            {
                switch (property.Key)
                {
                    case "companyName": PlayerSettings.companyName = (string)property.Value; break;
                    case "productName": PlayerSettings.productName = (string)property.Value; break;
                    case "bundleVersion": PlayerSettings.bundleVersion = (string)property.Value; break;
                    case "applicationIdentifier": PlayerSettings.SetApplicationIdentifier(namedTarget, (string)property.Value); break;
                    case "runInBackground": PlayerSettings.runInBackground = (bool)property.Value; break;
                    case "defaultScreenWidth": PlayerSettings.defaultScreenWidth = (int)property.Value; break;
                    case "defaultScreenHeight": PlayerSettings.defaultScreenHeight = (int)property.Value; break;
                    case "fullScreenMode": PlayerSettings.fullScreenMode = ParseEnum<FullScreenMode>(property.Key, (string)property.Value); break;
                    case "scriptingBackend": PlayerSettings.SetScriptingBackend(namedTarget, ParseEnum<ScriptingImplementation>(property.Key, (string)property.Value)); break;
                }
            }
        }

        private static string Hash(JObject value) => Hash128.Compute(value.ToString(Newtonsoft.Json.Formatting.None)).ToString();

        private static T ParseEnum<T>(string key, string value) where T : struct, Enum
        {
            if (Enum.TryParse<T>(value, false, out var parsed)) return parsed;
            throw new McpToolException(McpErrorCodes.ValidationFailed, $"{key}: '{value}' is invalid. Allowed: {string.Join(", ", Enum.GetNames(typeof(T)))}.");
        }
    }
}
