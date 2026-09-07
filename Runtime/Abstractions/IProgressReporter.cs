namespace LittleBrushGames.Mcp
{
    public interface IProgressReporter
    {
        void Report(double progress, string message = null);
    }
}
