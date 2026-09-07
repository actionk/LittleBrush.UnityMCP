# MCP Bridge Protocol Specification

MCP protocol version: 2025-06-18

## Overview

The MCP Bridge is a standalone process that proxies MCP requests between clients (Claude Code, agents) and Unity Editor. It survives Unity domain reloads, providing connection stability.

```
[Clients] ──HTTP/SSE──► [Bridge :48765] ──IPC──► [Unity :48766]
                              │                       │
                              │   ◄── reconnects ─────┘
                              └── buffers during reload
```

## Transport

### Client ↔ Bridge

Standard MCP over HTTP:

The bridge rejects unsupported non-empty `MCP-Protocol-Version` request headers. A successful `initialize` response returns a `Mcp-Session-Id`; the client sends that exact header on subsequent `POST`, SSE `GET`, and `DELETE` requests. Disconnecting SSE keeps the session available for reconnect, while `DELETE /mcp` terminates it. The bridge accepts at most 64 initialized sessions and expires sessions that have no active SSE stream after 30 minutes without a request.

- `POST /mcp` — JSON-RPC requests
- `GET /mcp` (Accept: text/event-stream) — attach an SSE notification stream to the initialized session
- `DELETE /mcp` — terminate the initialized session
- `GET /mcp/health` — health check

Port: **48765** (configurable)

### Bridge ↔ Unity

Newline-delimited JSON over TCP (or named pipe).

Port: **48766** (configurable)
Named pipe: `\\.\pipe\unity-mcp-{project-hash}` (Windows) or `/tmp/unity-mcp-{project-hash}.sock` (Unix)

Bridge tries named pipe first, falls back to TCP.

## Message Format (Bridge ↔ Unity)

All messages are JSON objects, one per line (NDJSON). Each message has a `type` field.

### Handshake

Unity connects to bridge and sends hello:

```json
{"type":"hello","version":"1.0","projectPath":"D:/Projects/MyGame","projectHash":"a1b2c3","processId":1234,"reloadSafeTools":["editor.status","prefab.read"]}
```

Bridge responds:

```json
{"type":"welcome","bridgeVersion":"1.0","bufferedRequests":0}
```

If version mismatch, bridge sends error and closes:

```json
{"type":"error","code":"VERSION_MISMATCH","message":"Expected 1.0, got 0.9"}
```

### Request/Response

Bridge forwards client MCP request to Unity:

```json
{"type":"request","id":"req-001","payload":{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{...}}}
```

Unity responds:

```json
{"type":"response","id":"req-001","payload":{"jsonrpc":"2.0","id":1,"result":{...}}}
```

The `id` field correlates request/response. The `payload` is the raw MCP JSON-RPC message, passed through unmodified.

The public MCP surface advertises `unity.tools` and `unity.call`. For bridge replay and test-run policy, the bridge resolves `unity.call.arguments.tool` as the effective tool name and applies replay markers to its nested `arguments` object. Unity derives `reloadSafeTools` from registered tool descriptors on every handshake; this is the authoritative replay policy. Before Unity supplies that policy, only `initialize`, `ping`, and `tools/list` are replay-safe.

Successful JSON-only tool calls return the value once in `structuredContent` with an empty required `content` array. The server does not duplicate structured JSON as text. Text, image, and resource-link tools populate `content`; tool errors keep a concise text message and may include structured diagnostics.

### Notifications (Unity → Bridge → Clients)

Unity can send notifications to broadcast to all SSE clients:

```json
{"type":"notification","payload":{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}}
```

Bridge queues notifications per initialized session and delivers them through that session's SSE stream. This prevents a tool-list change emitted just before the stream connects or reconnects from being lost.

### Lifecycle Events

**Unity about to reload:**

```json
{"type":"lifecycle","event":"reloading"}
```

Bridge enters buffering mode:
- Queues all fresh incoming requests, including mutations (default max 60 seconds)
- Keeps client connections alive
- Returns 503 for new requests if queue full

**Unity back after reload:**

Unity reconnects and sends `hello` again. Bridge:
1. Sends `welcome` with `bufferedRequests` count
2. Dispatches buffered requests in order while receiving responses concurrently
3. Resumes normal operation

**Unity shutting down (editor closing):**

```json
{"type":"lifecycle","event":"shutdown"}
```

Bridge behavior (configurable):
- `exit` — bridge process exits
- `wait` — bridge waits for new Unity connection (timeout: 60s)

### Health Check

Bridge exposes HTTP health endpoint:

```
GET /mcp/health

Response:
{
  "ok": true,
  "bridgeVersion": "1.0",
  "unityConnected": true,
  "unityProject": "D:/Projects/MyGame",
  "clientSessions": 3,
  "bufferedRequests": 0,
  "state": "ready"  // "ready" | "buffering" | "waiting_for_unity"
}
```

