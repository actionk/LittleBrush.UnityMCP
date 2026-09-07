using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace LittleBrushGames.Mcp.Editor.Dispatch
{
    public sealed class MainThreadPump : IMainThreadScheduler, IDisposable
    {
        private readonly ConcurrentQueue<IWorkItem> _queue = new();
        private readonly bool _autoTick;
        private bool _disposed;

        // Stamped each PumpOnce so dispatchers can detect main-thread stalls
        // (modal dialog, frozen reload, focus-loss starvation). Read from
        // background threads — long is atomic on 64-bit, but we still go
        // through Volatile to make the happens-before explicit.
        private long _lastTickUnixMs;

        public long MillisecondsSinceLastTick
        {
            get
            {
                var last = Volatile.Read(ref _lastTickUnixMs);
                if (last == 0) return 0;
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last;
            }
        }

        public MainThreadPump(bool autoTick = true)
        {
            _autoTick = autoTick;
            // Seed lastTick so MillisecondsSinceLastTick reports 0 (healthy) until
            // the editor actually fails to tick.
            Volatile.Write(ref _lastTickUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (autoTick)
                EditorApplication.update += PumpOnce;
        }

        public ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken ct)
        {
            var item = new WorkItem<T>(work, ct);
            _queue.Enqueue(item);
            // Don't call QueuePlayerLoopUpdate here — this method is invoked from the
            // HTTP background thread and QueuePlayerLoopUpdate is main-thread only.
            // The EditorApplication.update tick will drain the queue next frame.
            return new ValueTask<T>(item.Task);
        }

        public ValueTask RunAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct)
        {
            async ValueTask<byte> Wrap(CancellationToken c) { await work(c); return 0; }
            return new ValueTask(RunAsync<byte>(Wrap, ct).AsTask());
        }

        public void PumpOnce()
        {
            // Stamp BEFORE draining so MillisecondsSinceLastTick reflects "we are
            // alive on the main thread" even when a queued item runs long.
            Volatile.Write(ref _lastTickUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            while (_queue.TryDequeue(out var item))
                item.Start();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_autoTick) EditorApplication.update -= PumpOnce;
            while (_queue.TryDequeue(out var item))
                item.Cancel();
        }

        private interface IWorkItem
        {
            void Start();
            void Cancel();
        }

        private sealed class WorkItem<T> : IWorkItem
        {
            private readonly Func<CancellationToken, ValueTask<T>> _work;
            private readonly CancellationToken _ct;
            private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration _registration;

            public Task<T> Task => _tcs.Task;

            public WorkItem(Func<CancellationToken, ValueTask<T>> work, CancellationToken ct)
            {
                _work = work;
                _ct = ct;
                if (ct.CanBeCanceled)
                    _registration = ct.Register(static state =>
                    {
                        var item = (WorkItem<T>)state;
                        item._tcs.TrySetCanceled(item._ct);
                    }, this);
            }

            public void Start()
            {
                if (_tcs.Task.IsCompleted)
                {
                    _registration.Dispose();
                    return;
                }

                if (_ct.IsCancellationRequested)
                {
                    CompleteCanceled(_ct);
                    return;
                }

                try
                {
                    var vt = _work(_ct);
                    if (vt.IsCompletedSuccessfully)
                    {
                        CompleteResult(vt.Result);
                        return;
                    }
                    vt.AsTask().ContinueWith(t =>
                    {
                        if (t.IsCanceled) CompleteCanceled();
                        else if (t.IsFaulted) CompleteException(t.Exception!.InnerException ?? t.Exception);
                        else CompleteResult(t.Result);
                    }, TaskScheduler.Default);
                }
                catch (OperationCanceledException oce)
                {
                    CompleteCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    CompleteException(ex);
                }
            }

            public void Cancel()
            {
                _registration.Dispose();
                _tcs.TrySetCanceled();
            }

            private void CompleteResult(T result)
            {
                _registration.Dispose();
                _tcs.TrySetResult(result);
            }

            private void CompleteException(Exception ex)
            {
                _registration.Dispose();
                _tcs.TrySetException(ex);
            }

            private void CompleteCanceled(CancellationToken ct = default)
            {
                _registration.Dispose();
                if (ct.CanBeCanceled) _tcs.TrySetCanceled(ct);
                else _tcs.TrySetCanceled();
            }
        }
    }
}
