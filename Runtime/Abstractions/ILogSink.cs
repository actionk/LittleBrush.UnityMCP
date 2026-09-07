using System;

namespace LittleBrushGames.Mcp
{
    public enum LogLevel { Debug, Info, Warn, Error }

    public interface ILogSink
    {
        void Log(LogLevel level, string message, Exception exception = null);
    }
}
