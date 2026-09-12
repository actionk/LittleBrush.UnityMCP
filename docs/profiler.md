# CPU profiling

Use `profiler.start` with `{"durationSeconds":10}`, exercise the Editor, then call
`profiler.stop`. Both return a `captureId`. Stop is idempotent and returns the last
capture after an automatic stop. Existing recordings and Deep Profiling are protected.

For an authorized MCP-owned Play Mode session, use
`editor.stop` with `{"playSessionToken":"...","profileExit":true}`. Ownership is checked
before recording starts. CPU capture starts immediately before requesting exit, then
stops on the Editor update after `EnteredEditMode`. The stop response includes `capture`;
use its ID for reports. This option does not authorize starting Play Mode or stopping a
user-owned session. Capture settings are restored on explicit/automatic stop, reload,
and orderly Editor exit. Other MCP operations may run during recording.

## Search and resume

```json
{"captureId":"...","thread":"Main Thread","marker":"Ivy","sortBy":"maxMs","topN":10,"budgetMs":1000}
```

`profiler.report` aggregates a capture/thread/time scope once. Completed aggregates are
cached beside the raw file. Marker search, sorting and pagination reuse that cache
without loading Unity's Profiler history again (`cacheHit:true`). Cache identity includes
raw file size, modification time and analysis version. A changed thread/time scope
requires its own scan. The raw file remains authoritative.

When `partial:true`, repeat the query with the returned `cursor` and unchanged
capture/thread/fromMs/toMs. Results are cumulative, with `samplesScanned` and the next
frame/thread/sample reporting progress. Changing marker, sort, page or budget is allowed.
Only one unfinished analysis is retained in memory. A different scope, domain reload or
changed Profiler history can expire its cursor; the error instructs restarting without
it. Completed disk caches survive reloads. Partial totals are not final rankings.

Filters are case-insensitive substrings. `fromMs` (inclusive) and `toMs` (exclusive)
select calls by start timestamp relative to the first retained frame. Crossing calls
retain their full duration. Rows remain separate by thread ID and marker.

Sort by `selfMs`, `inclusiveMs`, `maxMs`, `calls`, or `gapMs`. `offset` and `topN` page
results; `nextOffset` is null on the last page. Inclusive rows overlap; self time excludes
direct children. Idle/wait markers describe elapsed time, not necessarily active CPU work.
`meanCadenceMs` measures start-to-start time; `gapMs` and `maxGapMs` measure uncovered
end-to-next-start intervals for the same marker/thread, accounting for nested calls.
These intervals do not prove that a thread was idle.

## Drill down

- `profiler.frames`: page main-thread frames sorted by duration.
- `profiler.sample`: inspect a frame/thread/sample and page its direct children sorted
  by inclusive duration. For a report row, pass `maxFrame` as `frame`, `maxThreadIndex`
  as `threadIndex`, and `maxSample` as `sample`. From a frame row, start at sample 0 on
  thread 0. Child rows contain sample indexes for further inspection.

Frame indexes are relative to retained capture history. Native loading appends data to
Unity's Profiler buffer without opening a window; Unity's history capacity can evict old
frames. Reports explicitly describe retained-history coverage, which may be only a suffix
of the raw file. Keep the history capacity unchanged when following cached sample indexes.
Loaded data is reused while its file identity and native history remain valid.

## Bounds

- Recording: 1–60 seconds (10 default), CPU only unless `includeMemory:true`; no allocation
  call stacks or Deep Profiling. Native buffer budget and cooperative file threshold are
  128 MiB. Duration/size checks run on Editor updates every 250 ms; a stalled frame or
  native buffering can overshoot both limits.
- Loading: independent 512 MiB raw-file limit accommodates recording overshoot. Larger
  files are preserved with an explicit native-load-budget error. Unity's native loader is
  synchronous and cannot be cancelled mid-load; this is not a peak-memory guarantee.
- Analysis: default 1,000 ms per call (configurable 1–5,000), approximately 8 ms slices,
  at most 250,000 samples per call with cooperative boundary overshoot. Budget exhaustion
  returns a continuation cursor. Native views are disposed before yielding. Scope is
  bounded to 20,000 marker/thread groups and 1,024 threads per frame; narrow thread/time
  filters if a group limit is reached.
- Responses: at most 100 rows per page. Capture files and completed aggregate caches
  live under `Library/McpProfiler`, excluded from Git, with no automatic evidence deletion.

## Verification

Focused Edit Mode tests cover recording/restoration, saved markers, oversized-load
validation, aggregation, cached search/paging, continuation and sample drill-down.
The 207 MiB exit capture that previously failed now loads through typed tools and reports
the 23.03 s ivy callback. After the ivy lifecycle fix, the approved exit capture stopped
automatically with `playModeExit`, retained two frames in 31 MiB, and reported a 1.56 s
exit frame with no ivy Tick callback. These are individual profiled observations, not
unprofiled performance guarantees.
