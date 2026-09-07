using System;
using Newtonsoft.Json.Linq;

namespace LittleBrushGames.Mcp
{
    public sealed class McpToolException : Exception
    {
        public int Code { get; }
        public new JObject Data { get; }

        public McpToolException(int code, string message, JObject data = null) : base(message)
        {
            Code = code;
            Data = data;
        }
    }
}
