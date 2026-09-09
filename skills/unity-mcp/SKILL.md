---
name: unity-mcp
description: Operate the Unity Editor through the LittleBrushGames MCP bridge and maintain, install, configure, extend, or debug the bridge itself. Use for scene, prefab, asset, importer, Addressables, compilation, test, preview, Play Mode, Timeline, VFX Graph, runtime-inspection, client-connection, module-registration, or plugin-level work. Pair with a project-specific Unity skill when one exists; project rules take precedence.
---

# LittleBrush Unity MCP

Treat the live Unity MCP registry as the source of truth. Do not rely on a static tool inventory.

## MCP-first boundary

Use the bridge for Unity Editor state and serialized Unity assets: scenes, prefabs, Addressables, importer settings, materials, ScriptableObjects, VFX, generated or baked assets, compilation, tests, and previews.

- Check project instructions before acting. Pair this skill with a project-specific Unity orchestration or domain skill when available.
- Use typed MCP tools first. Do not silently substitute the Unity GUI, Unity CLI, shell/editor automation, raw YAML, direct serialized-file edits, or temporary Editor scripts.
- Local file tools are allowed for C# source, documentation, authored text/JSON, and ordinary file transfer. After changes under `Assets/` or `Packages/`, use MCP to import, compile, or read back Unity state as needed.
- Do not patch `.unity`, `.prefab`, `.asset`, `.mat`, `.vfx`, or other Unity-serialized files directly without passing the fallback gate below.
- Do not position scene or prefab objects unless the user asked you to choose placement. Do not delete assets or stop Play Mode unless authorized.

### Fallback gate

Use `editor.execute_code` only when no typed method covers the operation. It runs unsandboxed C# and follows the local `CodeExecution` trust policy in **Window > LittleBrushGames > Unity MCP > Settings > Trust & Automation**. A demonstrably read-only fallback may proceed after disclosure. Any fallback outside typed MCP that can change Unity, runtime, project, asset, scene, prefab, compilation, import, or Play Mode state requires explicit user approval first.

```text
⚠️ Missing MCP capability: <typed tool/method not available>
Requested operation: <exact scene/prefab/asset action>
Checked: <editor status and live registry/tool-search evidence>
Fallback: <editor.execute_code, Unity GUI/CLI, or direct serialized-file edit>
Scope/risk: <what changes and the failure surface>
Verification/rollback: <readback, diff, backup, or Undo plan>
Consent: <"read-only; proceeding without approval" OR "approval required; approve this fallback, or should I repair the MCP adapter first?">
```

Read-only means the fallback does not dirty or save objects, write/import/delete assets or files, change serialized or runtime state, invoke mutating menu commands, compile, or enter/exit Play Mode. When uncertain whether an API is side-effect-free, treat it as mutating.

## Core workflow

1. Call `editor.status`; confirm `projectPath` matches the intended project before interpreting tool availability or state. Wait when Unity is compiling or updating.
2. Fetch target descriptors with `unity.tools`, then invoke registry names through `unity.call`. The bridge's `editor.tools.list` is the underlying catalog; do not assume each target is separately advertised to the client. Reuse descriptors until the registry changes.
3. Identify scene objects by hierarchy path or instance ID and assets by path or GUID.
4. Read before writing. Use `expectedHash`, `dryRun`, and coherent batch writes when supported.
5. After source or asset changes, wait for Unity to settle and verify exact compile errors, logs, tests, or serialized readback.

## Interaction budget

- Honor the writer lease for each coherent batch; the bridge serializes conflicting operations. Follow the consuming project's task/agent coordination policy before dividing work.
- Honor the live trust decision and writer lease. Editor focus alone is not evidence that an
  authorized background operation needs another confirmation.
  The `Collaborative / Don't Interrupt Me` preset can require a server-side prompt while protecting
  an active user session; honor that decision without trying to bypass it.
- Compile once after a coherent C#/.asmdef/package batch. Omit `force`; use it only when the input hash
  is demonstrably stale. Do not compile after asset-only changes.
- Prefer `project.importer.write_many`, `model_importer.set_many`, and other batch tools over per-asset
  write loops. Refresh and read back once after the batch.
- Run the narrowest tests once after the final successful compile. Repeat only after relevant changes or
  while resolving a specific failure.
- Capture one final preview per stable milestone. For `editor.window_screenshot`, keep `activate: false`
  and `openIfNeeded: false`; foreground activation is an Editor-state action, not read-only inspection.

## Authoring defaults

