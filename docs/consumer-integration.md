# Consumer Integration

## Automatic startup and Unity CLI

In **Getting Started**, enable **Start Unity automatically when a tool needs it**, then install or update the desired client configuration. The same native bridge executable runs as a small stdio MCP launcher. It advertises `unity.tools` and `unity.call` immediately; the first tool request starts a batch Editor if the configured project is closed. An already-running bridge is reused only after verifying its project identity. Each project needs distinct HTTP/TCP ports in MCP settings and its matching client configuration.

The launcher requires the project's licensed Unity version installed locally and a native bridge binary. It preserves graphics for rendering, launches Windows workers without a visible console, and waits up to ten minutes for startup/import. Only workers launched with `-littlebrushMcpWorker` exit automatically, after five idle minutes. Active calls, handler cleanup, provider background jobs, imports, compilation, Play Mode, and dirty loaded scenes keep them alive. It never closes a manually opened Editor. Close a managed worker normally before opening that same project interactively; Unity permits only one Editor per project.

Manual Codex example (replace paths and ports):

```toml
[mcp_servers.unity]
command = 'D:\Projects\Example\Assets\Plugins\LittleBrushGames\Mcp\Bridge~\mcp-bridge.exe'
args = ['--stdio', '--project', 'D:\Projects\Example', '--port', '48765', '--unity-port', '48766']
startup_timeout_sec = 15
tool_timeout_sec = 960
```

The launcher finds the exact version from `ProjectSettings/ProjectVersion.txt` in standard Unity Hub locations. For a custom installation, supply `--editor` with that version's executable (the installer captures the current executable). Update that path when upgrading Unity. `--idle-timeout` and `--startup-timeout` accept Go durations such as `5m`. Startup failures point to `Logs/littlebrush-mcp-worker.log`. A port owned by another project fails without taking it over. Failed tool calls are not automatically replayed by the launcher; existing bridge reload-safe rules still apply. Stdio supports cancellation, but does not forward the HTTP bridge's logging/progress SSE stream; use result/polling tools for long jobs.

### Optional official Unity CLI adapter

Install `com.unity.pipeline@0.6.0-exp.1` or a compatible newer version through Unity Package Manager. The plugin's optional Pipeline assembly registers two commands without making Pipeline a dependency of the base MCP plugin. Verified with Unity CLI `1.0.0-beta.9`, Pipeline `0.6.0-exp.1`, and Unity `6000.6.0f1` on Windows. macOS/Linux launch helpers cross-compile but have not been tested live.

```powershell
# Attach to a running Editor or worker:
unity command lbg_mcp_tools --project-path 'D:\Projects\Example' --query '{"name":"editor.status"}'
unity command lbg_mcp_call --project-path 'D:\Projects\Example' --tool editor.status

# Start a batch Editor, execute one command, then exit:
unity run 'D:\Projects\Example' --command lbg_mcp_call -- --tool editor.status
```

The `lbg_mcp_*` commands use the same router, validation, trust policy, writer lease, and handlers as MCP. They return structured results and inline images; gateway errors fail the CLI command. Pipeline's own commands are separate APIs and do not inherit LittleBrush trust controls. The stdio launcher does not need Unity CLI or Pipeline to start an Editor.

### Batch capabilities and permissions

`editor.status.worker` reports batch mode, managed ownership, and graphics availability. Tool schemas expose `requiresGraphics`, `requiresInteractiveEditor`, and `environmentUnavailableReason`. Graphics-backed batch workers can render prefab/scene previews. For a 3D prefab without a loaded scene camera, set `standaloneCamera: true` on `prefab.preview_screenshot`; the neutral camera does not reproduce gameplay camera effects. Scene previews still need the scene's camera. Editor window/SceneView/GameView captures require an interactive Editor. `-nographics` cannot render previews and returns `graphics_device_required` for declared graphics tools.

`Ask` permissions return `approval_required_in_batch` immediately. Select the intended per-project trust policy interactively before relying on unattended mutations; the launcher never changes it. Dirty or populated untitled scenes remain protected by the test runner; a fresh batch worker's empty scene can be restored after tests. Third-party/custom providers must declare UI/graphics requirements and report deferred jobs as described in [provider authoring](provider-authoring.md); installing Pipeline cannot make arbitrary GUI-only code batch-compatible.

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
