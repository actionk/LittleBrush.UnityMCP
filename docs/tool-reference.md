# Tool reference

Source inventory of 104 registry tools, plus the two MCP gateway tools. Optional modules require their dependencies. The live `unity.tools` response is authoritative for schemas, availability, and trust decisions.

- `unity.tools`: discover tools and fetch exact schemas, individually or in batches.
- `unity.call`: invoke a discovered tool with its validated arguments.

## Core

| Tool | Purpose |
| --- | --- |
| [`asset.animation_clip.extract`](../Editor/Providers/AssetProvider.cs) | Copy an imported AnimationClip sub-asset (for example from an FBX) into a standalone project-owned .anim asset. |
| [`asset.copy`](../Editor/Providers/AssetProvider.cs) | Copy an asset via AssetDatabase.CopyAsset. Safer than Instantiate+SaveAsPrefabAsset — does not touch the scene. Compilation inputs require Edit Mode. |
| [`asset.create_folder`](../Editor/Providers/AssetProvider.cs) | Create a folder under Assets/ or Packages/ if it does not already exist. Idempotent. |
| [`asset.delete`](../Editor/Providers/AssetProvider.cs) | Delete one or more assets. Returns paths that were deleted and those that did not exist. Compilation inputs require Edit Mode. |
| [`asset.find`](../Editor/Providers/AssetProvider.cs) | Find assets by type, optional folder scope, and optional label. Returns path + guid + type. Paginated via cursor. Preferred over execute_code+AssetDatabase.FindAssets. |
| [`asset.import`](../Editor/Providers/AssetProvider.cs) | Reimport one or more assets in a single StartAssetEditing/StopAssetEditing envelope so the cost is amortised. Compilation inputs require Edit Mode. |
| [`asset.move`](../Editor/Providers/AssetProvider.cs) | Move or rename an asset via AssetDatabase.MoveAsset. Compilation inputs require Edit Mode. |
| [`asset.subassets.list`](../Editor/Providers/AssetProvider.cs) | Page the main asset and imported sub-assets at a path. Use localId with the source path for exact follow-up operations. |
| [`build.player_settings.read`](../Editor/Providers/BuildProvider.cs) | Read common PlayerSettings values with a concurrency hash. |
| [`build.player_settings.write`](../Editor/Providers/BuildProvider.cs) | Write common PlayerSettings values with expectedHash and dryRun validation. |
| [`build.result`](../Editor/Providers/BuildProvider.cs) | Poll a queued Unity player build by runId. |
| [`build.settings`](../Editor/Providers/BuildProvider.cs) | Read active build target and enabled build scenes. |
| [`build.start`](../Editor/Providers/BuildProvider.cs) | Queue a Unity player build and return a runId before the blocking build begins. Governed by the local Builds trust policy; poll build.result. |
| [`content.read`](../Editor/Providers/ContentProvider.cs) | Read one source JSON file through a compact, paged projection with a concurrency hash. Defaults to child summaries; use pointer plus detail=full for targeted values. |
| [`content.reindex`](../Editor/Providers/ContentProvider.cs) | Rebuild JsonContentManager's index and wait until the Editor reloads it. |
| [`content.reload`](../Editor/Providers/ContentProvider.cs) | Reload JsonContentManager's current index and content in the Editor. |
| [`content.status`](../Editor/Providers/ContentProvider.cs) | Report JsonContentManager availability, load state, and source JSON file count. |
| [`content.write`](../Editor/Providers/ContentProvider.cs) | Create or replace one source content JSON file with expectedHash concurrency, dryRun, generated-file protection, and automatic reindex scheduling. |
| [`editor.compile.errors`](../Editor/Providers/EditorStatusProvider.cs) | Read the last compile result. Defaults to paged errors; warnings are counted but returned only with detail=all. |
| [`editor.ensure_compiled`](../Editor/Providers/EditorStatusProvider.cs) | Compile once after a coherent C#/.asmdef/package edit batch and return exact errors. Omit force so unchanged script inputs reuse the cached result; asset-only changes do not need compilation. Reload-safe across domain reloads. |
| [`editor.execute_code`](../Editor/Providers/ExecuteCodeProvider.cs) | Unsandboxed last-resort Roslyn snippet runner governed by the local CodeExecution trust policy. Prefer typed tools whenever they cover the operation. Code runs with Unity's full user permissions; the request timeout cannot interrupt user code after invocation. Requires a reason documenting why no typed tool fits. |
| [`editor.logs.clear`](../Editor/Providers/LogsProvider.cs) | Clear the log ring buffer. |
| [`editor.logs.tail`](../Editor/Providers/LogsProvider.cs) | Return the last N buffered log entries with optional substring and level filters. |
| [`editor.metrics`](../Editor/Providers/EditorStatusProvider.cs) | Page session-local duration, JSON text character counts excluding images, and decoded image bytes. Use query to inspect suspected high-context tools. |
| [`editor.pause`](../Editor/Providers/EditorStatusProvider.cs) | Pause the editor. |
| [`editor.play`](../Editor/Providers/EditorStatusProvider.cs) | Enter Play Mode under the local EditorState trust policy. Returns a playSessionToken; only that token can stop the MCP-started session. |
| [`editor.refresh`](../Editor/Providers/EditorStatusProvider.cs) | Force AssetDatabase import to detect external file changes. Prefer editor.ensure_compiled when you also need compile errors. |
| [`editor.request_stop_play_mode`](../Editor/Providers/EditorStatusProvider.cs) | Request stopping user-owned Play Mode under the separate local UserPlayModeStop trust policy. |
| [`editor.resume`](../Editor/Providers/EditorStatusProvider.cs) | Resume the editor. |
| [`editor.screenshot`](../Editor/Providers/ScreenshotProvider.cs) | Capture a screenshot of the SceneView (edit/play) or GameView (play mode). Returns inline PNG via ImageContent. Args: { source?: 'SceneView'\|'GameView', width?: int, height?: int }. |
| [`editor.selection.read`](../Editor/Providers/EditorInspectionProvider.cs) | Read a bounded page of selected object identities without changing selection or focus. |
| [`editor.selection.set`](../Editor/Providers/EditorInspectionProvider.cs) | Select existing objects by session-local IDs after whole-batch validation; never focuses or pings a window. |
| [`editor.windows.list`](../Editor/Providers/EditorInspectionProvider.cs) | List open Editor windows, their identities, titles and bounds; does not open or focus windows. |
| [`editor.scene_view.read`](../Editor/Providers/EditorInspectionProvider.cs) | Read bounded Scene view camera framing without moving the camera or acquiring writer ownership. |
| [`editor.status`](../Editor/Providers/EditorStatusProvider.cs) | Compact readiness, project identity, worker capabilities, Play Mode ownership, writer state, and registry errors by default. Request detail=full for configuration, activity, scene and compile-history details. |
| [`editor.stop`](../Editor/Providers/EditorStatusProvider.cs) | Exit only a Play Mode session started by editor.play. User-started Play Mode is protected. |
| [`editor.tools.list`](../Editor/Providers/EditorStatusProvider.cs) | Search and page the live Unity-side tool registry. Returns compact name and availability records unless includeMetadata=true. |
| [`editor.wait_ready`](../Editor/Providers/EditorStatusProvider.cs) | Wait until the Editor is ready, then return status plus a compact compile result. |
| [`editor.window_screenshot`](../Editor/Providers/ScreenshotProvider.cs) | Capture a visible Unity EditorWindow by type name or title. Captures the full window by default; region optionally crops in window-local logical coordinates from the top-left. Background capture reuses an already open window. Creating a temporary floating window requires activate=true and can steal focus. Returns inline PNG and logical/physical bounds. Args: { windowType?: string, title?: string, openIfNeeded?: bool, waitFrames?: int, floating?: bool, activate?: bool, width?: int, height?: int, region?: { x, y, width, height } }. |
| [`material.read`](../Editor/Providers/MaterialProvider.cs) | Read a material's shader, keywords, render queue, and paged shader properties with a concurrency hash. Properties default to 25; descriptions are opt-in. |
| [`material.write`](../Editor/Providers/MaterialProvider.cs) | Set material shader, keywords, renderQueue, and properties with expectedHash and dryRun validation. Texture values accept `assetPath` and optional `scale` / `offset` objects with x/y fields. |
| [`mcp.feature_request`](../Editor/Providers/FeatureRequestProvider.cs) | Submit local capability feedback after checking the live tool registry. Reuse the capability key to merge reports; counts are submissions, not distinct agents. Include the task, missing operation, checked tools and why they do not fit, workaround, and expected benefit (label unmeasured savings as estimates). This records a proposal, not approval to implement. Saved in Logs/McpUsage/feature-requests.json; no external service is contacted. |
| [`model_importer.clips.get`](../Editor/Providers/ModelImporterProvider.cs) | Read stable primitive ModelImporterClipAnimation settings and an optimistic asset hash. |
| [`model_importer.clips.set`](../Editor/Providers/ModelImporterProvider.cs) | Validate and set stable primitive ModelImporterClipAnimation settings by clip name. Requires expectedHash, supports dryRun and exact readback, and can opt into a scoped serialized .meta fallback when Unity rejects API persistence. |
| [`model_importer.clips.set_many`](../Editor/Providers/ModelImporterProvider.cs) | Batch validate and set stable primitive ModelImporterClipAnimation settings with per-asset expectedHash values, durable sequential reimports, and an optional scoped serialized .meta fallback. |
| [`model_importer.get`](../Editor/Providers/ModelImporterProvider.cs) | Read whitelisted ModelImporter properties for a single asset. |
| [`model_importer.set`](../Editor/Providers/ModelImporterProvider.cs) | Set one or more whitelisted ModelImporter properties. Reimports if any value changed. Whitelist: sourceAvatar, animationType, avatarSetup, importAnimation, importBlendShapes, importVisibility, importCameras, importLights, optimizeGameObjects, useFileScale, globalScale. |
| [`model_importer.set_many`](../Editor/Providers/ModelImporterProvider.cs) | Batch set whitelisted ModelImporter properties across many assets in a single StartAssetEditing/StopAssetEditing envelope. Replaces loops of GetAtPath+SaveAndReimport. |
| [`package_manager.add`](../Editor/Providers/PackageManagerProvider.cs) | Add or update a Unity package dependency through Package Manager. Accepts registry package identifiers (optionally with a version), Git URLs, and local package paths supported by Client.Add. |
| [`package_manager.remove`](../Editor/Providers/PackageManagerProvider.cs) | Remove a direct Unity package dependency through Package Manager using its package name. |
| [`prefab.add_nested_prefab`](../Editor/Providers/PrefabProvider.cs) | Add a source prefab as a real nested Prefab instance under a parent in an existing prefab asset. Preserves prefab linkage and applies optional local transform overrides. |
| [`prefab.create`](../Editor/Providers/PrefabProvider.cs) | Create a new prefab at dest from an existing source prefab. Uses scene-free LoadPrefabContents + SaveAsPrefabAsset — does not dirty the active scene. Modes: 'copy' creates a standalone prefab, 'variant' creates a prefab variant linked to src. |
| [`prefab.create_from_scene_object`](../Editor/Providers/PrefabProvider.cs) | Create a prefab from one GameObject in a loaded or project scene without dirtying or modifying the source scene or loaded scene set. Dry-run is the default; writes require confirm=true. Supports an optional wrapper root, __PivotVisual-style child name, exact relative child exclusions, transform reset, and explicit overwrite. |
| [`prefab.preview_screenshot`](../Editor/Providers/PrefabProvider.cs) | Render a UI or 3D prefab in isolation. UI diagnostics default to counts; use layoutDetail=warnings/full, layoutPath, and independent layoutNode/layoutWarning Offset/Limit pages for detail. JSON defaults to a 30,000-character budget, excluding the image. 3D prefabs copy gameplay camera settings and auto-frame renderer bounds. |
| [`prefab.read`](../Editor/Providers/PrefabProvider.cs) | Read a prefab hierarchy. Defaults to a sparse outline; request targeted component properties only when needed. |
| [`prefab.remove_unused_overrides`](../Editor/Providers/PrefabProvider.cs) | Inspect nested Prefab instances in a prefab asset and remove only Unity-reported unused overrides with PrefabUtility.RemoveUnusedOverrides. Dry-run is the safe default; pass dryRun=false to opt into an apply and save. |
| [`prefab.replace_id`](../Editor/Providers/PrefabProvider.cs) | Find exact serialized string values and authored object references matching an ID across a prefab hierarchy, replace them in scene-free prefab contents with Undo, and optionally save without reloading the active scene. |
| [`prefab.write`](../Editor/Providers/PrefabProvider.cs) | Atomically apply a batch of operations to a prefab asset (add_component, remove_component, set_property, create_gameobject, delete_gameobject, rename_gameobject, set_active, set_transform, reparent). Scene-free: if any operation fails, the prefab is not saved. Paths are relative to the prefab root. |
| [`project.assets.create`](../Editor/Providers/AssetsProvider.cs) | Create a ScriptableObject asset by concrete type and optionally set serialized properties. Supports dryRun. |
| [`project.assets.open`](../Editor/Providers/AssetsProvider.cs) | Select and ping an asset in the Project window. |
| [`project.assets.read`](../Editor/Providers/AssetsProvider.cs) | Return asset or selected sub-asset metadata and an optional filtered page of serialized properties. Pass `localId` from `asset.subassets.list` to select a sub-asset. Properties are omitted unless explicitly requested. |
| [`project.assets.write`](../Editor/Providers/AssetsProvider.cs) | Set serialized properties on an asset or selected sub-asset with optimistic concurrency. Pass `localId` from `project.assets.read` or `asset.subassets.list` to select a sub-asset. Requires `expectedHash` from `project.assets.read`; supports `dryRun`. |
| [`project.guid_to_path`](../Editor/Providers/ProjectProvider.cs) | Resolve a GUID to an asset path. |
| [`project.importer.read`](../Editor/Providers/AssetsProvider.cs) | Read a filtered page of serialized AssetImporter properties. |
| [`project.importer.write`](../Editor/Providers/AssetsProvider.cs) | Set one AssetImporter with expectedHash concurrency and dryRun validation; then reimport once. Use project.importer.write_many for multiple assets. |
| [`project.importer.write_many`](../Editor/Providers/AssetsProvider.cs) | Batch different serialized AssetImporter changes with per-asset expectedHash values in one StartAssetEditing/StopAssetEditing envelope. |
| [`project.path_to_guid`](../Editor/Providers/ProjectProvider.cs) | Resolve an asset path to a GUID. |
| [`project.prefab.instantiate`](../Editor/Providers/ProjectProvider.cs) | Instantiate a prefab into the active scene at optional world position. |
| [`project.settings.list`](../Editor/Providers/AssetsProvider.cs) | Page serialized Unity ProjectSettings assets (tags/layers, physics, graphics, quality, navigation, and more). |
| [`project.settings.read`](../Editor/Providers/AssetsProvider.cs) | Read a filtered page of serialized properties and a concurrency hash from one ProjectSettings asset. |
| [`project.settings.write`](../Editor/Providers/AssetsProvider.cs) | Set serialized ProjectSettings properties with expectedHash and dryRun validation. |
| [`scene.find`](../Editor/Providers/SceneProvider.cs) | Find GameObjects in a loaded or project scene by name, path, tag, or component type without changing the loaded scene set. Returns compact path + entity ID results. |
| [`scene.list`](../Editor/Providers/SceneProvider.cs) | List loaded scenes. |
| [`scene.preview_screenshot`](../Editor/Providers/ScenePreviewProvider.cs) | Edit Mode capture of a loaded or saved scene object. Supports gameplay-camera isolation/context and isolated neutral studio rendering. Studio accepts `ground` for a receiving plane and `excludePaths` for non-destructive cutaways. Never starts Play Mode. |
| [`scene.read`](../Editor/Providers/SceneProvider.cs) | Read a loaded or project scene hierarchy without changing the loaded scene set. Defaults to a sparse outline without components; request full profile or targeted component properties only when needed. |
| [`scene.read_object`](../Editor/Providers/SceneProvider.cs) | Read one GameObject subtree from a loaded or project scene without changing the loaded scene set. Defaults to a sparse outline with component descriptors; serialized properties are opt-in, filterable, and paged. |
| [`scene.replace_id`](../Editor/Providers/SceneProvider.cs) | Find exact serialized string values and authored object references matching an ID across a loaded or project scene. Existing scene writes require expectedHash from scene.read; dirty loaded scenes are rejected. Unloaded scene writes open additively for this call, save, and close. |
| [`scene.request_open`](../Editor/Providers/SceneProvider.cs) | Open a scene in Edit Mode under the local EditorState trust policy. Replacing dirty loaded scenes is governed separately by UnsavedWork. |
| [`scene.save`](../Editor/Providers/SceneProvider.cs) | Save the active or specified loaded scene. Transient unloaded-scene writes save within scene.write or scene.replace_id. |
| [`scene.write`](../Editor/Providers/SceneProvider.cs) | Transactional GameObject batch with expectedHash protection and background access to saved scenes. `create_gameobject` accepts an optional standard Unity `primitive`, `tag`, parent and local transforms. Other operations edit hierarchy, components, serialized properties and prefab instances. Dirty loaded scenes are rejected. |
| [`shader.inspect`](../Editor/Providers/MaterialProvider.cs) | Inspect a Shader asset's name plus paged properties and compiler messages. Each page defaults to 25; property descriptions are opt-in. |
| [`ui.preview_screenshot`](../Editor/Providers/UiPreviewProvider.cs) | Render a UI Toolkit UXML asset offscreen in Edit Mode and return an inline PNG. Uses Unity's VisualTreeAsset preview renderer first, then falls back to an offscreen PanelSettings target. Args: { uxmlPath: string, styleSheetPaths?: string[], panelSettingsPath?: string, width?: int, height?: int, waitFrames?: int }. |