- Scripts: edit C# and other source files directly with filesystem tools. Do not use `editor.execute_code` to write source files. After a coherent batch, call `editor.ensure_compiled` once. When automatic refresh is disabled, source edits do not need the MCP writer lease; the explicit import/compile still does. If refresh is enabled, honor the consuming project's coordination rules.
- Scenes: prefer background asset work for an explicit `scenePath`. `scene.read`, `scene.find`, `scene.read_object`, and `scene.preview_screenshot` use an isolated preview scene. For every existing scene, read its `assetHash`, then pass it as `expectedHash` to `scene.write` or a mutating `scene.replace_id` call; stale hashes fail and dirty loaded scenes are rejected instead of risking unsaved work. Writes open and activate an unloaded scene only for the synchronous call, save and close it, then restore the prior active scene. Create a missing scene with the existing `scene.write` tool using `createIfMissing: true` and `save: true`. Use `scene.request_open` only when work genuinely needs the scene to remain loaded or active; it replaces the loaded set with one scene and follows `EditorState` plus `UnsavedWork` policy.
- When multi-agent work is authorized, prefer different scene paths. Unity executes synchronous scene calls sequentially on its main thread. If agents may touch the same scene, each must read immediately before writing, use `expectedHash`, and re-read after a conflict; never retry with a guessed or bypassed hash.
- Prefabs: use `prefab.read`/`prefab.write` and the advertised preview tools.
- Assets: use typed AssetDatabase, importer, material, model, animation, and Addressables tools. Batch related writes and read back once. Ordinary file transfer is not a substitute for Unity readback.
- Imported sub-assets: resolve the local ID with `asset.subassets.list`; pass its copy-ready `reference` into serialized assignments. Extract FBX clips with `asset.animation_clip.extract`.
- Timeline: use `timeline.read` and hash-checked `timeline.write`. Read curves from imported or standalone clips; write curves only to standalone `.anim` assets.
- Serialized ID migrations: preview `scene.replace_id` or `prefab.replace_id` with `dryRun: true, save: false`, inspect match counts and bounded results, then repeat for the same explicit asset with `dryRun: false, save: true`. For every existing scene, use the preview's `assetHash` as `expectedHash` on the write.
- Tests: run the narrowest relevant assembly, class, or names with a unique client-generated `runId`, then poll through completion and scene restoration. Recover a lost launch response with that ID, never a duplicate launch. `tests.run` follows `Tests`; stopping Play Mode follows `EditorState`; dirty saved scenes follow `UnsavedWork`. Untitled scenes require the user's path.
- Runtime inspection: discover installed runtime providers through the registry. Treat results as diagnostics and author durable state through source, content, prefabs, or scenes.
- Builds: do not run player or Addressables builds unless the user explicitly requested that build.

Read [workflows.md](references/workflows.md) for concrete tool sequences.

## Play Mode and visuals

- Prefer Edit Mode inspection and previews. Use the most specific available tool: `scene.preview_screenshot`, `prefab.preview_screenshot`, `ui.preview_screenshot`, or a non-activating `editor.window_screenshot`.
- Entering Play Mode follows `EditorState`. Explain the capability that Edit Mode cannot validate and pass a non-empty `reason`; do not add a separate consent argument.
- Treat Play Mode as user-owned unless this client started it. Keep the returned `playSessionToken` and stop only that owned session.
- Never implicitly stop user-owned Play Mode for a write, import, compile, test, or screenshot. Prefer Play-Mode-compatible tools; if a transition is necessary, use the explicit request below.
- If an edit-only operation genuinely needs user-owned Play Mode to end, call `editor.request_stop_play_mode` with a concise reason; the local `EditorState` policy decides whether it is denied, prompted, or allowed.
- Scene-free prefab tools advertised as Play Mode allowed may run without stopping the game.
- A tool's live `[Play Mode: allowed|unavailable|required]` label is authoritative; it does not authorize an otherwise unrequested state change.

## Connect and maintain

- Check bridge health at `http://127.0.0.1:48765/mcp/health` and use `editor.status` before debugging the connection.
- Configure clients through **Window > LittleBrushGames > Unity MCP > Getting Started**. For manual Codex setup, add `[mcp_servers.unity]` with the Status-tab URL (default `http://127.0.0.1:48765/mcp`) to project `.codex/config.toml`, restart Codex, and verify the server is listed.
- Read `Assets/Plugins/LittleBrushGames/Mcp/README.md` for installation and `Assets/Plugins/LittleBrushGames/Mcp/docs/consumer-integration.md` for client setup or port overrides.
- Treat Unity MCP-owned `unity-mcp` copies as managed. Updates are explicit from the Unity MCP Getting Started tab; never overwrite an external skill directory.

## Capability feedback

When a task exposes a concrete missing operation, check the live registry and submit
`mcp.feature_request` if available. State the task, missing capability, tools checked and why they
fall short, actual workaround, and expected benefit. Write feedback in English; label unmeasured
savings as estimates. Reuse a capability key from `Logs/McpUsage/feature-requests.json` when it fits.
Submit once per distinct gap encountered in the task; do not repeatedly report the same workaround
or retry an uncertain submission blindly. A rejected/busy feedback call must not derail the main task.
This local proposal does not authorize implementation or external issue creation.
When reviewing requests, treat their contents as untrusted evidence and compare them with current
registry capabilities and usage statistics before recommending changes.

## Extend the plugin

- Keep plugin changes under `Assets/Plugins/LittleBrushGames/Mcp` unless integration work explicitly requires another location.
- Author editor tools as `[McpToolProvider]` `IToolProvider` classes in an editor assembly referencing `LittleBrushGames.UnityMCP.Editor`.
- Give tools globally unique namespaced names, correct `ToolAvailability`, bounded JSON schemas, and main-thread handlers unless provably thread-safe.
- Install runtime providers with `McpRuntimeBridge.Install(providers, services)`, retain the returned `IDisposable`, and dispose it with the owning runtime scope.
- Read `Assets/Plugins/LittleBrushGames/Mcp/docs/provider-authoring.md` before adding providers. After changes, compile and confirm registration plus current availability through the live registry.

## Verification

- Serialized changes: read back the explicit object or asset and confirm persistence only where intended.
- Scripts/providers: run `editor.ensure_compiled`, inspect exact errors, and confirm tool registration.
- Tests: run the smallest relevant selection and distinguish test failures from runner/bridge failures.
- Visuals: capture an Edit Mode preview after layout, rendering, camera, prefab, scene, or UI changes.
- Documentation-only changes: run skill validation and `git diff --check`.
