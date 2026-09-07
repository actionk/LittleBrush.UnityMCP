using System;
using LittleBrushGames.Mcp.Editor.Transport;
using LittleBrushGames.Mcp.Tests.Fakes;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Transport
{
    public class NotificationBroadcasterTests
    {
        [Test]
        public void SessionCount_InitiallyZero()
        {
            var broadcaster = new NotificationBroadcaster(new FakeLogSink());
            Assert.That(broadcaster.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void Send_NoSessions_DoesNotThrow()
        {
            var broadcaster = new NotificationBroadcaster(new FakeLogSink());
            Assert.DoesNotThrow(() => broadcaster.Send("notifications/tools/list_changed"));
        }

        [Test]
        public void Send_WithParams_FormatsJsonRpc()
        {
            var broadcaster = new NotificationBroadcaster(new FakeLogSink());
            // No sessions — just verifying it doesn't crash with params
            Assert.DoesNotThrow(() => broadcaster.Send("notifications/progress",
                new JObject { ["progress"] = 50, ["total"] = 100 }));
        }

        [Test]
        public void TryGetSession_NonExistent_ReturnsFalse()
        {
            var broadcaster = new NotificationBroadcaster(new FakeLogSink());
            Assert.That(broadcaster.TryGetSession("does-not-exist", out _), Is.False);
        }

        [Test]
        public void DisposeAll_ClearsAllSessions()
        {
            var broadcaster = new NotificationBroadcaster(new FakeLogSink());
            broadcaster.DisposeAll();
            Assert.That(broadcaster.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void Send_RaisesNotificationSentEvent()
        {
            var broadcaster = new NotificationBroadcaster(new FakeLogSink());
            JObject captured = null;
            broadcaster.NotificationSent += n => captured = n;

            broadcaster.Send("notifications/tools/list_changed");

            Assert.That(captured, Is.Not.Null);
            Assert.That((string)captured["jsonrpc"], Is.EqualTo("2.0"));
            Assert.That((string)captured["method"], Is.EqualTo("notifications/tools/list_changed"));
        }

        [Test]
        public void Send_NotificationSentFiresEvenWithNoSseSessions()
        {
            // Critical for bridge mode: notifications must fire the event even when no
            // SSE sessions are connected in-process (since SSE clients attach to the Go bridge).
            var broadcaster = new NotificationBroadcaster(new FakeLogSink());
            int callCount = 0;
            broadcaster.NotificationSent += _ => callCount++;

            broadcaster.Send("notifications/tools/list_changed");

            Assert.That(callCount, Is.EqualTo(1));
            Assert.That(broadcaster.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void TryCreateSession_EnforcesCapacity()
        {
            var broadcaster = new NotificationBroadcaster(new FakeLogSink(), maxSessions: 2);

            Assert.That(broadcaster.TryCreateSession(out _), Is.True);
            Assert.That(broadcaster.TryCreateSession(out _), Is.True);
            Assert.That(broadcaster.TryCreateSession(out _), Is.False);
            Assert.That(broadcaster.SessionCount, Is.EqualTo(2));
        }

        [Test]
        public void SweepExpired_RemovesOnlyIdleSessions()
        {
            var now = DateTime.UtcNow;
            var broadcaster = new NotificationBroadcaster(
                new FakeLogSink(),
                idleTimeout: TimeSpan.FromMinutes(30),
                utcNow: () => now);
            Assert.That(broadcaster.TryCreateSession(out var sessionId), Is.True);

            now = now.AddMinutes(31);

            Assert.That(broadcaster.SweepExpired(), Is.EqualTo(1));
            Assert.That(broadcaster.TryTouchSession(sessionId), Is.False);
        }
    }
}
