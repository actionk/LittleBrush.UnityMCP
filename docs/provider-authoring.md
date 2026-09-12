# Authoring MCP Tools

A tool provider is any class that implements `LittleBrushGames.Mcp.IToolProvider`. Editor providers additionally carry the `[McpToolProvider]` attribute so the plugin's discovery scan instantiates them on editor load.

## Minimal example

```csharp
using System.Threading;
using System.Threading.Tasks;
using LittleBrushGames.Mcp;
using LittleBrushGames.Mcp.Editor;
using Newtonsoft.Json.Linq;

[McpToolProvider]
public sealed class HelloProvider : IToolProvider
{
    public string Namespace => "hello";

    public void RegisterTools(IToolRegistration reg)
    {
        reg.Register(new ToolDescriptor
        {
            Name = "hello.greet",
            Description = "Returns a friendly greeting.",
            InputSchema = new JObject { ["type"] = "object" },
            Availability = ToolAvailability.Either,
            Handler = Greet,
        });
    }

    private static ValueTask<ToolResult> Greet(ToolContext ctx, CancellationToken ct)
        => new(ToolResult.Ok(new JObject { ["greeting"] = "hi" }));
}
```

## Tool descriptor

| Field | Notes |
|---|---|
| `Name` | Fully qualified, namespaced (`scene.read`, `runtime.ai.plan`). Must be globally unique across all providers. |
| `Description` | Returned on demand by `unity.tools`. Keep it short and informative. |
| `InputSchema` | JSON Schema subset: `type`, `properties`, `required`, `enum`, `const`, numeric bounds including exclusive bounds, string/array bounds, `uniqueItems`, `items`, and `additionalProperties`. Returned by `unity.tools` and validated before the handler runs. Unsupported keywords reject that provider during registry rebuild instead of being silently ignored. |
| `OutputSchema` | Optional JSON Schema returned with the full on-demand descriptor. |
| `Annotations` | Optional MCP annotations such as `{ ["readOnlyHint"] = true }`. |
| `Availability` | Flags: `EditMode` / `PlayMode` / `Either`. `unity.tools` reports live availability and the dispatcher enforces it on every call. |
| `Execution` | `Sync` (default), `Async`, or `LongRunning`. Affects timeout and cancellation defaults. |
| `RequiresMainThread` | Defaults to `true`. The dispatcher hops to the main thread before invoking the handler. |
| `Handler` | `async ValueTask<ToolResult>` with cancellation token. Throw `McpToolException(code, message, data?)` for expected failures. |

## Error handling

Throw `McpToolException(code, message, data?)` with codes from `McpErrorCodes` (`NotFound`, `InvalidParams`, `Conflict`, etc.) for expected, controlled rejections. The MCP response still contains the error code, but the Unity Console records transient rejections as informational and other expected rejections as warnings. `ToolResult.Error(message)` and `ToolResult.ErrorWithData(message, structured)` are also expected failures by default. Pass `expectedFailure: false` only when a handler deliberately catches an unexpected host or invariant exception and returns a diagnostic envelope. Otherwise let unexpected exceptions bubble to the dispatcher so they remain visible as errors.

## Result shapes

`ToolResult.Ok(JObject structured, params ContentBlock[] content)` is the common case. The `structured` JObject is delivered as `structuredContent` in the MCP response, and the `content[]` blocks (Text/Image/ResourceLink) are delivered as the canonical MCP `content[]` array.

Structured JSON is not copied into a text block. A JSON-only success therefore has `content: []` plus one `structuredContent` object. Add content blocks only for actual text, media, or resource links; errors keep a short human-readable text message and may also carry structured diagnostics.

`ToolResult.Text(string)` is shorthand for a single text content block.

## Response budgets

- Registered descriptors are exposed on demand through `unity.tools`; only `unity.tools` and `unity.call` have a persistent client-context cost. Do not register aliases or superseded workflows because discovery results and maintenance still pay for them.
- Registration validates tool names, handlers, schemas, and positive timeouts. One invalid provider is skipped atomically, reported by `editor.status.registryErrors`, and does not remove tools from healthy providers.
- Any list or discovery result whose size can grow must default to a 25-item page, support `offset`, and report `total`, `returned`, `truncated`, and `nextOffset` when applicable.
- Large reads default to an `outline`, names, bindings, or descriptors. Full serialized values, keys, warning messages, stack traces, and component data must be explicit opt-ins.
- Scene and prefab hierarchy outlines default to at most 100 nodes; callers opt into larger `maxNodes` values when the truncated outline is insufficient.
- Prefer filters (`query`, type selectors, property paths) before detail expansion. Page nested values independently when one matched item can still be large.
- Bound individual free-form strings as well as collection counts. Return the original character count and an explicit truncation marker so callers can request a larger targeted cap when necessary.
- Structured document readers should default to paged child summaries and support a stable selector (for example JSON Pointer) before returning full values. Reject an explicitly requested page that still exceeds its declared character budget instead of flooding client context.
- Polling tools return progress summaries while running and bounded failure/detail pages when complete.
- Do not emit null or empty stack traces, output, or diagnostic fields.
- Do not duplicate structured JSON into `content`; add content blocks only for text or media the client must render.
- Keep tool descriptions short; input schemas own argument documentation.

## Progress and cancellation

Clients may attach `_meta.progressToken` to `tools/call`; the dispatcher forwards it to `ToolContext.Progress`. Long-running handlers should report meaningful milestones and honor the supplied cancellation token. The router keys cancellations by client session and JSON-RPC ID type, so providers must not implement their own global request map.

## Runtime providers

Runtime providers are instantiated and owned by the consumer project (game runtime), then installed via:

```csharp
McpRuntimeBridge.Install(providers, services);
```

Hold onto the returned `IDisposable` until shutdown. The dispatcher's `ToolContext.RuntimeServices` exposes the `IServiceProvider` you passed to `Install`.

## Main thread

Handlers run on the Unity main thread by default — `RequiresMainThread` defaults to `true`. Safe to touch `EditorApplication`, `AssetDatabase`, scene APIs, GameObjects, etc.

**Only set `RequiresMainThread = false` when the entire handler is provably thread-safe** — for example a tool that reads a `ConcurrentQueue`, a static thread-safe ring buffer, or a `volatile` field. Touching almost any Unity API from a background thread will raise `UnityException: ... can only be called from the main thread`. The HTTP listener thread is not the main thread.

Bridge state shared across threads (editor isPlaying / isCompiling) is cached on the main-thread tick by `McpBridgeHost` and exposed to the dispatcher via a `Func<(bool isPlaying, bool isCompiling)>` probe — never read those properties directly from a tool handler unless it is hopping through the main thread pump.
