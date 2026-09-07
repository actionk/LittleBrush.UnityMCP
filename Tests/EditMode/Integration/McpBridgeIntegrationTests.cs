using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using LittleBrushGames.Mcp.Editor.Transport;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Integration
{
    /// <summary>
    /// Spins up an isolated bridge on an ephemeral port to test the full HTTP stack without
    /// touching the global <c>McpBridgeHost</c>. This avoids deadlocking when run
    /// via the MCP <c>tests.run</c> tool (which would kill its own serving bridge).
    /// </summary>
    public class McpBridgeIntegrationTests
    {
        private int _testPort;
        private HttpTransport _transport;
        private ToolRegistry _registry;
        private MainThreadPump _pump;
        private NotificationBroadcaster _broadcaster;

        [OneTimeSetUp]
        public void SetUp()
        {
            _testPort = GetEphemeralPort();
            _pump = new MainThreadPump(autoTick: false);
            _registry = new ToolRegistry();
            var dispatcher = new ToolDispatcher(
                _registry,
                _pump,
                new FakeFrameWaiter(),
                new FakeLogSink(),
                () => (false, false));
            var router = new JsonRpcRouter(_registry, dispatcher, () => (false, false));
            _broadcaster = new NotificationBroadcaster(new FakeLogSink());
            _transport = new HttpTransport(_testPort, router, _broadcaster, new FakeLogSink());
            _transport.Start();
        }

        private static int GetEphemeralPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            try { _transport?.Dispose(); } catch { }
            try { _pump?.Dispose(); } catch { }
        }

        [Test]
        public async Task LiveBridge_InitializeToolsListEditorStatus_RoundTrips()
        {
            Assert.That(_transport.IsRunning, Is.True, "isolated bridge failed to start on test port");

            using var http = new HttpClient();
            var url = $"http://127.0.0.1:{_testPort}/mcp";

            var (init, sessionId) = await Initialize(http, url);
            Assert.That(init["result"]["capabilities"]["tools"]["listChanged"].Value<bool>(), Is.True);

            var list = await PostJson(http, url, @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/list""}", sessionId);
            var tools = (JArray)list["result"]["tools"];
            Assert.That(tools.Select(tool => (string)tool["name"]), Is.EqualTo(new[] { "unity.tools", "unity.call" }));

            var unknown = await PostJson(http, url, @"{""jsonrpc"":""2.0"",""id"":3,""method"":""does.not.exist""}", sessionId);
            Assert.That((int)unknown["error"]["code"], Is.EqualTo(McpErrorCodes.MethodNotFound));
        }

        [Test]
        public async Task LiveBridge_RequiresInitializedSession()
        {
            using var http = new HttpClient();
            var url = $"http://127.0.0.1:{_testPort}/mcp";
            using var response = await Send(http, url, @"{""jsonrpc"":""2.0"",""id"":4,""method"":""ping""}");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("Missing Mcp-Session-Id"));
        }

        [Test]
        public async Task LiveBridge_RejectsUnsupportedProtocolVersion()
        {
            using var http = new HttpClient();
            var url = $"http://127.0.0.1:{_testPort}/mcp";
            using var response = await Send(http, url,
                @"{""jsonrpc"":""2.0"",""id"":5,""method"":""initialize"",""params"":{}}",
                protocolVersion: "1999-01-01");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task LiveBridge_RejectsOversizedRequestBody()
        {
            using var http = new HttpClient();
            var url = $"http://127.0.0.1:{_testPort}/mcp";
            using var response = await Send(http, url, new string('x', HttpTransport.MaxRequestBodyBytes + 1));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        }

        [Test]
        public async Task LiveBridge_DeliversNotificationQueuedBeforeSseAttach()
        {
            using var http = new HttpClient();
            var url = $"http://127.0.0.1:{_testPort}/mcp";
            var (_, sessionId) = await Initialize(http, url);
            _broadcaster.Send("notifications/tools/list_changed");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            string notificationLine = null;
            for (var i = 0; i < 8 && notificationLine == null; i++)
            {
                var read = reader.ReadLineAsync();
                var completed = await Task.WhenAny(read, Task.Delay(3000));
                Assert.That(completed, Is.SameAs(read), "timed out waiting for queued SSE notification");
                var line = await read;
                if (line?.StartsWith("data: {") == true)
                    notificationLine = line.Substring("data: ".Length);
            }

            Assert.That(notificationLine, Is.Not.Null);
            Assert.That((string)JObject.Parse(notificationLine)["method"], Is.EqualTo("notifications/tools/list_changed"));
        }

        private static async Task<(JObject Body, string SessionId)> Initialize(HttpClient http, string url)
        {
            using var response = await Send(http, url,
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{}}");
            response.EnsureSuccessStatusCode();
            var sessionId = response.Headers.GetValues("Mcp-Session-Id").Single();
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());
            return (body, sessionId);
        }

        private static async Task<JObject> PostJson(HttpClient http, string url, string body, string sessionId)
        {
            using var response = await Send(http, url, body, sessionId);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync();
            return JObject.Parse(text);
        }

        private static async Task<HttpResponseMessage> Send(
            HttpClient http,
            string url,
            string body,
            string sessionId = null,
            string protocolVersion = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(sessionId))
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            if (!string.IsNullOrEmpty(protocolVersion))
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
            try { return await http.SendAsync(request); }
            finally { request.Dispose(); }
        }
    }
}
