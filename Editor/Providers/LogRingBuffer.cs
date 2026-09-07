using System;
using UnityEngine;

namespace LittleBrushGames.Mcp.Editor.Providers
{
    public sealed class LogRingBuffer
    {
        public readonly struct Entry
        {
            public string Message { get; }
            public string StackTrace { get; }
            public LogType Type { get; }
            public long Timestamp { get; }
            public long Sequence { get; }

            public Entry(string message, string stackTrace, LogType type, long timestamp, long sequence)
            {
                Message = message;
                StackTrace = stackTrace;
                Type = type;
                Timestamp = timestamp;
                Sequence = sequence;
            }
        }

        private readonly Entry[] _buffer;
        private int _start;
        private int _count;
        private long _nextSequence;
        private readonly object _lock = new();

        public int Capacity => _buffer.Length;
        public int Count { get { lock (_lock) return _count; } }

        public LogRingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buffer = new Entry[capacity];
        }

        public void Add(string message, string stackTrace, LogType type)
        {
            lock (_lock)
            {
                var entry = new Entry(
                    message,
                    stackTrace,
                    type,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ++_nextSequence);
                if (_count < _buffer.Length)
                {
                    _buffer[(_start + _count) % _buffer.Length] = entry;
                    _count++;
                }
                else
                {
                    _buffer[_start] = entry;
                    _start = (_start + 1) % _buffer.Length;
                }
            }
        }

        public Entry[] Snapshot()
        {
            lock (_lock)
            {
                var result = new Entry[_count];
                for (var i = 0; i < _count; i++)
                    result[i] = _buffer[(_start + i) % _buffer.Length];
                return result;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _start = 0;
                _count = 0;
            }
        }
    }
}