## Addressables

| Tool | Purpose |
| --- | --- |
| [`addressables.add`](../Modules/Addressables/AddressablesProvider.cs) | Add or move assets into Addressables. Supports per-asset address, group, and labels. Args: { groupName?: string, labels?: string[], assets: [{ path, address?, groupName?, labels? }] }. |
| [`addressables.build`](../Modules/Addressables/AddressablesProvider.cs) | Build Addressables player content. Governed by the local Builds trust policy. |
| [`addressables.find`](../Modules/Addressables/AddressablesProvider.cs) | Find explicit Addressables entries by address text, label, path text, guid, or group name. At least one filter is required. |
| [`addressables.group_create`](../Modules/Addressables/AddressablesProvider.cs) | Create an Addressables group using the default group's schemas. Idempotent. |
| [`addressables.list`](../Modules/Addressables/AddressablesProvider.cs) | List Addressables groups; explicit entries are opt-in and bounded. |
| [`addressables.profile_set_active`](../Modules/Addressables/AddressablesProvider.cs) | Set the active Addressables profile by name. |
| [`addressables.profiles`](../Modules/Addressables/AddressablesProvider.cs) | Page Addressables profiles. Defaults to compact names; request detail=values for profile variables and values. |
| [`addressables.remove`](../Modules/Addressables/AddressablesProvider.cs) | Remove explicit Addressables entries by path or guid. Returns removed and missing targets. |
| [`addressables.set_address`](../Modules/Addressables/AddressablesProvider.cs) | Set the address for an explicit Addressables entry. Args: { address: string, path?: string, guid?: string }. |

