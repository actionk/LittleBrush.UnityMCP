using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class ToolRegistryTests
    {
        private sealed class StubProvider : IToolProvider
        {
            public string Namespace => "stub";
            public ToolAvailability Availability = ToolAvailability.Either;

            public void RegisterTools(IToolRegistration reg)
            {
                reg.Register(new ToolDescriptor
                {
                    Name = "stub.a",
                    Description = "stub",
                    InputSchema = new JObject { ["type"] = "object" },
                    Availability = Availability,
                    Handler = (_, _) => new ValueTask<ToolResult>(ToolResult.Text("ok")),
                });
            }
        }

        [Test]
        public void Register_ExposesAllTools()
        {
            var reg = new ToolRegistry();
            reg.SetEditorProviders(new IToolProvider[] { new StubProvider() });
            Assert.That(reg.Enumerate().Select(t => t.Name), Contains.Item("stub.a"));
        }

        [Test]
        public void Register_StampsProviderMetadata()
        {
            var reg = new ToolRegistry();
            reg.SetEditorProviders(new IToolProvider[] { new StubProvider() });
            var tool = reg.Enumerate().Single(t => t.Name == "stub.a");
            Assert.That(tool.ProviderTypeName, Is.EqualTo(typeof(StubProvider).FullName));
            Assert.That(tool.ProviderAssemblyName, Is.EqualTo(typeof(StubProvider).Assembly.GetName().Name));
        }

        [Test]
        public void Register_DuplicateName_SkipsProviderAndReportsError()
        {
            var reg = new ToolRegistry();
            reg.SetEditorProviders(new IToolProvider[] { new StubProvider(), new StubProvider() });

            Assert.That(reg.Enumerate().Select(tool => tool.Name), Is.EqualTo(new[] { "stub.a" }));
            Assert.That(reg.Errors.Count, Is.EqualTo(1));
            Assert.That(reg.Errors[0], Does.Contain("Duplicate MCP tool 'stub.a'"));
        }

        [Test]
        public void Register_InvalidDescriptor_IsSkippedWithoutRemovingHealthyTools()
        {
            var reg = new ToolRegistry();
            reg.SetEditorProviders(new IToolProvider[]
            {
                new StubProvider(),
                new InvalidProvider(),
            });

            Assert.That(reg.Enumerate().Select(tool => tool.Name), Is.EqualTo(new[] { "stub.a" }));
            Assert.That(reg.Errors.Count, Is.EqualTo(1));
            Assert.That(reg.Errors[0], Does.Contain("unsupported schema keyword 'pattern'"));
        }

        [Test]
        public void EnumerateAvailable_FiltersByState()
        {
            var reg = new ToolRegistry();
            reg.SetEditorProviders(new IToolProvider[] { new StubProvider { Availability = ToolAvailability.PlayMode } });
            Assert.That(reg.EnumerateAvailable(isPlaying: false, isCompiling: false).Any(), Is.False);
            Assert.That(reg.EnumerateAvailable(isPlaying: true, isCompiling: false).Any(), Is.True);
        }

        private sealed class InvalidProvider : IToolProvider
        {
            public string Namespace => "invalid";

            public void RegisterTools(IToolRegistration reg)
            {
                reg.Register(new ToolDescriptor
                {
                    Name = "invalid.tool",
                    InputSchema = JObject.Parse(@"{ ""type"": ""string"", ""pattern"": ""x"" }"),
                    Handler = (_, _) => new ValueTask<ToolResult>(ToolResult.Text("never")),
                });
            }
        }

        [Test]
        public void RuntimeBridge_ReportsSubscriberFailureAndContinuesNotification()
        {
            var observed = 0;
            Action throwing = () => throw new InvalidOperationException("subscriber failed");
            Action observer = () => observed++;
            IDisposable scope = null;
            McpRuntimeBridge.Changed += throwing;
            McpRuntimeBridge.Changed += observer;
            try
            {
                LogAssert.Expect(LogType.Exception, new Regex("subscriber failed"));
                scope = McpRuntimeBridge.Install(Array.Empty<IToolProvider>(), null);
                Assert.That(observed, Is.EqualTo(1));
            }
            finally
            {
                McpRuntimeBridge.Changed -= throwing;
                McpRuntimeBridge.Changed -= observer;
                scope?.Dispose();
            }
        }
    }
}
