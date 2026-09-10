# Unity MCP Workflows

Confirm the intended project with `editor.status`. Fetch each target's live schema with `unity.tools`, then call it through `unity.call`; names below are registry targets.

## Readiness

1. Call `editor.status` and verify `projectPath` before interpreting project state or available providers.
2. If `isCompiling` or `isUpdating` is true, use `editor.wait_ready` when available.
3. If `mainThreadStalledMs` is high, wait before calling main-thread tools.
4. If Play Mode is user-owned, use allowed inspection/preview tools and defer incompatible edits, imports, compilation, or tests.

## Scene object

1. Find candidates with `scene.find`; prefer an explicit `scenePath` for background work.
2. Read the exact object and obtain the scene's `assetHash` from `scene.read`.
3. Apply one coherent `scene.write` batch with `expectedHash` for every existing scene. Resolve stale hashes or dirty-scene conflicts without bypassing them.
4. Unloaded-scene writes require `save: true` and restore the previous active scene after the call. Read back the result. New scenes use explicit `scenePath`, `createIfMissing: true`, and `save: true`.

## Scene switching

1. Prefer background operations with explicit `scenePath`. Only when a scene must remain loaded or active, call `scene.list` to capture the current set and dirty flags.
2. If the requested set is already satisfied, stop; `scene.request_open` also returns an `already_open` no-op without a notification.
3. Call `scene.request_open` with the target `path` and a concise `reason`. It only supports replacing the loaded scene set with the target scene. The local `EditorState` policy decides whether switching is denied, prompted, or allowed; dirty replacement also uses `UnsavedWork`.
4. If switching is blocked by dirty scenes, do not save, close, or discard them. Report the conflict and ask the user to resolve it, then retry.
5. Re-read `scene.list` and verify the returned scene set.

## Prefab

1. Inspect with `prefab.read`.
2. Write a coherent batch with `prefab.write`.
3. Read back or diff the result.
4. Capture `prefab.preview_screenshot` or the appropriate installed UI preview tool.

Do not delete a prefab root through `prefab.write`; use `asset.delete` only when deletion was explicitly requested.

## Script edit

1. Edit one coherent source batch directly with filesystem tools; do not route file edits through code execution.
2. Call `editor.ensure_compiled` once without `force`; expect a domain reload to briefly disconnect the Editor.
3. Fix exact compiler errors before attaching or using new components.

## Serialized ID replacement

1. Choose `scene.replace_id` for one loaded or unloaded scene, or `prefab.replace_id` for one prefab.
2. Call it with the explicit asset, `oldId`, `newId`, `dryRun: true`, and `save: false`.
3. Inspect `matchCount`, match kinds, bounded `matches`, and `truncated`.
4. Repeat with `dryRun: false` and `save: true`. Every existing scene also requires the preview's `assetHash` as `expectedHash`; then read back when useful.

## Imported animation and Timeline

1. Resolve imported sub-assets with `asset.subassets.list`.
2. Use the returned `reference` for scene/prefab property assignment.
3. Extract an imported AnimationClip with `asset.animation_clip.extract` when a writable standalone `.anim` is required.
4. Read clip curves with `asset.animation_clip.curves.read`; write only standalone clips.
5. Read Timeline state with `timeline.read`; pass its hash to `timeline.write` and verify the new duration/timing.

## Addressables and assets

1. Confirm entries with `addressables.find`.
2. Register or move with `addressables.add`; change addresses with `addressables.set_address`.
3. Remove entries only when requested.
4. Use `asset.find`, `project.assets.read`, `asset.copy`, `asset.move`, `asset.delete`, `asset.import`, and GUID/path conversion tools for AssetDatabase work.

## Tests

1. Ensure the Editor is ready and not in user-owned Play Mode.
2. When the runner may replace scene state, let `tests.run` apply `UnsavedWork` to dirty saved scenes. Untitled scenes remain refused until the user chooses a path.
3. Fetch the live `tests.run` schema and run the narrowest known names, group, or assembly with a unique `runId` (32 lowercase hex characters). The local `Tests` trust policy determines whether the run is denied, prompted, or allowed.
4. Poll `tests.result` until completion and scene restoration. Recover a lost launch response using the same ID; do not launch another run. Follow the returned polling hint.
5. Report failing tests separately from tool, bridge, ownership, or main-thread errors.

## Visual check

- Scene object: `scene.preview_screenshot` in isolation or context mode.
- UI or 3D prefab: `prefab.preview_screenshot`.
- Runtime UI: discover a compatible preview provider; do not assume one is installed.
- UI Toolkit: `ui.preview_screenshot`.
- Editor window/Inspector: `editor.window_screenshot` with `activate: false` and `openIfNeeded: false`.

Capture once after the state is stable. Do not alternate compile and screenshot calls while iterating.

Use `editor.screenshot` only for an already visible view. Do not enter Play Mode solely for a static screenshot.

## Last-resort code execution

Use `editor.execute_code` only after the parent skill's fallback disclosure; the local `CodeExecution` policy decides whether the call is denied, prompted, or allowed. It is unsandboxed and its request timeout cannot interrupt user code after invocation. Use returned compilation/runtime diagnostics instead of retrying blindly.
