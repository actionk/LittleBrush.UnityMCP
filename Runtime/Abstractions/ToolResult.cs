using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp
{
    public sealed class ToolResult
    {
        public bool IsError { get; init; }
        public bool IsExpectedFailure { get; init; }
        public JObject StructuredContent { get; init; }
        public IReadOnlyList<ContentBlock> Content { get; init; } = Array.Empty<ContentBlock>();

        public static ToolResult Ok(JObject structured, params ContentBlock[] content)
            => new() { StructuredContent = structured, Content = content ?? Array.Empty<ContentBlock>() };

        public static ToolResult Text(string text)
            => new() { Content = new ContentBlock[] { new TextContent { Text = text } } };

        public static ToolResult Error(string message)
            => new()
            {
                IsError = true,
                IsExpectedFailure = true,
                Content = new ContentBlock[] { new TextContent { Text = message } },
            };

        /// <summary>
        /// Error result that carries both a human-readable message (for MCP clients that
        /// only surface <c>isError</c>+text) and a structured diagnostic envelope (for
        /// callers that inspect structuredContent, e.g. compile-error details, stack traces).
        /// </summary>
        public static ToolResult ErrorWithData(string message, JObject structured, bool expectedFailure = true)
            => new()
            {
                IsError = true,
                IsExpectedFailure = expectedFailure,
                StructuredContent = structured,
                Content = new ContentBlock[] { new TextContent { Text = message ?? string.Empty } },
            };
    }
}