Unity can check this before spawning a new bridge.

## States

### Bridge States

```
                    ┌─────────────────┐
                    │  STARTING       │
                    └────────┬────────┘
                             │ listening on ports
                             ▼
                    ┌─────────────────┐
    ┌──────────────►│  WAITING_UNITY  │◄──────────────┐
    │               └────────┬────────┘               │
    │                        │ Unity connects         │
    │                        ▼                        │
    │               ┌─────────────────┐               │
    │               │     READY       │───────────────┤
    │               └────────┬────────┘               │
    │                        │ Unity disconnects      │
    │   timeout              │ (reload or crash)      │
    │   (60s)                ▼                        │
    │               ┌─────────────────┐               │
    └───────────────│   BUFFERING     │───────────────┘
                    └─────────────────┘  Unity reconnects
```

### Request Handling by State

| State | Incoming Request | Behavior |
|-------|------------------|----------|
| READY | Any | Proxy to Unity |
| BUFFERING | Any fresh request | Queue (max 100 requests, 60s default timeout) |
| WAITING_UNITY | Any fresh request | Queue (max 100 requests, 60s default timeout) |

Waiting for the first dispatch is not replay. Fresh queued requests are sent without
`__mcpReplay`; cancelled or expired entries are removed before dispatch. A reload
lifecycle event pauses dispatch even before the old socket closes.

Only requests already dispatched without a confirmed response require replay safety.
Those explicitly marked reload-safe may be retried with `__mcpReplay`; otherwise
the bridge returns `REPLAY_REJECTED` with `executionState: "unknown"`. Check the
operation's result before manually retrying a mutation with an unknown outcome.

## Error Codes

| Code | HTTP Status | Meaning |
|------|-------------|---------|
| `UNITY_DISCONNECTED` | 503 | Unity not connected |
| `UNITY_RELOADING` | 503 | Unity reloading, request queued |
| `BUFFER_FULL` | 503 | Too many buffered requests |
| `BUFFER_TIMEOUT` | 504 | Request timed out in buffer |
| `VERSION_MISMATCH` | 502 | Protocol version incompatible |
| `INTERNAL_ERROR` | 500 | Bridge internal error |

Error response format:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32000,
    "message": "Unity reloading, request queued",
    "data": {"bridgeError": "UNITY_RELOADING", "queuePosition": 3}
  }
}
```

## Configuration

Bridge reads config from (in order):
1. Command-line args
2. Environment variables
3. `mcp-bridge.json` in working directory
4. Defaults

| Setting | CLI | Env | Default |
|---------|-----|-----|---------|
| Client port | `--port` | `MCP_BRIDGE_PORT` | 48765 |
| Unity port | `--unity-port` | `MCP_BRIDGE_UNITY_PORT` | 48766 |
| Buffer timeout | `--buffer-timeout` | `MCP_BRIDGE_BUFFER_TIMEOUT` | 60s |
| Buffer max | `--buffer-max` | `MCP_BRIDGE_BUFFER_MAX` | 100 |
| Unity wait timeout | `--unity-timeout` | `MCP_BRIDGE_UNITY_TIMEOUT` | 60s |
| On shutdown | `--on-shutdown` | `MCP_BRIDGE_ON_SHUTDOWN` | exit |
| Log level | `--log` | `MCP_BRIDGE_LOG` | info |

## Unity Integration

### Spawning the Bridge

```csharp
// In McpBridgeHost.cs

private static Process s_bridgeProcess;

private static void EnsureBridgeRunning()
{
    // Check if already running
    try
    {
        using var client = new HttpClient();
        var response = client.GetAsync("http://127.0.0.1:48765/mcp/health").Result;
        if (response.IsSuccessStatusCode) return; // Already running
    }
    catch { /* Not running */ }

    // Spawn bridge
    var bridgePath = Path.Combine(Application.dataPath, "Plugins/LittleBrushGames/Mcp/Bridge~/mcp-bridge.exe");
    s_bridgeProcess = Process.Start(new ProcessStartInfo
    {
        FileName = bridgePath,
        Arguments = $"--port 48765 --unity-port 48766",
        UseShellExecute = false,
        CreateNoWindow = true,
    });
}
```

### Connecting to Bridge

```csharp
// Unity connects as TCP client to bridge's Unity port
private static TcpClient s_bridgeConnection;
private static StreamWriter s_writer;
private static StreamReader s_reader;

