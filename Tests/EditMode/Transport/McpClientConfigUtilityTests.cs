using System.Reflection;
using LittleBrushGames.Mcp.Editor.Host;
using LittleBrushGames.Mcp.Editor.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Transport
{
    public class McpClientConfigUtilityTests
    {
        [Test]
        public void OnDemandCodexConfigSwitchesTransportWithoutRemovingOtherSettings()
        {
            var server = McpClientConfigUtility.BuildOnDemandServer("bridge.exe", ".", "Unity.exe", 48765, 48766);
            const string existing = "[mcp_servers.unity]\nurl = \"http://old\"\nenabled = true\n\n[mcp_servers.other]\nurl = \"https://other\"\n";
            var result = McpClientConfigUtility.UpsertOnDemandCodexConfig(existing, server);
            Assert.That(result, Does.Not.Contain("http://old"));
            Assert.That(result, Does.Contain("enabled = true"));
            Assert.That(result, Does.Contain("https://other"));
            Assert.That(result, Does.Contain("--stdio"));
            Assert.That(McpClientConfigUtility.UpsertOnDemandCodexConfig(result, server), Is.EqualTo(result));
            var http = McpClientConfigUtility.UpsertCodexServerConfig(result, 48765);
            Assert.That(http, Does.Not.Contain("--stdio"));
            Assert.That(http, Does.Not.Contain("command ="));
            Assert.That(http, Does.Contain("enabled = true"));
        }

        [TestCase("Claude Code")]
        [TestCase("Cursor")]
        [TestCase("OpenCode")]
        public void OnDemandJsonConfigPreservesOtherServersAndEnvironment(string client)
        {
            var section = client == "OpenCode" ? "mcp" : "mcpServers";
            var root = new JObject { [section] = new JObject
            {
                ["other"] = new JObject { ["url"] = "https://other" },
                ["unity"] = new JObject { ["url"] = "http://old", ["env"] = new JObject { ["EXAMPLE"] = "value" } },
            }};
            var server = McpClientConfigUtility.BuildOnDemandServer("bridge.exe", ".", "Unity.exe", 48765, 48766);
            var result = JObject.Parse(McpClientConfigUtility.UpsertOnDemandJsonConfig(root.ToString(), server, client));
            Assert.That(result[section]["other"]["url"].Value<string>(), Is.EqualTo("https://other"));
            Assert.That((object)result[section]["unity"]["url"], Is.Null, result.ToString());
            Assert.That(result[section]["unity"]["env"]["EXAMPLE"].Value<string>(), Is.EqualTo("value"));
            Assert.That(result[section]["unity"]["command"], Is.Not.Null);
        }

        [Test]
        public void UpsertCodexServerConfig_IsIdempotentAndPreservesOtherBlocks()
        {
            const string existing = "[workspace]\ntrusted = true\n\n[mcp_servers.other]\nurl = \"https://example.com/mcp\"\n";

            var once = McpClientConfigUtility.UpsertCodexServerConfig(existing, 48765);
            var twice = McpClientConfigUtility.UpsertCodexServerConfig(once, 48765);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(once, Does.Contain("[workspace]\ntrusted = true"));
            Assert.That(once, Does.Contain("[mcp_servers.other]\nurl = \"https://example.com/mcp\""));
            Assert.That(once, Does.Contain("[mcp_servers.unity]\nurl = \"http://127.0.0.1:48765/mcp\""));
        }

        [Test]
        public void RemoveCodexServerConfig_IsIdempotentAndPreservesOtherBlocks()
        {
            const string existing = "[workspace]\ntrusted = true\n\n[mcp_servers.unity]\nurl = \"http://127.0.0.1:48765/mcp\"\n\n[mcp_servers.other]\nurl = \"https://example.com/mcp\"\n";

            var once = McpClientConfigUtility.RemoveCodexServerConfig(existing);
            var twice = McpClientConfigUtility.RemoveCodexServerConfig(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(once, Does.Contain("[workspace]\ntrusted = true"));
            Assert.That(once, Does.Contain("[mcp_servers.other]\nurl = \"https://example.com/mcp\""));
            Assert.That(once, Does.Not.Contain("[mcp_servers.unity]"));
        }

        [Test]
        public void UpsertClaudeCodeConfig_IsIdempotentAndPreservesOtherServers()
        {
            const string existing = @"{
  ""mcpServers"": {
    ""other"": { ""type"": ""http"", ""url"": ""https://example.com/mcp"" }
  },
  ""meta"": { ""owner"": ""tools"" }
}";

            var once = McpClientConfigUtility.UpsertClaudeCodeConfig(existing, 48765);
            var twice = McpClientConfigUtility.UpsertClaudeCodeConfig(once, 48765);
            var root = JObject.Parse(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["meta"]["owner"]?.Value<string>(), Is.EqualTo("tools"));
            Assert.That(root["mcpServers"]["other"]["url"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            AssertHttpServer(root["mcpServers"]["unity"]);
        }

        [Test]
        public void RemoveClaudeCodeConfig_IsIdempotentAndPreservesOtherServers()
        {
            var existing = McpClientConfigUtility.UpsertClaudeCodeConfig(@"{ ""mcpServers"": { ""other"": { ""url"": ""https://example.com/mcp"" } } }", 48765);

            var once = McpClientConfigUtility.RemoveClaudeCodeConfig(existing);
            var twice = McpClientConfigUtility.RemoveClaudeCodeConfig(once);
            var root = JObject.Parse(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["mcpServers"]["other"]["url"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            Assert.That(root["mcpServers"]["unity"], Is.Null);
        }

        [Test]
        public void UpsertCursorConfig_IsIdempotentAndPreservesOtherServers()
        {
            const string existing = @"{
  ""mcpServers"": {
    ""other"": { ""type"": ""http"", ""url"": ""https://example.com/mcp"" }
  }
}";

            var once = McpClientConfigUtility.UpsertCursorConfig(existing, 48765);
            var twice = McpClientConfigUtility.UpsertCursorConfig(once, 48765);
            var root = JObject.Parse(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["mcpServers"]["other"]["url"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            AssertHttpServer(root["mcpServers"]["unity"]);
        }

        [Test]
        public void RemoveCursorConfig_IsIdempotentAndPreservesOtherServers()
        {
            var existing = McpClientConfigUtility.UpsertCursorConfig(@"{ ""mcpServers"": { ""other"": { ""url"": ""https://example.com/mcp"" } } }", 48765);

            var once = McpClientConfigUtility.RemoveCursorConfig(existing);
            var twice = McpClientConfigUtility.RemoveCursorConfig(once);
            var root = JObject.Parse(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["mcpServers"]["other"]["url"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            Assert.That(root["mcpServers"]["unity"], Is.Null);
        }

        [Test]
        public void UpsertVsCodeConfig_IsIdempotentAndPreservesOtherServers()
        {
            const string existing = @"{
  ""servers"": {
    ""other"": { ""type"": ""http"", ""url"": ""https://example.com/mcp"" }
  }
}";

            var once = McpClientConfigUtility.UpsertVsCodeConfig(existing, 48765);
            var twice = McpClientConfigUtility.UpsertVsCodeConfig(once, 48765);
            var root = JObject.Parse(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["servers"]["other"]["url"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            AssertHttpServer(root["servers"]["unity"]);
        }

        [Test]
        public void RemoveVsCodeConfig_IsIdempotentAndPreservesOtherServers()
        {
            var existing = McpClientConfigUtility.UpsertVsCodeConfig(@"{ ""servers"": { ""other"": { ""url"": ""https://example.com/mcp"" } } }", 48765);

            var once = McpClientConfigUtility.RemoveVsCodeConfig(existing);
            var twice = McpClientConfigUtility.RemoveVsCodeConfig(once);
            var root = JObject.Parse(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["servers"]["other"]["url"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            Assert.That(root["servers"]["unity"], Is.Null);
        }

        [Test]
        public void UpsertWindsurfConfig_IsIdempotentAndPreservesOtherServers()
        {
            const string existing = @"{
  ""mcpServers"": {
    ""other"": { ""serverUrl"": ""https://example.com/mcp"" }
  }
}";

            var once = McpClientConfigUtility.UpsertWindsurfConfig(existing, 48765);
            var twice = McpClientConfigUtility.UpsertWindsurfConfig(once, 48765);
            var root = JObject.Parse(once);
            var unity = root["mcpServers"]["unity"];

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["mcpServers"]["other"]["serverUrl"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            Assert.That(unity["serverUrl"]?.Value<string>(), Is.EqualTo("http://127.0.0.1:48765/mcp"));
        }

        [Test]
        public void RemoveWindsurfConfig_IsIdempotentAndPreservesOtherServers()
        {
            var existing = McpClientConfigUtility.UpsertWindsurfConfig(@"{ ""mcpServers"": { ""other"": { ""serverUrl"": ""https://example.com/mcp"" } } }", 48765);

            var once = McpClientConfigUtility.RemoveWindsurfConfig(existing);
            var twice = McpClientConfigUtility.RemoveWindsurfConfig(once);
            var root = JObject.Parse(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["mcpServers"]["other"]["serverUrl"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            Assert.That(root["mcpServers"]["unity"], Is.Null);
        }

        [Test]
        public void BuildOpenCodeConfig_ReturnsRemoteMcpServer()
        {
            var json = McpClientConfigUtility.BuildOpenCodeConfig(48765);
            var root = JObject.Parse(json);
            var unity = root["mcp"]["unity"];

            Assert.That(root["$schema"]?.Value<string>(), Is.EqualTo("https://opencode.ai/config.json"));
            Assert.That(unity["type"]?.Value<string>(), Is.EqualTo("remote"));
            Assert.That(unity["url"]?.Value<string>(), Is.EqualTo("http://127.0.0.1:48765/mcp"));
            Assert.That(unity["enabled"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void UpsertOpenCodeServerConfig_PreservesExistingConfigAndUpdatesUnityServer()
        {
            const string existing = @"{
  ""model"": ""anthropic/claude-sonnet-4-5"",
  ""mcp"": {
    ""other"": { ""type"": ""remote"", ""url"": ""https://example.com/mcp"" },
    ""unity"": { ""type"": ""remote"", ""url"": ""http://127.0.0.1:11111/mcp"", ""enabled"": false }
  }
}";

            var json = McpClientConfigUtility.UpsertOpenCodeServerConfig(existing, 48765);
            var root = JObject.Parse(json);
            var unity = root["mcp"]["unity"];

            Assert.That(root["model"]?.Value<string>(), Is.EqualTo("anthropic/claude-sonnet-4-5"));
            Assert.That(root["mcp"]["other"]["url"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            Assert.That(unity["type"]?.Value<string>(), Is.EqualTo("remote"));
            Assert.That(unity["url"]?.Value<string>(), Is.EqualTo("http://127.0.0.1:48765/mcp"));
            Assert.That(unity["enabled"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void RemoveOpenCodeServerConfig_IsIdempotentAndPreservesOtherServers()
        {
            var existing = McpClientConfigUtility.UpsertOpenCodeServerConfig(@"{ ""mcp"": { ""other"": { ""type"": ""remote"", ""url"": ""https://example.com/mcp"" } } }", 48765);

            var once = McpClientConfigUtility.RemoveOpenCodeServerConfig(existing);
            var twice = McpClientConfigUtility.RemoveOpenCodeServerConfig(once);
            var root = JObject.Parse(once);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(root["mcp"]["other"]["url"]?.Value<string>(), Is.EqualTo("https://example.com/mcp"));
            Assert.That(root["mcp"]["unity"], Is.Null);
        }

        [Test]
        public void HasOpenCodeServerConfig_ReturnsTrueOnlyWhenUnityServerExists()
        {
            var withoutUnity = @"{ ""mcp"": { ""other"": { ""type"": ""remote"", ""url"": ""https://example.com/mcp"" } } }";
            var withUnity = McpClientConfigUtility.BuildOpenCodeConfig(48765);

            Assert.That(McpClientConfigUtility.HasOpenCodeServerConfig(withoutUnity), Is.False);
            Assert.That(McpClientConfigUtility.HasOpenCodeServerConfig(withUnity), Is.True);
        }

        [Test]
        public void BridgeTransport_BufferTimeout_AllowsSlowDomainReload()
        {
            var field = typeof(BridgeTransport).GetField("BridgeBufferTimeout", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(field, Is.Not.Null);
            Assert.That(field.GetRawConstantValue(), Is.EqualTo("60s"));
        }

        private static void AssertHttpServer(JToken unity)
        {
            Assert.That(unity["type"]?.Value<string>(), Is.EqualTo("http"));
            Assert.That(unity["url"]?.Value<string>(), Is.EqualTo("http://127.0.0.1:48765/mcp"));
        }
    }
}
