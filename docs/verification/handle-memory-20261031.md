# Reclaim retired element-handle storage — 2026-10-31

Repeated subtree replacement kept a pointer-sized tombstone for every historical
element in the document. A sparse ordered table now releases empty 256-slot
pages while preserving monotonically increasing IDs. Full markup loads also
preserve the serial, preventing stale handles from targeting new elements.
Box snapshots reuse the existing reverse map instead of allocating a second one.
This shared-core change applies to Unity, Godot and Agentic without host logic
or a C ABI change.

## Memory evidence

Baseline: `7fa65255`. Each isolated process warms and destroys one document,
then keeps 32 children live while running 5,000 operations. The probe counts
requested live C++ `new` storage, including atomic accounting for raster workers.
It excludes direct `malloc`, host and GPU allocations. Allocator and RSS samples
are supplementary; neither equals the live C++ counter.

| Tracked live C++ bytes | Baseline | Candidate |
| --- | ---: | ---: |
| Fresh active document | 2,453,166 | 2,454,790 |
| After 500 subtree replacements | 2,615,214 | 2,488,382 |
| After 5,000 subtree replacements | 4,581,294 | 2,488,382 |
| After 5,000 identical reconcile reloads | 2,483,870 | 2,485,494 |
| After 5,000 full loads | 2,483,870 | 2,485,494 |
| After 5,000 idle updates | 2,453,166 | 2,454,790 |
| After destruction, every mode | 75,832 | 75,832 |

The replacement case saves 2,092,912 bytes (1.996 MiB, 45.7%) at iteration 5,000.
The candidate is flat at every 500-operation sample from 500 onward. The initial
cost is 1,624 bytes. Pages containing surviving elements remain allocated;
storage is bounded by live pages rather than the lifetime number of removed
nodes. Numeric IDs are not reused; reaching the reserved invalid ID stops ID
allocation instead of wrapping.

The regression performs 4,096 replacements, checks 64 retired handles spanning
reclaimed pages, and checks surviving handles, reconcile reload, full-load
stale mutation rejection and destruction. The old implementation fails three
assertions (retention and two stale full-load assertions); the candidate passes
all 8,352. A 64 KiB plateau budget avoids depending on exact allocator sizing.

This does **not** attribute the earlier 13.7 MiB Agentic process-memory growth.
Identical reload controls were already flat in the old core. Host allocation
profiling and a matched long-duration control remain necessary for that finding.
No CPU speedup or whole-process memory reduction is claimed in this pass.

## Verification

Final gate results and evidence hashes are recorded in the adjacent JSON receipt.
Raw baseline/candidate probes, source snapshots and the failed baseline regression
are preserved under `.utmp/memory-20261031/`. The portable regression is committed
as `libweva/tests/test_handle_memory.cpp` and runs through CTest.

The qualified release gate passes: 18 GCC suites, 20 Clang sanitizer suites,
Chrome layout captures and 67 browser behavior scripts (one existing known IME
failure), Godot font/Unicode, backend, scene, reload and export checks. The main
core suite remains 808,781 checks; the new memory suite adds 8,352 checks.
Both Windows hosts rebuilt. Unity full EditMode passes 255 tests with the same
two environment-gated inconclusive results; the Mono player passes 7/7.
All 16 Unity render captures and the Godot render match the accepted baseline
exactly; representative renders were visually inspected. No gate retry was
needed for this chunk. The earlier baseline regression failures are intentional
positive-control evidence, not unresolved candidate failures.
