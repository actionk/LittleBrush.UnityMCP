using System;
using System.Threading;
using System.Threading.Tasks;

namespace LittleBrushGames.Mcp
{
    public interface IFrameWaiter
    {
        ValueTask WaitNextFrameAsync(CancellationToken ct);
        ValueTask WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout, CancellationToken ct);
        ValueTask DelayFramesAsync(int frames, CancellationToken ct);
    }
}
