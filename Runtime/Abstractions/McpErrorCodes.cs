namespace LittleBrushGames.Mcp
{
    public static class McpErrorCodes
    {
        public const int ParseError        = -32700;
        public const int InvalidRequest    = -32600;
        public const int MethodNotFound    = -32601;
        public const int InvalidParams     = -32602;
        public const int InternalError     = -32603;

        public const int ToolError          = -32000;
        public const int ToolUnavailable    = -32001;
        public const int Compiling          = -32002;
        public const int ValidationFailed   = -32003;
        public const int MainThreadBusy     = -32004;
        public const int ServiceUnavailable = -32005;
        public const int Cancelled          = -32006;
        public const int NotFound           = -32007;
        public const int Conflict           = -32008;
        public const int ExclusiveBusy      = -32009;
    }
}
