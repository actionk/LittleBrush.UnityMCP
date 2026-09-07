using System;
using System.Collections.Generic;

namespace LittleBrushGames.Mcp.Tests.Fakes
{
    public sealed class FakeLogSink : ILogSink
    {
        public readonly List<(LogLevel level, string message, Exception exception)> Entries = new();

        public void Log(LogLevel level, string message, Exception exception = null)
            => Entries.Add((level, message, exception));
    }
}
