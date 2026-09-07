using System;
using System.Threading;
using System.Threading.Tasks;

namespace LittleBrushGames.Mcp.Tests.Fakes
{
    public sealed class FakeFrameWaiter : IFrameWaiter
    {
        public ValueTask WaitNextFrameAsync(CancellationToken ct) => default;

        public ValueTask WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout, CancellationToken ct)
        {
            if (!predicate()) throw new TimeoutException();
            return default;
        }

        public ValueTask DelayFramesAsync(int frames, CancellationToken ct) => default;
    }
}
