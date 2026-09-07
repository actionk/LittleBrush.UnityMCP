using System;
using System.Threading;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public sealed class EditorLogSink : ILogSink
    {
        private const string PrefixColor = "#74B9FF";
        private readonly SynchronizationContext _mainThreadContext = SynchronizationContext.Current;
        private readonly int _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        public void Log(LogLevel level, string message, Exception exception = null)
        {
            var prefix = $"{Marker(level)} <color={PrefixColor}>[MCP]</color> {message}";
            if (exception != null)
                prefix += $"\n{exception}";

            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _mainThreadContext?.Post(_ => Write(level, prefix), null);
                return;
            }

            Write(level, prefix);
        }

        private static void Write(LogLevel level, string prefix)
        {
            switch (level)
            {
                case LogLevel.Warn:
                    UnityEngine.Debug.LogWarning(prefix);
                    break;
                case LogLevel.Error:
                    UnityEngine.Debug.LogError(prefix);
                    break;
                default:
                    UnityEngine.Debug.Log(prefix);
                    break;
            }
        }

        private static string Marker(LogLevel level)
        {
            var color = level switch
            {
                LogLevel.Debug => "#9099A6",
                LogLevel.Warn => "#FFD166",
                LogLevel.Error => "#FF6B6B",
                _ => "#7FE7C4",
            };
            var glyph = level switch
            {
                LogLevel.Debug => "·",
                LogLevel.Warn => "▲",
                LogLevel.Error => "✖",
                _ => "●",
            };
            return $"<color={color}>{glyph}</color>";
        }
    }
}
