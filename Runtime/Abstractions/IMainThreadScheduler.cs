using System;
using System.Threading;
using System.Threading.Tasks;

namespace LittleBrushGames.Mcp
{
    public interface IMainThreadScheduler
    {
        ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken ct);
        ValueTask RunAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct);

        /// <summary>
        /// Milliseconds since the scheduler last drained its queue. Used to detect
        /// editor stalls (modal dialogs, frozen reload, focus-loss starvation) so
        /// dispatchers can fail fast instead of queueing indefinitely. Returns 0
        /// before the first tick.
        /// </summary>
        long MillisecondsSinceLastTick { get; }
    }
}
