namespace LittleBrushGames.Mcp.Tests.Fakes
{
    public sealed class NoopProgressReporter : IProgressReporter
    {
        public void Report(double progress, string message = null) { }
    }
}
