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
        [Test]
        public void MatchingExecutableDoesNotGrantOwnershipOfALauncher()
        {
            using var transport = new BridgeTransport(null, null);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var owns = typeof(BridgeTransport).GetMethod("IsOwnedBridgeProcess", flags);
            var path = process.MainModule.FileName;
            Assert.That(owns.Invoke(transport, new object[] { process, path }), Is.False,
                "Sharing a server executable must not make a stdio launcher eligible for cleanup.");
            typeof(BridgeTransport).GetField("_bridgeProcessId", flags).SetValue(transport, process.Id);
            Assert.That(owns.Invoke(transport, new object[] { process, path }), Is.True);
            Assert.That(owns.Invoke(transport, new object[] { process, path + ".other" }), Is.False);
        }

        [Test]
        public void ForeignBridge_IsRejectedWithoutTakingOwnership()
        {
            using var transport = new BridgeTransport(null, null);
            var validate = typeof(BridgeTransport).GetMethod("ValidateBridgeOwner", BindingFlags.Instance | BindingFlags.NonPublic);
            var health = new JObject { ["projectPath"] = "/another-project/Assets", ["projectHash"] = "foreign" };
            var error = Assert.Throws<TargetInvocationException>(() => validate.Invoke(transport, new object[] { health }));
            Assert.That(error.InnerException, Is.TypeOf<System.InvalidOperationException>());
            Assert.That(error.InnerException.Message, Does.Contain("left running"));
            Assert.That(transport.IsRunning, Is.False);
        }

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