## TestRunner

| Tool | Purpose |
| --- | --- |
| [`tests.cancel`](../Modules/TestRunner/TestRunnerProvider.cs) | Cancel a running test run by runId. |
| [`tests.list`](../Modules/TestRunner/TestRunnerProvider.cs) | Search and page test cases by mode, full-name substring, assembly, or category. |
| [`tests.result`](../Modules/TestRunner/TestRunnerProvider.cs) | Poll a test run without changing scenes. running, cancelling and restoring are nonterminal; completed means scene cleanup has finished. auto returns summary while pending and compact paged failures when finished. Request diagnostics explicitly for bounded stack traces and output. inputEvidence records source fingerprints and whether inputs changed during the run; case success alone does not verify changed inputs. |
| [`tests.run`](../Modules/TestRunner/TestRunnerProvider.cs) | Run tests matching an optional filter under the Tests trust policy. Stopping user-owned Play Mode separately follows UserPlayModeStop. Dirty saved scenes are saved under the UnsavedWork policy; untitled scenes remain blocked. EditMode tests run in a temporary empty scene. Supply a unique runId (32 lowercase hex characters) before dispatch so a lost launch response can be recovered with tests.result. Otherwise use tests.runs to recover the generated ID. Never blindly repeat an unknown launch. Current scripts must be compiled; optional inputPaths fingerprint fixture/content files alongside script inputs. |
| [`tests.runs`](../Modules/TestRunner/TestRunnerProvider.cs) | Recover a lost tests.run response: list the latest 32 launches, newest first, including completed runs. Metadata survives domain reload, not Editor restart. filterSummary is capped at 1000 characters. Use tests.result with the matching runId for current status/results; do not guess when multiple launches match. |

