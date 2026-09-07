using System;

namespace LittleBrushGames.Mcp.Editor.Host
{
    public static class PortResolver
    {
        public const int DefaultPort = 48765;

        public static int Resolve()
        {
            var env = Environment.GetEnvironmentVariable("LITTLEBRUSH_MCP_PORT");
            if (int.TryParse(env, out var port) && port is > 0 and < 65536)
                return port;

            var settings = McpBridgeSettings.GetOrLoad();
            if (settings != null && settings.Port is > 0 and < 65536)
                return settings.Port;

            return DefaultPort;
        }
        public static int ResolveUnityPort()
        {
            var env = Environment.GetEnvironmentVariable("LITTLEBRUSH_MCP_UNITY_PORT");
            if (int.TryParse(env, out var port) && port is > 0 and < 65536)
                return port;

            return McpBridgeSettings.GetOrLoad()?.BridgeUnityPort ?? 48766;
        }
    }
}
