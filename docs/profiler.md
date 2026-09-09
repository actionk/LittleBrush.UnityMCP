# CPU profiling

Use `profiler.start` with `{"durationSeconds":10}`, exercise the Editor, then call
`profiler.stop` and `profiler.report`. Start returns immediately with a `captureId`.
Stop is idempotent and returns the last capture after an automatic stop. Report defaults
to that capture; supply its ID to revisit another saved recording.

Example report arguments:

```json
{"thread":"Main Thread","marker":"RoomMesh.","fromMs":0,"toMs":10000,"topN":20,"sortBy":"selfMs"}
```

Thread and marker filters are case-insensitive substrings. Omit them to include all
threads and markers. Rows remain separate by thread ID and marker. The interval selects
calls by start timestamp, relative to the first loaded frame; its end is exclusive.
Calls crossing the boundary retain their full duration. Sort by `selfMs`, `inclusiveMs`,
`maxMs`, `calls`, or `gapMs`. Inclusive time overlaps across nested markers.
Idle/wait markers and `(unnamed)` thread spans describe elapsed time, not necessarily
active CPU execution. Prefer a relevant thread/marker filter when investigating work.

`meanCadenceMs` measures start-to-start time. `gapMs` sums uncovered end-to-next-start
time for the same marker/thread, accounting for nested calls; `maxGapMs` is the largest
gap. These are elapsed intervals, not CPU work or proof that a thread was idle.
An interval requires two calls inside the selected range.

Captures use the local Editor CPU profiler, without Deep Profiling, memory snapshots,
or allocation call stacks. `includeMemory:true` additionally enables the Memory module.
CPU markers can still describe allocations. Existing recordings and Deep Profiling
are rejected; MCP does not silently replace them. Recording settings are restored on
explicit/automatic stop, reload, and orderly Editor exit. Other MCP operations can run
during recording so their work can be measured. Avoid changing Profiler settings manually
during a capture; stop it first.

Raw files and small metadata files stay under `Library/McpProfiler`, excluded from Git.
They persist until explicitly deleted or Library is cleaned; no automatic retention
deletes evidence. No raw samples are returned through MCP. Reporting appends the raw file
to Unity's Profiler history without opening/focusing a window. History capacity can evict
older frames, so a report can represent only the retained suffix; the file is authoritative.
Use Unity's Profiler to inspect the complete file when a report is partial.

## Limits and acceptance

- Duration: 1–60 seconds (10 default). A 250 ms Editor-update check stops on the deadline
  or a 128 MiB file threshold. These are cooperative limits: blocked Editor updates and
  native log buffering can overshoot. Native profiler buffer budget is 128 MiB.
- Report: raw load capped at 128 MiB; at most 2 million scanned samples, 20,000 marker/thread
  groups, 1,024 threads per frame, 5 seconds of analysis wall time and 100 returned rows.
  Limit exhaustion is explicitly marked partial. Unity's native file loading is synchronous
  and cannot be cancelled; shorter captures reduce its latency. Sample analysis yields
  around an 8 ms budget, checks cancellation, and disposes native views before yielding.
  `loadMs`, `analysisMs` (including yields), and `maxAnalysisSliceMs` expose report overhead.
- Verification criteria: no new compilation warnings/errors; focused tests prove timing
  arithmetic, automatic stop, settings restoration, saved raw capture, compact filtering,
  duplicate-start rejection and input validation. Native load latency and capture overhead
  must be measured in the target workload; these limits are not release performance promises.

The timestamp definition follows Unity's [GetSampleStartTimeMs API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Profiling.RawFrameDataView.GetSampleStartTimeMs.html).

## Development verification — 2026-09-10

Unity 6000.6.0f1, Windows 11, i7-10700, 64 GB RAM; background Editor with the
ConstructionToolkit showcase loaded. These are single-run diagnostics, not release
budgets or a comparison of recording overhead against an unprofiled baseline.

| Capture | Actual stop | Raw bytes | Result |
| --- | --- | --- | --- |
| 2 s CPU | 2.016 s | 10,453,183 | 59,123 main-thread samples, compact top-5 output |
| 10 s CPU | 10.159 s | 27,504,023 | All-thread analysis returned an explicit partial result at the 5 s limit |

The final filtered report of the 10 s capture (`Main Thread`, `PolygonConstruction.Scan`,
0–3,000 ms) loaded in 132.94 ms, scanned 91,901 samples and returned 14 calls in one row.
Analysis took 2,646.72 ms including Editor yields; longest measured slice was 18.94 ms.
The 8 ms yield target is cooperative and can overshoot. Repeat reports and capture cycles
succeeded; native views are disposed between slices. Managed allocations and peak process
memory were not isolated from other Editor work and remain unmeasured.

Final focused Edit Mode run: **2 passed, 0 failed, 0 skipped**, 1.812 s test duration
(`158145d1e8464729b357488013e4da97`). Compilation had zero errors and no warnings in
the added files; 20 warnings were reported in existing files. An earlier attempt crashed
inside Test Framework bootstrap scene creation (`UnityScene::AddRootToScene`) before
capture began; after Editor restart, direct MCP checks and both subsequent test runs passed.