private static void ConnectToBridge()
{
    s_bridgeConnection = new TcpClient("127.0.0.1", 48766);
    var stream = s_bridgeConnection.GetStream();
    s_writer = new StreamWriter(stream) { AutoFlush = true };
    s_reader = new StreamReader(stream);

    // Send hello
    var hello = new JObject
    {
        ["type"] = "hello",
        ["version"] = "1.0",
        ["projectPath"] = Application.dataPath,
        ["projectHash"] = ComputeProjectHash(),
        ["processId"] = Process.GetCurrentProcess().Id,
        ["reloadSafeTools"] = new JArray(router.ReloadSafeToolNames())
    };
    s_writer.WriteLine(hello.ToString(Formatting.None));

    // Read welcome
    var welcomeJson = s_reader.ReadLine();
    var welcome = JObject.Parse(welcomeJson);
    if (welcome["type"].ToString() == "error")
        throw new Exception(welcome["message"].ToString());
}
```

### Lifecycle Notifications

```csharp
// Before domain reload
AssemblyReloadEvents.beforeAssemblyReload += () =>
{
    SendLifecycleEvent("reloading");
    // Don't close connection — let it drop naturally
};

// On editor quit
EditorApplication.quitting += () =>
{
    SendLifecycleEvent("shutdown");
    s_bridgeProcess?.Kill();
};

private static void SendLifecycleEvent(string evt)
{
    var msg = new JObject { ["type"] = "lifecycle", ["event"] = evt };
    s_writer?.WriteLine(msg.ToString(Formatting.None));
}
```

## Sequence Diagrams

### Normal Request Flow

```
Client              Bridge              Unity
  │                   │                   │
  │──POST /mcp───────►│                   │
  │                   │──{"type":"request"}─►│
  │                   │                   │
  │                   │◄─{"type":"response"}─│
  │◄──200 OK─────────│                   │
  │                   │                   │
```

### Domain Reload Flow

```
Client              Bridge              Unity
  │                   │                   │
  │                   │◄─{"lifecycle":"reloading"}─│
  │                   │                   │
  │                   │    [Unity disconnects]
  │                   │                   │
  │──POST /mcp───────►│                   │
  │                   │──[queue request]──│
  │                   │                   │
  │                   │    [Unity reconnects]
  │                   │                   │
  │                   │◄─{"type":"hello"}───│
  │                   │──{"type":"welcome"}─►│
  │                   │                   │
  │                   │──{"type":"request"}─►│ (replayed)
  │                   │◄─{"type":"response"}─│
  │◄──200 OK─────────│                   │
  │                   │                   │
```

## File Locations

```
Assets/Plugins/LittleBrushGames/Mcp/
├── Bridge~/                    # Excluded from Unity import
│   ├── mcp-bridge.exe          # Windows binary
│   ├── mcp-bridge              # Linux/Mac binary
│   ├── go.mod
│   ├── main.go
│   └── README.md
├── Editor/
│   ├── Host/
│   │   └── McpBridgeHost.cs    # Modified to use bridge
│   └── Transport/
│       └── BridgeTransport.cs  # New: connects to bridge
└── docs/
    └── bridge-protocol.md      # This file
```

The `Bridge~` folder uses Unity's special naming to exclude it from import while keeping it in the project.

## Versioning

Protocol version uses semver: `MAJOR.MINOR`

- MAJOR bump: breaking changes (bridge and Unity must match)
- MINOR bump: backward-compatible additions

Bridge and Unity must have matching MAJOR version. Bridge should accept same or higher MINOR version from Unity.

## Responses during shutdown

Unity stops admitting new requests and notifications when transport disposal starts,
but permits completed responses to flush during the bounded 500 ms drain. Writer
closure is synchronized with response writes. After closure, late responses are dropped.

If cancellation occurs while a request is still waiting for Unity's writer lease,
the router returns `executionState: not_started`: no tool handler ran. This is not
an automatic retry instruction; wait for readiness and revalidate inputs before retrying.
Once dispatch has started, cancellation does not establish that no side effects occurred.
Disconnected mutations without a confirmed result remain non-replayable.

Source files are edited directly with filesystem tools. The MCP source-writing tool
has been removed; use `editor.ensure_compiled` after a coherent source-edit batch.

## Writer scheduling

The default idle writer reservation is zero. Active same-session calls remain protected; queued clients proceed in order once ownership expires. A timeout timer is cancelled when a queue signal wakes its waiter. Compact status exposes queue size and owner tool without exposing session identifiers.

### Cancellation during reload

After Unity announces reload, a cancellation reply (`-32006`) from the current
connection leaves a reload-safe request pending for normal disconnect/reconnect
recovery. Completed replies still finish immediately. Ordinary cancellation and
tools not declared reload-safe are not retried by this rule. Existing request
timeouts and explicit client cancellation remain in effect.
