using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp.Editor.Dispatch;
using NUnit.Framework;

namespace LittleBrushGames.Mcp.Tests.Dispatch
{
    public class EditorLogSinkTests
    {
        [Test]
        public void Log_FromWorkerThread_PostsToCapturedContext()
        {
            var previousContext = SynchronizationContext.Current;
            var context = new RecordingSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var sink = new EditorLogSink();

                Task.Run(() => sink.Log(LogLevel.Info, "background")).GetAwaiter().GetResult();

                Assert.That(context.PostCount, Is.EqualTo(1));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        private sealed class RecordingSynchronizationContext : SynchronizationContext
        {
            public int PostCount { get; private set; }

            public override void Post(SendOrPostCallback callback, object state)
            {
                PostCount++;
            }
        }
    }
}
