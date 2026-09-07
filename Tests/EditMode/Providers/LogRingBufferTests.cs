using LittleBrushGames.Mcp.Editor.Providers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LittleBrushGames.Mcp.Tests.Providers
{
    public class LogRingBufferTests
    {
        [Test]
        public void Add_OverCapacity_EvictsOldest()
        {
            var buf = new LogRingBuffer(capacity: 3);
            buf.Add("a", "", LogType.Log);
            buf.Add("b", "", LogType.Log);
            buf.Add("c", "", LogType.Log);
            buf.Add("d", "", LogType.Log);
            var entries = buf.Snapshot();
            Assert.That(entries.Length, Is.EqualTo(3));
            Assert.That(entries[0].Message, Is.EqualTo("b"));
            Assert.That(entries[2].Message, Is.EqualTo("d"));
        }

        [Test]
        public void Add_AssignsIncreasingSequence()
        {
            var buf = new LogRingBuffer(capacity: 3);
            buf.Add("a", "", LogType.Log);
            buf.Add("b", "", LogType.Log);

            var entries = buf.Snapshot();
            Assert.That(entries[0].Sequence, Is.GreaterThan(0));
            Assert.That(entries[1].Sequence, Is.GreaterThan(entries[0].Sequence));
        }

        [Test]
        public void Clear_RemovesAll()
        {
            var buf = new LogRingBuffer(capacity: 3);
            buf.Add("a", "", LogType.Log);
            buf.Clear();
            Assert.That(buf.Snapshot(), Is.Empty);
        }

        [Test]
        public void Tail_DefaultsToTwentyWithoutStackTraces()
        {
            var buf = new LogRingBuffer(capacity: 30);
            for (var i = 0; i < 25; i++) buf.Add($"message {i}", "large stack", LogType.Log);

            var compact = LogsProvider.CreateTailResponse(buf.Snapshot(), new JObject());
            Assert.That((int)compact["returned"], Is.EqualTo(20));
            Assert.That((bool)compact["truncated"], Is.True);
            Assert.That(compact["entries"][0]["stackTrace"], Is.Null);

            var detailed = LogsProvider.CreateTailResponse(buf.Snapshot(), new JObject
            {
                ["limit"] = 1,
                ["includeStackTrace"] = true,
            });
            Assert.That((string)detailed["entries"][0]["stackTrace"], Is.EqualTo("large stack"));
        }

        [Test]
        public void Tail_BoundsMessageAndStackText()
        {
            var buf = new LogRingBuffer(capacity: 1);
            buf.Add(new string('m', 600), new string('s', 700), LogType.Error);

            var result = LogsProvider.CreateTailResponse(buf.Snapshot(), new JObject
            {
                ["limit"] = 1,
                ["includeStackTrace"] = true,
                ["maxTextCharacters"] = 256,
            });
            var entry = result["entries"][0];

            Assert.That(((string)entry["message"]).Length, Is.EqualTo(256));
            Assert.That((bool)entry["messageTruncated"], Is.True);
            Assert.That((int)entry["messageCharacters"], Is.EqualTo(600));
            Assert.That(((string)entry["stackTrace"]).Length, Is.EqualTo(256));
            Assert.That((bool)entry["stackTraceTruncated"], Is.True);
        }
    }
}
