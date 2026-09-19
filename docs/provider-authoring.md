# Authoring MCP Tools

Editor providers carry `[McpToolProvider]` so discovery instantiates them on load. For ordinary typed methods, inherit `AttributedToolProvider` and add `[McpTool]`; it implements the existing `IToolProvider` contract. Manual `IToolProvider.RegisterTools` remains available for complex schemas and lifecycle hooks. Both routes produce the same `ToolDescriptor` and use the same dispatcher, trust checks, queues and gateway.

## Non-disruptive automation

Give agents the capabilities needed to complete their work while minimizing disruption to the user. Keep operations callable independently of their editor UI; opening a window must not be a prerequisite for inspecting, generating, editing, or rendering data when the operation itself does not require that UI.

- Default to background operations that preserve the user's windows, focus, selection, camera framing, loaded scenes, and Play Mode. Make intentional presentation or navigation an explicit operation or opt-in, and describe its effects in the tool descriptor.
- Return images directly from isolated renderers for visual output checks. Reuse the same generation and rendering code as the human-facing tool, with explicit inputs and deterministic camera controls. Inspect an actual window only when its UI or current interactive state is the subject of the task.
- Keep expensive generation responsive through staged work and cancellation. Dispose temporary scenes, cameras, textures, and other owned resources on success, failure, and cancellation; do not save preview assets or prepare materials implicitly.
- Tests of data or rendering should use the underlying APIs. Show a window only for behavior that genuinely requires a displayed UI, and close only the window created by that test in guaranteed cleanup.
- Verify both the result and preservation of user-owned Editor state. Quiet operation must not reduce capability, hide failures, or bypass trust and consent rules.

## Attribute-based registration

```csharp
using LittleBrushGames.Mcp;
using LittleBrushGames.Mcp.Editor;
using Newtonsoft.Json.Linq;

[McpToolProvider]
public sealed class HelloProvider : AttributedToolProvider
{
    public override string Namespace => "hello";

    [McpTool("hello.greet", "Return a greeting.", ToolTrustCategory.Read,
        Availability = ToolAvailability.Either,
        RequiresMainThread = false, RequiresWriterLease = false)]
    private static JObject Greet([McpParameter(MaxLength = 80)] string name = "World")
        => new() { ["greeting"] = "Hello, " + name };
}
```

Only explicitly attributed methods are exported; their ordinary C# visibility does not grant MCP
access. Discovery and method/parameter reflection happen during registration, not on each request.
Invocation uses the cached method metadata. Names and parameter names are the external contract;
rename them intentionally with their callers. Duplicate names and unsupported signatures reject the
whole provider through the existing registry diagnostics, leaving healthy providers available.

- Specify a tool name, description and explicit `ToolTrustCategory` (`Auto` is rejected). Availability
  defaults to Edit Mode, main-thread execution and writer ownership remain enabled, and reload replay
  is disabled by default. Opt out only where the operation's contract permits it.
- Parameters support `string`, `bool`, `int`, `long`, `float`, `double`, ordinary named enums,
  nullable value types and one-dimensional arrays. Non-finite numbers and conversion overflow are
  rejected. Flags enums, Unity object/component arguments, dictionaries, arbitrary DTOs, generics,
  ref/out parameters and other complex contracts use manual registration in this first version.
- C# optional defaults make parameters optional. Explicit null is accepted for nullable types and
  parameters whose default is null. Unknown JSON properties and coercions such as strings to numbers
  are rejected. `ToolContext` and `CancellationToken` are injected and never exposed as user arguments.
- `[McpParameter]` supplies descriptions, length/item limits and numeric bounds. Strings default to
  at most 2,000 characters and arrays to 1,000 items; override for a documented bounded use case.
  Contradictory limits, constraints on the wrong type and invalid defaults reject registration.
- Return `JObject` or `ToolResult`, synchronously or as `Task<T>` / `ValueTask<T>`. Async execution
  metadata is inferred. Build compact results explicitly; arbitrary object graphs are not serialized
  automatically. Original exceptions and cancellation propagate to the normal dispatcher.
- Attributes describe access and signatures; methods still own validation, cancellation, Undo,
  transactions, preservation of user-owned state and result semantics. Destructive hints default to
  true for non-read operations; explicitly declare a non-destructive write when appropriate.

`FeatureRequestProvider` is the first migrated provider. Its existing tool name, full input schema,
policy and storage behavior are preserved. Subsequent migrations should compare old/new descriptors
and exercise the existing behavior before removing manual registration. Do not expose arbitrary
class/method invocation or generate unbounded discovery responses.

## Manual registration

Use `IToolProvider.RegisterTools` and `registration.Register(new ToolDescriptor { ... })` for
contracts needing custom object schemas, dynamic trust resolution, background-operation probes or
other descriptor behavior not represented by the attributes. Attribute and manual providers coexist;
no second gateway, compatibility alias or global migration is required.

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
| `RequiresWriterLease` | Defaults to `true`. Set `false` only for read-only tools that can safely observe cached or stable state without reserving the cross-client writer. Reported by `unity.tools`. |
| `ReloadSafe` | Allows the bridge to replay an interrupted call after domain reload only when the handler is idempotent or concurrency-keyed. Replay state is exposed as `ToolContext.IsReplay`, not as a public schema property. |
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
