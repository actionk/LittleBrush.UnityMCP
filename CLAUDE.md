# LittleBrush Unity MCP

This repository is a standalone Unity MCP server. Read `README.md` first and use the focused documents under `docs/` for protocol or provider work.

## Repository boundaries

- Keep the plugin independent from sibling LittleBrushGames source code. Optional integrations belong outside this repository.
- Keep assembly names under `LittleBrushGames.UnityMCP.*` and public C# namespaces under `LittleBrushGames.Mcp`.
- Rename an `.asmdef` and its `.meta` file together, then update every assembly reference and `InternalsVisibleTo` declaration.
- Preserve Unity `.meta` files and their GUIDs.
- `Bridge~` is hidden from Unity but tracked by Git; do not treat it as generated output.
- Microsoft Roslyn binaries under `ThirdParty/Roslyn` come from official NuGet packages. Update them only from those packages and keep their license, notices, versions, and hashes current.

## Implementation rules

- Prefer the smallest durable change and reuse existing providers, transport code, serializers, and tests.
- Keep editor-only code in `Editor/` assemblies and optional package integrations in `Modules/` with version defines.
- Keep the Go bridge on the standard library; do not add Go module dependencies without explicit approval.
- Bound untrusted input, queues, logs, traversal, and payload sizes. Preserve loopback/origin checks and Play Mode ownership protections.
- Keep bridge and Unity transport protocol changes coordinated and update `docs/bridge-protocol.md` when the contract changes.
- Treat `editor.tools.list` as the authoritative tool catalog; keep the README feature overview and docs/tool-reference.md inventory aligned with provider changes; obtain exact schemas from the live registry.
- Do not rebuild the tracked bridge executable, export packages, or run Unity/player builds unless explicitly requested.

## Verification

- Run `go test ./...` from `Bridge~` after Go changes.
- After C# or assembly-definition changes, compile in a consuming Unity project through `editor.ensure_compiled` and run the narrowest relevant Edit Mode tests.
- Keep documentation-only changes lightweight; at minimum run `git diff --check` and verify referenced paths.
