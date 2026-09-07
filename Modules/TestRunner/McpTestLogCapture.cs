#if UNITY_TESTS_FRAMEWORK
using System;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LittleBrushGames.Mcp.Modules.TestRunner
{
    /// <summary>
    /// Routes managed Debug logs to Unity's managed callbacks without writing
    /// them to the native Console. Used only while MCP owns an EditMode run.
    /// </summary>
    internal sealed class McpTestLogCapture : ILogHandler, IDisposable
    {
        private static readonly MethodInfo s_callLogCallback = typeof(Application).GetMethod(
            "CallLogCallback",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(string), typeof(LogType), typeof(bool) },
            null);

        private readonly ILogHandler _fallback;
        private readonly Action<string, string, LogType, bool> _managedCallback;
        private readonly int _mainThreadId;
        private bool _disposed;

        internal McpTestLogCapture(
            ILogHandler fallback,
            Action<string, string, LogType, bool> managedCallback,
            int mainThreadId)
        {
            _fallback = fallback;
            _managedCallback = managedCallback;
            _mainThreadId = mainThreadId;
        }

        internal static McpTestLogCapture TryInstall()
        {
            if (s_callLogCallback == null || Debug.unityLogger.logHandler is McpTestLogCapture)
                return null;

            try
            {
                var callback = (Action<string, string, LogType, bool>)s_callLogCallback.CreateDelegate(
                    typeof(Action<string, string, LogType, bool>));
                var capture = new McpTestLogCapture(
                    Debug.unityLogger.logHandler,
                    callback,
                    Thread.CurrentThread.ManagedThreadId);
                Debug.unityLogger.logHandler = capture;
                return capture;
            }
            catch
            {
                return null;
            }
        }

        public void LogFormat(LogType logType, Object context, string format, params object[] args)
        {
            try
            {
                var message = args == null || args.Length == 0
                    ? format
                    : string.Format(format, args);
                _managedCallback(
                    message,
                    StackTraceUtility.ExtractStackTrace(),
                    logType,
                    Thread.CurrentThread.ManagedThreadId == _mainThreadId);
            }
            catch
            {
                _fallback?.LogFormat(logType, context, format, args);
            }
        }

        public void LogException(Exception exception, Object context)
        {
            try
            {
                var message = exception == null
                    ? "Null"
                    : $"{exception.GetType().FullName}: {exception.Message}";
                _managedCallback(
                    message,
                    exception?.StackTrace ?? string.Empty,
                    LogType.Exception,
                    Thread.CurrentThread.ManagedThreadId == _mainThreadId);
            }
            catch
            {
                _fallback?.LogException(exception, context);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(Debug.unityLogger.logHandler, this))
                Debug.unityLogger.logHandler = _fallback;
        }
    }
}
#endif
