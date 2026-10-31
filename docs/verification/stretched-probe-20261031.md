# Skip doomed stretched-item probes — 2026-10-31

**Superseded:** the optimization was reverted after the
[paired Unity follow-up](stretched-probe-host-20261031.md) failed to establish
a host-level benefit. The regression tests remain. The measurements below
describe the evaluated candidate, not the current production implementation.

The shared core now skips a speculative incremental-layout probe when a
previously stretched item has equal natural/final widths but different
natural/final heights. The existing probe requires preserving both heights,
yet only reapplies width. It otherwise builds and lays out a subtree before
rejecting it. Starting at the owning parent avoids that wasted work; the
parent still computes allocation, minimum sizes and cross-axis stretching.
This does not narrow the final replacement boundary or remove paint work.

## Paired core measurements

Six alternating baseline/candidate pairs ran the complete twenty-mutation,
twelve-selection inventory sequence. Each fresh process performed 120 warmups
and 20,000 measured updates: 240,000 measured updates in total. Setter status,
twenty final target values and missing assets were checked in every process.
Timing excludes setters, uses StubFont, and has profiling/logging disabled.
No builds or tests overlapped these measurements.

| Pair | Baseline mean (ms) | Candidate mean (ms) |
| --- | ---: | ---: |
| 1 | 0.379173 | 0.354163 |
| 2 | 0.388363 | 0.373996 |
| 3 | 0.385491 | 0.342653 |
| 4 | 0.362075 | 0.373126 |
| 5 | 0.384181 | 0.351581 |
| 6 | 0.373618 | 0.356940 |

The candidate improves five of six pairs, with a **5.53% median paired
reduction**. Medians of run means are 0.381677 and 0.355552 ms. The losing
pair is retained. This is a core CPU result, not a real-font host frame-time,
GPU, cold-start or Chrome performance claim. Hardware is the existing Ryzen
7 9800X3D Windows/WSL2 setup, without affinity or clock locking.

## Correctness and host synchronization

The added regression exercises four successive text changes in six layouts:
ordinary stretching, shrinking, height bounds, content-driven parent height,
wrapped flex lines and fixed grid tracks. It compares incremental output
against a forced full layout with identical glyph history, including vertex,
index, effect, scissor and texture data. No host behavior or ABI changes.

- GCC: 15/15 CTest suites; core suite 493,558 checks, zero failures.
- Clang with AddressSanitizer/UndefinedBehaviorSanitizer: 17/17 CTest suites.
- Chrome captures: 47 samples, 68 hand cases and 225 harvested cases pass the
  1.5 px ceiling, with no excused or missing captures.
- Unity Windows native plugin rebuilt, installed and load-tested. Native
  EditMode: 254 passed, two existing environment-gated inconclusive, zero
  failures. PlayMode rendering: 7/7; all sixteen captured PNGs exactly match
  the preceding direct-vertex renderer run. The packaged font/image/binding
  render was also inspected.
- Godot Windows and Linux extensions rebuilt. Qualified Linux Godot: host
  247, font-size 208, font-inheritance 560, texture lifetime 48 headless and
  96 rendered checks, all with zero failures. The rendered run used Xvfb and
  OpenGL compatibility; its unavailable audio device falls back to dummy.

These are affected-suite checks, not a new full local gate or player/export
qualification. Both hosts receive the optimization through the same core;
there is no duplicated layout policy in either adapter.

## Evidence

Tracked timing receipt: [stretched-probe-20261031.json](stretched-probe-20261031.json).
Local evidence is under `.utmp/render-audit-20261030/`: the
`core-selection-profile/imposed-probe-pairs/` logs and binary hashes,
`imposed-probe-*-build.log`, `imposed-probe-*-tests.log`,
`imposed-probe-chrome-*.log`, Unity XML/logs and
`imposed-probe-play-renders/`, and `imposed-probe-godot-*.log`.
Baseline source is preserved as `imposed-probe-baseline.cpp`; baseline commit
is `7afc6f82`. The diagnostic and its limitations are described in the
[inventory attribution audit](inventory-invalidation-20261031.md).
