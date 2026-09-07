namespace LittleBrushGames.Mcp
{
    public interface IToolProvider
    {
        string Namespace { get; }
        void RegisterTools(IToolRegistration reg);
    }
}
