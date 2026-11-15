# Host-engine memory ownership follow-up — 2026-11-01

The current locally built SDK **does not reproduce the historical +78.20 MiB
five-minute reload growth in this workload**. After 1,801 reloads its private
commit ends 2.32 MiB below the warmed starting sample. The extra Rust allocation
observed at the active endpoint subsides after stopping reloads and holding the
inventory idle. This is a scoped result, not a leak-free certification or a
causal explanation of the older SDK's growth.

This pass adds opt-in ownership snapshots and an idle cooldown to the reusable
runtime audit runner. No engine runtime code changes. The older memory finding
and its receipts are preserved in the [host memory audit](host-memory-20261101.md).
The [JSON receipt](host-engine-memory-20261101.json) records exact identities, counters,
artifact hashes and limits; raw evidence is in `.utmp/host-engine-memory-20261101/`.

## Identified build and controls

The existing leased host-engine audit worktree was clean and fast-forwarded from
`c2288c71c` to current local main `a8bab554e77ad5177ea03ceccb92083d2ce2e728`.
That main already contains the native allocation ledger, heap and shader
observations used here. They are inherited changes, not implementations from
this pass.

An optimized CLI/headless/runtime SDK was built with explicit `WEVA_SOURCE_DIR`
pointing to this checkout at `d9d32caecab4bc5f7929df0236fec8fe0c48cc38`; native
inputs are clean and their content hash is recorded. The immutable SDK key is
`36a5c06a91b3dfb8f401e26f5ac09f447107fce62ed943c530c151de90470c2c`;
runtime SHA-256 is `ff9d438aa204c26281faf07bb93f3c16bb26ec0c03510b49e72163e6086c4359`.
Version, command discovery and doctor pass. This is an explicitly selected local
SDK, not a published cross-platform package or a retargeting of any game.

Both fresh rendered processes use the same existing 1280 × 720 survival fixture,
real fonts/PNG icons and 240-cube world, capped at 60 updates/s. The fixture is
unchanged from the previous capped controls. Each process warms for 30 seconds,
records 61 process-memory samples over 300 seconds, then keeps the same UI
loaded and idle for a 60-second cooldown with 13 samples. Idle and reload run
concurrently; other workspace/game activity remains allowed. No performance
timing comparison is claimed.

Three allocator snapshots per process are collected at warm, active-end and
cooldown-end boundaries. Active process sampling queries script state only at
the first sample; subsequent samples use its explicitly marked last observation.
Endpoint state verifies progress; cooldown state verifies that reloads stopped.
There is no continuous progress assertion in endpoint mode. All snapshots
identify the same PID, process birth and runtime hash within their own run.

## Measurements

MiB is 1,048,576 bytes; KiB is 1,024 bytes. Private-commit values in this table
come from the process samples; Rust values come from the boundary snapshots.

| Metric | Idle control | Reload control |
|---|---:|---:|
| Observed updates/s | 60.003 | 59.995 |
| Active reloads | 0 | 1,801 |
| Warm private commit | 832.301 MiB | 832.328 MiB |
| Active-end private commit | 830.250 MiB | 830.004 MiB |
| Active private change | -2.051 MiB | -2.324 MiB |
| Cooldown-end private commit | 834.676 MiB | 830.445 MiB |
| Whole-run private change | +2.375 MiB | -1.883 MiB |
| Rust live requested change, active-end vs warm | +146.67 KiB | +690.54 KiB |
| Rust live requested change, cooldown-end vs warm | +231.38 KiB | +219.56 KiB |
| Default-heap allocated change, cooldown-end vs warm | +375.29 KiB | +267.27 KiB |
| Default-heap committed change, cooldown-end vs warm | +4 KiB | +4 KiB |

The reload control's Rust live bytes fall by **482,282 bytes** between active-end
and cooldown-end. Its remaining warm-to-cooldown change is 12,102 bytes smaller
than the idle control's. Allocation-origin deltas are concentrated in startup
and unscoped storage; the UI owner does not grow in either run. Original-owner
accounting preserves the owner across reallocations, so a zero UI-owner delta
does not prove that all UI-related storage was measured under that owner.

The guest linear memory stays 1 MiB, with the same loaded WASM identity. Shader
IR stays at 52 live modules and 433,449 compressed bytes; the routed Vulkan host
allocation counters also stay unchanged. No native script DLL is loaded.
These are overlapping subsystem views, not numbers to add to process commit.
The default heap includes Rust and foreign users of that heap; it does not
isolate C++ or include every driver heap and virtual mapping.

## Verification and implementation

- The runner's `--memory-snapshots` requires a real immutable memory artifact
  with the diagnostic allocator available. Missing, ambiguous or unavailable
  observations fail instead of silently producing an ownership result.
- `--lifecycle-cooldown` preserves the loaded UI, samples process memory,
  verifies unchanged reload count and checks interaction afterward. Duration
  bounds reject negative, excessive and non-finite input.
- An initial rendered smoke run passes the three snapshots, reloads, cooldown,
  selection, use, close, post-reload click and owned-session shutdown. Both full
  controls pass these interaction/lifecycle checks and stop successfully.
- Both selected-inventory captures hash to
  `df5d0ec8b0509c51588985f324465baef9302c3dd4bddc98626bbc2cf242c17e`,
  matching the prior integration capture. A current capture was inspected.
- The SDK's own `memory diff` accepts both warm-to-active and warm-to-cooldown
  artifact pairs. Tooling: **90 pass on Linux; 81 pass and nine Linux-only skips
  on Windows**, including three new ownership/cooldown regressions.

No core, Unity or Godot runtime source changed, so the earlier qualified
[integration gate](tech-debt-20261101.md) remains the applicable runtime evidence.
This pass does not claim a new complete engine gate, hardware GPU soak, long
session qualification, default host-engine native-pin update or macOS execution.

## What remains

The historical SDK's growth is still unattributed. Host-engine main, the Weva core
and diagnostic instrumentation all differ from that SDK; this comparison cannot
credit one fix or establish that its old allocation owner is gone. The new
ownership API substantially narrows what can be observed, but C++ allocations,
several driver/resource classes and broad startup/unscoped origins still limit
complete attribution. The existing `weva-runtime-memory-ownership-census`
feedback item remains in progress.

There is no confirmed current runtime defect to patch from these measurements.
The next actionable shared-core memory feature is a safe image-cache budget for
unique URL churn, described in the [technical-debt audit](tech-debt-20261101.md#remaining-debt-in-priority-order).
It needs an owned resource-lifetime boundary, not arbitrary eviction of borrowed
decoded-image pointers. A longer hardware soak and matched build ablations are
appropriate if the current SDK later reproduces the older growth.

Reproduce with the command in
[the performance runner guide](../../Tools/performance/README.md#host-cpu-submission-comparisons),
using `--memory-snapshots --lifecycle-cooldown 60` and separate idle/reload outputs.
