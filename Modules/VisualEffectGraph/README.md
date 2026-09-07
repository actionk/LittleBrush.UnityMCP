# Visual Effect Graph MCP module

Requires `com.unity.visualeffectgraph` 17.x or newer. The adapter uses Unity's
internal editor model through a version-gated reflection boundary; incompatible
package changes make the tools unavailable instead of falling back to YAML edits.

## Tools

- `vfx.catalog`: searches contexts, blocks, operators, parameter types, slot
  types, and subgraphs. Results are compact summaries by default; use
  `detail: "full"` for settings and slots.
- `vfx.graph.read`: defaults to a small graph outline. Use `profile: "topology"`
  for connectivity or `profile: "full"` for complete editable JSON.
- `vfx.graph.write`: replaces one complete graph. Existing assets require the
  hash returned by `vfx.graph.read`; `dryRun` builds and compiles a temporary
  graph without changing the target.

`vfx.graph.write` accepts contexts with ordered blocks, operators, parameters,
flow/data edges, model settings, recursive slot values, curves, gradients, asset
references, layout, groups, notes, multiple parameter nodes, and block/operator
subgraphs. Use catalog IDs from `vfx.catalog`; a `type:` ID returned by
`vfx.graph.read` is a package-owned hidden or legacy VFX model and is accepted
only from Unity's VFX editor assembly.

Custom HLSL nodes require `allowCustomHlsl: true`. Input is limited to 2 MiB,
2,000 models, and 5,000 edges. The writer compiles a temporary graph first,
preserves an existing target's `.meta` GUID, and restores the original bytes if
the target import fails.
