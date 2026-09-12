# Consumer Integration

## Codex

Create `.codex/config.toml` at your project root for project-scoped setup, or `~/.codex/config.toml` for global setup:

```toml
[mcp_servers.unity]
url = "http://127.0.0.1:48765/mcp"
```

Restart Codex, then run `/mcp`. The `unity` server should appear with its tools listed. The MCP window installer/remover updates only the `unity` block, so repeated installs or removes preserve unrelated TOML sections.

## OpenCode

Create `opencode.json` at your project root for project-scoped setup, or `~/.config/opencode/opencode.json` for global setup:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity": {
      "type": "remote",
      "url": "http://127.0.0.1:48765/mcp",
      "enabled": true
    }
  }
}
```

Restart OpenCode. The `unity` MCP server will be available as an OpenCode tool source.

## Claude Code

Create `.mcp.json` at your project root for project-scoped setup, or add `mcpServers.unity` to `~/.claude.json` for user-scoped setup:

```json
{
  "mcpServers": {
    "unity": { "type": "http", "url": "http://127.0.0.1:48765/mcp" }
  }
}
```

Restart Claude Code. Run `/mcp`. The `unity` server should appear with its tools listed.

## Cursor

Create `.cursor/mcp.json` at your project root for project-scoped setup, or `~/.cursor/mcp.json` for global setup:

```json
{
  "mcpServers": {
    "unity": { "type": "http", "url": "http://127.0.0.1:48765/mcp" }
  }
}
```

## VS Code Copilot

Create `.vscode/mcp.json` at your project root for workspace setup, or the user-level `mcp.json` file for global setup:

```json
{
  "servers": {
    "unity": { "type": "http", "url": "http://127.0.0.1:48765/mcp" }
  }
}
```

On Windows, VS Code's user-level file is `%APPDATA%\Code\User\mcp.json`.

## Windsurf

Windsurf currently uses a global MCP config file at `~/.codeium/windsurf/mcp_config.json`:

```json
{
  "mcpServers": {
    "unity": { "serverUrl": "http://127.0.0.1:48765/mcp" }
  }
}
```

## Port override

Default HTTP port is `48765`. Override via `LITTLEBRUSH_MCP_PORT` before launching Unity; this applies to both direct and bridge transports. In bridge mode, also assign a distinct internal TCP port with `LITTLEBRUSH_MCP_UNITY_PORT` (otherwise the saved Unity transport port is used). For example, use `48769` and `48770` respectively when running another project alongside the defaults. A bridge owned by another project is rejected without terminating it.

## Adding editor tools

Mark a class with `[McpToolProvider]` and implement `IToolProvider`. The plugin auto-discovers it on editor load via reflection scan.

Editor providers can live in any editor asmdef that references `LittleBrushGames.UnityMCP.Editor`. See `provider-authoring.md`.

## Adding runtime tools (consumer-side)

Runtime providers (game-side ECS dumps, save state, AI inspectors, etc) install themselves via the static bridge:

```csharp
var providers = new IToolProvider[] { new MyEcsInspector(world), new MySaveInspector(save) };
_mcpScope = McpRuntimeBridge.Install(providers, new PlainServiceProvider());
```

Dispose `_mcpScope` on shutdown (scene unload, server scope dispose, application quit).

## VContainer adapter

```csharp
public sealed class ServerMcpBootstrap : IStartable, IDisposable
{
    private readonly IObjectResolver _resolver;
    private readonly IReadOnlyList<IToolProvider> _providers;
    private IDisposable _scope;

    public ServerMcpBootstrap(IObjectResolver resolver, IReadOnlyList<IToolProvider> providers)
    {
        _resolver = resolver;
        _providers = providers;
    }

    public void Start()
        => _scope = McpRuntimeBridge.Install(_providers, new VContainerServiceProvider(_resolver));

    public void Dispose() => _scope?.Dispose();
}
```

The `VContainerServiceProvider` adapter lives in your project, not the plugin. The plugin has zero references to VContainer (or any DI framework).