## Timeline

| Tool | Purpose |
| --- | --- |
| [`asset.animation_clip.curves.read`](../Modules/Timeline/AnimationClipCurveProvider.cs) | Read paged float and object-reference curves from a selected AnimationClip, including imported clip sub-assets. Key detail defaults to 25 keys for one binding. |
| [`asset.animation_clip.curves.write`](../Modules/Timeline/AnimationClipCurveProvider.cs) | Atomically set or remove float curves on a standalone .anim asset. Requires expectedHash and supports dryRun. |
| [`timeline.binding.write`](../Modules/Timeline/TimelineBindingProvider.cs) | Bind one Timeline track on a loaded scene's PlayableDirector to a compatible scene object or component. Existing different bindings are protected; supports dryRun and optional scene save. |
| [`timeline.read`](../Modules/Timeline/TimelineProvider.cs) | Read a Timeline asset as paged normalized tracks and clips with asset-local IDs and an optimistic hash. Track and per-track clip pages default to 25. |
| [`timeline.write`](../Modules/Timeline/TimelineProvider.cs) | Atomically create Animation tracks/clips or edit Timeline clip timing and duration settings. Requires expectedHash and supports dryRun. Create a track and read its trackId before creating a clip. |

## VisualEffectGraph

| Tool | Purpose |
| --- | --- |
| [`vfx.catalog`](../Modules/VisualEffectGraph/VisualEffectGraphProvider.cs) | Search the live Visual Effect Graph node/type catalog. Returns descriptors accepted by vfx.graph.write. |
| [`vfx.graph.read`](../Modules/VisualEffectGraph/VisualEffectGraphProvider.cs) | Read a .vfx asset as normalized JSON, including contexts, blocks, operators, parameters, settings, values, links, notes, groups, layout, and an optimistic hash. |
| [`vfx.graph.write`](../Modules/VisualEffectGraph/VisualEffectGraphProvider.cs) | Validate, compile, and transactionally replace a complete .vfx graph from normalized JSON. Existing assets require expectedHash. Supports dryRun; custom HLSL requires allowCustomHlsl=true. |
