using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    /// <summary>
    /// Frame waiter backed by <see cref="EditorApplication.update"/>.
    /// Polls predicates each editor tick until satisfied or timeout.
    /// </summary>
    public sealed class EditorFrameWaiter : IFrameWaiter, IDisposable
    {
        private readonly List<IWaiter> _waiters = new();
        private readonly object _lock = new();
        private bool _disposed;

        public EditorFrameWaiter()
        {
            EditorApplication.update += Tick;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorApplication.update -= Tick;
            lock (_lock)
            {
                foreach (var w in _waiters)
                    w.Cancel();
                _waiters.Clear();
            }
        }

        public ValueTask WaitNextFrameAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return new ValueTask(Task.FromCanceled(ct));

            var waiter = new FrameWaiter(1, ct);
            lock (_lock) _waiters.Add(waiter);
            return new ValueTask(waiter.Task);
        }

        public ValueTask DelayFramesAsync(int frames, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return new ValueTask(Task.FromCanceled(ct));
            if (frames <= 0)
                return default;

            var waiter = new FrameWaiter(frames, ct);
            lock (_lock) _waiters.Add(waiter);
            return new ValueTask(waiter.Task);
        }

        public ValueTask WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return new ValueTask(Task.FromCanceled(ct));

            // Check immediately
            try
            {
                if (predicate())
                    return default;
            }
            catch (Exception ex)
            {
                return new ValueTask(Task.FromException(ex));
            }

            var waiter = new PredicateWaiter(predicate, timeout, ct);
            lock (_lock) _waiters.Add(waiter);
            return new ValueTask(waiter.Task);
        }

        private void Tick()
        {
            List<IWaiter> snapshot;
            lock (_lock)
            {
                if (_waiters.Count == 0) return;
                snapshot = new List<IWaiter>(_waiters);
            }

            var toRemove = new List<IWaiter>();
            foreach (var w in snapshot)
            {
                if (w.Tick())
                    toRemove.Add(w);
            }

            if (toRemove.Count > 0)
            {
                lock (_lock)
                {
                    foreach (var w in toRemove)
                        _waiters.Remove(w);
                }
            }
        }

        private interface IWaiter
        {
            Task Task { get; }
            bool Tick(); // returns true when done
            void Cancel();
        }

        private sealed class FrameWaiter : IWaiter
        {
            private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _remaining;

            public Task Task => _tcs.Task;

            public FrameWaiter(int frames, CancellationToken ct)
            {
                _remaining = frames;
                ct.Register(() => _tcs.TrySetCanceled(ct));
            }

            public bool Tick()
            {
                if (_tcs.Task.IsCompleted) return true;
                if (--_remaining <= 0)
                {
                    _tcs.TrySetResult(true);
                    return true;
                }
                return false;
            }

            public void Cancel() => _tcs.TrySetCanceled();
        }

        private sealed class PredicateWaiter : IWaiter
        {
            private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Func<bool> _predicate;
            private readonly DateTime? _deadline;

            public Task Task => _tcs.Task;

            public PredicateWaiter(Func<bool> predicate, TimeSpan? timeout, CancellationToken ct)
            {
                _predicate = predicate;
                _deadline = timeout.HasValue ? DateTime.UtcNow + timeout.Value : null;
                ct.Register(() => _tcs.TrySetCanceled(ct));
            }

            public bool Tick()
            {
                if (_tcs.Task.IsCompleted) return true;

                // Check timeout
                if (_deadline.HasValue && DateTime.UtcNow >= _deadline.Value)
                {
                    _tcs.TrySetException(new TimeoutException("WaitUntilAsync timed out."));
                    return true;
                }

                // Check predicate
                try
                {
                    if (_predicate())
                    {
                        _tcs.TrySetResult(true);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                    return true;
                }

                return false;
            }

            public void Cancel() => _tcs.TrySetCanceled();
        }
    }
}
