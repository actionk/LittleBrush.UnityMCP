using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    [McpToolProvider]
    public sealed class PackageManagerProvider : IToolProvider
    {
        public string Namespace => "package_manager";

        public void RegisterTools(IToolRegistration reg)
        {
            reg.Register(new ToolDescriptor
            {
                Name = "package_manager.add",
                Description = "Add or update a Unity package dependency through Package Manager. Accepts registry package identifiers (optionally with a version), Git URLs, and local package paths supported by Client.Add.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Async,
                Timeout = TimeSpan.FromMinutes(10),
                ReloadSafe = true,
                ExclusiveGroup = "package-manager",
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""packageId""],
                    ""properties"": {
                        ""packageId"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Package identifier accepted by Unity Package Manager, such as com.unity.cinemachine@3.1.3, a Git URL, or a local file: path."" }
                    },
                    ""additionalProperties"": false
                }"),
                Handler = Add,
            });

            reg.Register(new ToolDescriptor
            {
                Name = "package_manager.remove",
                Description = "Remove a direct Unity package dependency through Package Manager using its package name.",
                Availability = ToolAvailability.EditMode,
                Execution = ToolExecution.Async,
                Timeout = TimeSpan.FromMinutes(10),
                ReloadSafe = true,
                ExclusiveGroup = "package-manager",
                Annotations = new JObject { ["destructiveHint"] = true },
                InputSchema = JObject.Parse(@"{
                    ""type"": ""object"",
                    ""required"": [""packageName""],
                    ""properties"": {
                        ""packageName"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Package name to remove, such as com.unity.cinemachine."" }
                    },
                    ""additionalProperties"": false
                }"),
                Handler = Remove,
            });
        }

        private static async ValueTask<ToolResult> Add(ToolContext ctx, CancellationToken ct)
        {
            var packageId = ctx.Arguments.Value<string>("packageId");
            AddRequest request;
            try
            {
                request = Client.Add(packageId);
            }
            catch (ArgumentException ex)
            {
                throw new McpToolException(McpErrorCodes.InvalidParams, ex.Message);
            }

            await ctx.Frames.WaitUntilAsync(() => request.IsCompleted, null, ct);
            ThrowIfFailed(request, $"Failed to add package '{packageId}'.");

            var package = request.Result;
            return ToolResult.Ok(new JObject
            {
                ["packageId"] = package.packageId,
                ["name"] = package.name,
                ["version"] = package.version,
                ["source"] = package.source.ToString(),
            });
        }

        private static async ValueTask<ToolResult> Remove(ToolContext ctx, CancellationToken ct)
        {
            var packageName = ctx.Arguments.Value<string>("packageName");
            RemoveRequest request;
            try
            {
                request = Client.Remove(packageName);
            }
            catch (ArgumentException ex)
            {
                throw new McpToolException(McpErrorCodes.InvalidParams, ex.Message);
            }

            await ctx.Frames.WaitUntilAsync(() => request.IsCompleted, null, ct);
            ThrowIfFailed(request, $"Failed to remove package '{packageName}'.");

            return ToolResult.Ok(new JObject
            {
                ["packageName"] = packageName,
                ["removed"] = true,
            });
        }

        private static void ThrowIfFailed(Request request, string fallbackMessage)
        {
            if (request.Status != StatusCode.Failure)
                return;

            var error = request.Error;
            throw new McpToolException(
                McpErrorCodes.ToolError,
                error == null || string.IsNullOrWhiteSpace(error.message) ? fallbackMessage : error.message,
                error == null ? null : new JObject { ["upmErrorCode"] = error.errorCode.ToString() });
        }
    }
}
