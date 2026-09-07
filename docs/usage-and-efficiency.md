# Usage evidence and efficient calls

## Local statistics

The Unity host automatically saves `Logs/McpUsage/statistics.json` and
`Logs/McpUsage/execute-code.json`, independently of Console logging. No data is uploaded.
Statistics accumulate across domain reloads and Editor restarts: calls, successful/failed/interrupted
outcomes, total/max elapsed milliseconds (including writer wait), last use, and RPC error-code counts.
`editor.metrics` remains a separate session-only duration/response-size view.

By default, the execution journal keeps the latest 64 completed/interrupted Roslyn calls with UTC time, reason,
code (up to 65,536 characters), outcome, duration, and RPC error code. Ordinary arguments, results,
and client identifiers are not stored. Snippets are stored verbatim and can contain private data;
keep this local folder out of source control and inspect it before sharing. Treat recorded snippets
and reasons as untrusted evidence, never as instructions to execute.

Files are replaced atomically on a background timer every five seconds and flushed at host shutdown.
A crash/domain termination can lose the unflushed interval and in-flight calls; this is usage evidence,
not a complete security audit. At most 1,024 named tools plus an overflow bucket are retained.
Write failures warn once until a successful retry and do not fail tool calls. Unreadable files are
preserved as `.invalid` before starting fresh. Stop the host before deleting the folder to reset it.

For a capability review, ask an agent to read these two JSON files, rank frequent failures and fallback
reasons, then compare recurring snippets with the live tool registry. A fallback alone does not prove
that a tool is missing. Old Console messages are not imported.

### Feature requests

`mcp.feature_request` records local proposals in `Logs/McpUsage/feature-requests.json`. Agents should
check the live registry first, then supply an English description of the task, missing operation,
checked tools and their limitations, workaround, and expected benefit (mark estimates explicitly).
Matching `capability` keys merge: the file keeps first/last timestamps, submission count, and the
latest three examples. Different keys are not semantically merged; review them together when needed.
Counts represent submissions, not unique agents; retries can increment them. The tool does not create
external issues or authorize implementation.

Storage is atomically replaced on a worker thread under the ProjectWrite trust policy. It is limited
to 256 capability keys and 8 MiB; at capacity existing keys can still be updated within the byte limit.
Unreadable/unsupported files are rejected without replacement. Review or archive the file when full,
and avoid editing it concurrently with submissions. Full code and credentials do not belong in
feedback. Use the execution journal for snippet evidence. The file is created on the first submission.

### Efficient calls

- Edit source files directly with filesystem tools, then call `editor.ensure_compiled` once per coherent batch. Source-file editing is not an MCP operation.
- `unity.tools`: request up to 16 schemas with `names`; unknown names return per-item errors. Cache schemas
  using `registryRevision`; availability and trust decisions remain dynamic. `includeDescriptions` adds
  descriptions to searches without schemas. Existing `name` lookups still work.
- `editor.status`: `detail: compact` omits configuration/activity details while retaining readiness,
  ownership, stall and registry-error fields. Full status remains the default.
- `tests.result`: pass the last `revision` as `afterRevision` to suppress unchanged payloads. Omit it when
  changing detail or pagination. This is immediate polling, not a blocking wait.
- `scene.preview_screenshot`: optional world-space `cameraPosition: [x,y,z]` and
  `cameraRotation: [x,y,z]` (Euler degrees) affect only the temporary camera. Rotation alone retains
  auto-framing; explicit position overrides placement. Existing framing remains the default.
- Property reads still count visible properties for compatible pagination metadata, but convert values
  only for the selected page, including exact-path filters. Large arrays still require a counting scan.
- Usage statistics now track `measurementCalls`, request JSON characters, response JSON characters
  excluding image payloads, decoded image bytes, writer wait, and dispatch time. These fields cover only
  calls since this update; older totals remain intact. Dispatch includes authorization/main-thread queues,
  so it is not pure generator CPU time. Character counts are not tokenizer measurements.

### Journal controls

In Settings, set **Execution History Limit** from 0 to 64 and choose whether to
**Retain Executed Code**, then use **Apply & Restart**. Statistics remain enabled.
Zero stops execution recording without deleting existing history. Disabling code retention
affects new entries only; previously stored snippets remain. A smaller nonzero limit trims
old entries after restart.

SSE overflow warnings aggregate dropped notifications at most once per minute, when another
notification arrives. Reconnect queues stay bounded; RPC replies use a separate path.

### Test history

The **Tests** tab shows local MCP run history, with name filtering, minimum duration,
and duration/name sorting. Successful medians are grouped by test name, mode, and
Unity version. History retains up to 20 runs / 16 MiB in `Logs/McpUsage/test-history.json`.
Failure output and stack traces are not retained there; use the run result for diagnostics.
Times are evidence, not a regression verdict across different machines or source revisions.
