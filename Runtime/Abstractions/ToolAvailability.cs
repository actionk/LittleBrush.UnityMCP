using System;

namespace LittleBrushGames.Mcp
{
    [Flags]
    public enum ToolAvailability
    {
        None      = 0,
        EditMode  = 1 << 0,
        PlayMode  = 1 << 1,
        Compiling = 1 << 2,
        Either    = EditMode | PlayMode,
        Always    = EditMode | PlayMode | Compiling,
    }
}
