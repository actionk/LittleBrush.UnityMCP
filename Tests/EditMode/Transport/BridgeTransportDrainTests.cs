using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using LittleBrushGames.Mcp.Editor.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Transport
{
    public class BridgeTransportDrainTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void CompletedResponseCanBeDeliveredWhileCancellationUnwinds(bool duringDispose)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var transport = new BridgeTransport(null, null);
            using var bytes = new MemoryStream();
            var writer = new StreamWriter(bytes, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            typeof(BridgeTransport).GetField("_writer", flags).SetValue(transport, writer);
            var send = typeof(BridgeTransport).GetMethod("SendToBridge", flags);
            var response = new JObject { ["type"] = "response", ["id"] = "completed-during-drain", ["payload"] = new JObject() };
            if (duringDispose)
            {
                var cts = (CancellationTokenSource)typeof(BridgeTransport).GetField("_cts", flags).GetValue(transport);
                cts.Token.Register(() => send.Invoke(transport, new object[] { response }));
            }
            else send.Invoke(transport, new object[] { response });
            transport.Dispose();
            Assert.That(Encoding.UTF8.GetString(bytes.ToArray()), Does.Contain("completed-during-drain"),
                "A completed response must be allowed onto the open socket during the promised drain interval.");
        }
    }
}
