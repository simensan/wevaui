# Cold screen-load attribution — October 31, 2026

Host-engine `490cb3997` adds `ui_screen_load`: elapsed main-thread time for a
script-requested screen creation and `load_files`, including font loading and
failed attempts. Previously this work was outside named UI sections. The timer
runs only when a load is requested. It excludes binding application and the
later document update, snapshot and worker drawing; it does not cover startup
loading outside script requests.

The new section is included once in the CSV main-thread sum. Nested
`ui_document`/`ui_capture` and overlapping `ui_worker_cpu` remain excluded.
The accounting regression supplies a 2 ms load and verifies both the named
column and the corresponding reduction of unattributed time.

## Current runtime evidence

Five fresh processes each open the 720p survival inventory once, hide it and
reopen the retained document. Median opening-frame observations, milliseconds:

| Measurement | Cold | Retained |
| --- | ---: | ---: |
| Main CPU | 19.5668 | 0.5641 |
| Screen creation/load | 2.7733 | 0 |
| Document update | 16.2821 | 0.1325 |
| Snapshot capture | 0.2980 | 0.0004 |
| Worker UI CPU | 1.2944 | 0.1139 |
| Following frame interval | 19.6221 | 0.6106 |

Independent medians are not additive, and worker work overlaps the main
thread. All cold main-CPU samples are 19.38–21.15 ms in this batch. The earlier
77 ms sample remains valid evidence; its cause is not resolved by the absence
of a similar spike here. This is attribution, not a paired speedup claim.

The remaining cold cost is dominated by document update rather than screen
loading. The earlier buffered glyph diagnostic attributed substantial first
paint time to glyph scaling/rasterization. Markup precompilation or faster
file access alone would leave most of the opening cost. Preparing first
layout/paint/glyph resources before interactive gameplay remains the useful
next direction, while preserving hidden-overlay deferral and font output.

## Verification

- Five capture-trace tests pass (`builds/verify/20261031-014245`). The initial
  run failed because the test's section-sum expectation still omitted the new
  2 ms sample; that evidence is retained at `20261031-014221`.
- Nine runtime render-thread tests and production compile checks pass
  (`builds/verify/20261031-014256`). Official optimized SDK builds.
- Ten 6,000-frame profiles: 60,000 unmarked CPU-only frames, actual opening
  input tick verified inside each profile, 243 entities throughout, immutable
  report/frame/capture hashes verified, all five sessions stopped. All ten
  captures match the previously inspected retained inventory image.
- Same RTX 5080/Windows machine, synthetic 240-cube scene, no VSync,
  off-desktop window; no concurrent builds/tests during runtime profiles.
- Shared core remains `d863b7d3`. No core/ABI/font behavior or Godot/Unity
  code changes. This host-engine profiler correction does not measure their load
  paths. No full release gate, upstream merge or configured-SDK switch.

[Runtime hashes, SDK identity and every opening](screen-load-profile-20261031.json).
Raw evidence: `.utmp/render-audit-20261030/screen-load-current/` and
`screen-load-sdk.log`. The API feedback observation
`weva-screen-load-profile-scope` records this corrected measurement gap.
