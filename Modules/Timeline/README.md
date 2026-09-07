# Timeline MCP module

Requires `com.unity.timeline` 1.0 or newer. The module is omitted when Timeline
is not installed.

## Tools

- `timeline.read` defaults to an `outline` of tracks and clips. Use
  `profile: "full"` for capabilities, object references, and detailed timing.
- `timeline.write` applies bounded `create_track`, `create_clip`, `set_clip`, and
  `set_timeline` operations with hash checking, validation before mutation,
  Undo, and `dryRun` support. Creation currently supports root AnimationTracks
  backed by standalone `.anim` clips. Create and read a track before adding its
  clip so the second request can use the returned asset-local `trackId`.
- `timeline.binding.write` binds one existing track on a loaded scene's
  PlayableDirector to a compatible GameObject or Component. It rejects replacing
  a different existing binding, supports `dryRun`, and saves only when requested.
- `asset.animation_clip.curves.read` defaults to paged binding summaries. Use
  `detail: "keys"` with binding and key paging only for curves being inspected.
- `asset.animation_clip.curves.write` sets or removes float curves on standalone
  `.anim` assets with hash checking, Undo, and `dryRun` support.

Track IDs and clip IDs are strings so 64-bit Unity local IDs survive JSON
clients without precision loss. Clip IDs are asset-local
(`trackLocalId:clipIndex`) and must be used with the hash returned by the same
read. Imported AnimationClips are read-only; extract
one with `asset.animation_clip.extract` before changing its curves. Clip
extrapolation modes are reported by `timeline.read` but are not writable through
the installed Timeline package's public API.
