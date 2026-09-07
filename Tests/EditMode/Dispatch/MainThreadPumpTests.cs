using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class MainThreadPumpTests
    {
        [Test]
        public async Task RunAsync_ExecutesWorkOnPumpTick()
        {
            using var pump = new MainThreadPump(autoTick: false);
            var task = pump.RunAsync<int>(_ => new ValueTask<int>(42), CancellationToken.None);
            Assert.That(task.IsCompleted, Is.False);
            pump.PumpOnce();
            var result = await task;
            Assert.That(result, Is.EqualTo(42));
        }

        [Test]
        public async Task RunAsync_PropagatesExceptions()
        {
            using var pump = new MainThreadPump(autoTick: false);
            var task = pump.RunAsync<int>(_ => throw new System.InvalidOperationException("boom"), CancellationToken.None);
            pump.PumpOnce();
            try
            {
                await task;
                Assert.Fail("should throw");
            }
            catch (System.InvalidOperationException ex)
            {
                Assert.That(ex.Message, Is.EqualTo("boom"));
            }
        }

        [Test]
        public void RunAsync_CancelledBeforeTick_CompletesWithCancelled()
        {
            using var pump = new MainThreadPump(autoTick: false);
            using var cts = new CancellationTokenSource();
            var ran = false;
            var task = pump.RunAsync<int>(_ =>
            {
                ran = true;
                return new ValueTask<int>(1);
            }, cts.Token).AsTask();

            cts.Cancel();

            Assert.That(SpinWait.SpinUntil(() => task.IsCompleted, 100), Is.True, "Cancellation should complete without waiting for an editor tick.");
            Assert.That(async () => await task, Throws.InstanceOf<System.OperationCanceledException>());

            pump.PumpOnce();
            Assert.That(ran, Is.False);
        }
    }
}
