namespace LittleBrushGames.Mcp
{
    public abstract class ContentBlock
    {
    }

    public sealed class TextContent : ContentBlock
    {
        public string Text { get; init; }
    }

    public sealed class ImageContent : ContentBlock
    {
        public byte[] Data { get; init; }
        public string MimeType { get; init; } = "image/png";
    }

    public sealed class ResourceLinkContent : ContentBlock
    {
        public string Uri { get; init; }
        public string Name { get; init; }
        public string MimeType { get; init; }
    }
}
