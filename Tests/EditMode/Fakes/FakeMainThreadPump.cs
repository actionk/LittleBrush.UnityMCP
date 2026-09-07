using System;
using System.Threading;
using System.Threading.Tasks;

namespace LittleBrushGames.Mcp.Tests.Fakes
{
    public sealed class FakeMainThreadPump : IMainThreadScheduler
    {
        public ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken ct)
            => work(ct);

        public ValueTask RunAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct)
            => work(ct);

        public long MillisecondsSinceLastTick => 0;
    }
}
