# Frontier Camp: load, cold opening and lifecycle

## Current installed addon: five-minute lifecycle verification

The opacity/stacking-corrected addon passes 200 recreations and 300 seconds of
Unicode updates in the 1080p Vulkan 3D scene: 208,642 frames and 35,214 name
updates. Cold/prepared screenshots match. All soak markers retain 496 nodes and
1,949 objects. Whole-process private-memory medians by minute are 863.695,
863.740, 863.773, 863.773 and 863.773 MB; the final minute peaks at 863.908 MB.

Cold construction costs 82.500 ms CPU and hidden preparation 26.267 ms.
Prepared reuse with hidden data updates costs 0.383 ms CPU p95 and 3.496 ms
through draw p95. Soak frame p95 is 4.080 ms, maximum 15.998 ms. These are
lifecycle observations on the patched Windows release runtime, not isolated UI
memory/GPU costs, a causal speedup or an hours-long leak test. The separate
current 1080p and 4K timing profiles still fail five gates each.
[Exact identity and results](../../docs/verification/lifecycle193.json).

## Historical five-minute follow-up on the explicit patched runtime

The following results describe the earlier addon recorded in lifecycle185 and
template184; references to installation below apply to those historical runs.

The current installed addon passes 200 recreations and a 300-second Unicode soak
with 161,306 frames. Every soak marker records 496 nodes and 1,949 objects.
Whole-process private-memory medians in consecutive one-minute windows are
861.22, 861.88, 861.97, 862.11 and 862.11 MB. The final minute peaks at 862.24 MB.
Growth largely settles during this run; this is not an hours-long leak test or
an isolated measurement of UI memory.

Cold construction CPU is 83.260 ms, hidden preparation 34.835 ms, and prepared
reuse CPU p95 0.404 ms. Reuse through draw p95 is 11.341 ms; soak frame p95 is
7.840 ms with a 31.920 ms maximum. These observations are retained alongside
the successful lifecycle assertions and matching cold/prepared captures. This
run does not replace the separate timing-budget qualification below.
[Raw identities, memory windows and results](../../docs/verification/lifecycle185.json).

The installed addon has now been measured with an explicitly selected and hashed
patched Godot release template. The normal desktop profile passes all 72 timing
checks across three OpenGL and three Vulkan runs. API p95 ranges: vitals at 10Hz
0.276–0.707 ms, inventory sorting 0.737–1.216 ms, settings toggles 1.391–1.808 ms,
typing 0.391–0.830 ms, hover 0.202–0.444 ms. The automatic 1080p deterministic
3D profile passes all 276 timing checks. These runs do not prove a causal speedup.

The same installed addon passes 200 UI recreations and a 120-second Unicode soak
(127,069 frames, 21,619 name updates) on the explicit patched runtime. Cold first
construction costs 70.620 ms CPU; hidden preparation costs 19.044 ms. Prepared
reuse with hidden data updates costs 0.658 ms CPU p95 and 1.580 ms through draw
p95. Prepared/cold screenshots match. Private process memory rises from 852.72 MB
at soak start to 861.59 MB at the last soak marker and ends at 859.48 MB after
teardown; this is not proof of long-term memory stability.

The earlier performance exporter selected a cached/default template despite
using the patched editor. Its manual and 3D measurements apply to that recorded
executable, not to the intended patched runtime. Its Unicode lifecycle run
crashed with Windows heap corruption (0xC0000374); the cause remains unproven.
The exporter now accepts `--release-template`, records its SHA256 and restores
export presets even on failure. Both renderer smoke exports and 23 harness
tests pass. Stock-engine compatibility, 4K requalification, longer memory runs
and other hardware remain open.

[Current lifecycle and template evidence](../../docs/verification/template184.json).

## Historical 4K qualification

The current centred sample, installed native runtime and event diagnostics pass
all twelve workloads across six automatic 3840×2160 3D runs (three per renderer),
600 measured frames after 120 warmups each. **276/276 timing checks pass** with
the original 4K limits. Captures retain the centred modal and correct UI state.

Changed API p95 ranges are 0.303–0.445 ms for inventory sorting, 0.386–0.504 ms for
typing and 1.306–1.792 ms for toggles. Separate cumulative core p95 ranges are
0.5243–0.6226, 0.1327–0.1943 and 0.6620–1.0265 ms respectively. API/core intervals
overlap and must not be added. Every combined-scene whole-frame or changed-frame
p95 is at most 3.942 ms. These measurements apply to the RTX 5080 development
machine and do not prove a causal speedup or acceptance on other hardware.

The earlier input failure below remains unexplained. Its absence from the two
diagnostics and this full qualification is evidence about these runs, not proof
of a root-cause fix. [Full current receipt](../../docs/verification/load150-4k.json).

## Earlier 4K follow-up: incomplete failed qualification

The six-run automatic 3840×2160 profile uses the same workload and 60 FPS limits
as 1080p. It stopped in the third run: Vulkan reverse-order inventory sorting
also triggered a gameplay action. The original failure is preserved and its cause
is not yet established. A focused four-case editor diagnostic did not reproduce
it; this does not clear the release-export failure. No complete 4K timing pass is
claimed. [Partial evidence](../../docs/verification/load147-4k.json).

A subsequent instrumented **release** export also completes all twelve reverse-order
workloads with captures and records no unhandled gameplay mouse/key event. It uses
the original frozen sample. This does not identify the original failure's cause
or replace its failed qualification. [Diagnostic receipt](../../docs/verification/input148-diagnostic.json).

The captures also exposed a sample CSS issue: the settings dialog was 433 pixels
below centre at 4K in Chrome. Replacing the explicit calculated top with zero
lets the dialog's automatic margins centre it. Native checks now report zero
centre offset at 720p, 1080p and 4K, and all 93 sample integration checks pass.
This changes the sample source, not the installed addon binary or the frozen
export used above. [Centering evidence](../../docs/verification/dialog147.json).
The corrected source now also passes fresh-project import, 93 headless integration
checks, 99 rendered checks in each renderer, and 93/99 checks in the release export.
[Corrected sample export](../../docs/verification/dialog148-export.json).

## Installed: bounded retained text-cache payload

A current-runtime 36,000-frame mixed-script diagnostic reaches 4,096 shaped
entries and **13.32 MiB** of retained text/glyph/entry payload. The count limit
permits substantial growth while long changing names fill it. This accounts for
a concrete retained allocation source; it does not account for all process memory.
[Baseline evidence](../../docs/verification/cache146-baseline.json) includes the
cache events and lifecycle receipt. Core builds overlapped part of this diagnostic,
so its timing is not qualification evidence.

The installed build adds a 4 MiB per-document payload budget, retaining the
4,096-entry limit and incremental LRU eviction. Oversized results remain usable
but are not retained. A regression fails on the baseline and checks eviction of
cold long labels while preserving a hot label and correct geometry. Native payload, sanitizer and both performance profiles now pass;
[qualification](../../docs/verification/cache146.json) records their scope.

The candidate's native 36,000-frame diagnostic now passes and peaks at
**3.9998 MiB** of retained cache payload (1,347 maximum entries). Active-UI private
memory is 823.54 → 822.31 MiB between first and last soak markers; nodes/objects
stay at 496/1,949. Concurrent sanitizer/core checks make timing comparisons invalid.
The native host's 27,827 checks, sample and export checks pass, as does an additional
regression for shaping an oversized result without retaining it or evicting hot
labels. [Candidate evidence](../../docs/verification/cache146-candidate.json).
The validated build is now installed in both local addon copies.

## Current build: cold preparation, reuse and short memory comparison

The installed runtime passed 100 measured reuse and 100 reconstruction cycles,
including mixed-script name changes, in a Vulkan 1080p 3D release export. Cold
and prepared screenshots match byte for byte; destruction disconnects bindings.
First hidden construction took **83.180 ms** in this process. Showing that prepared
document took 0.020 ms CPU / 1.832 ms through draw. Reusing it after hidden data
changes measured **0.771 ms CPU p95**, with 8.517 ms p95 through draw. The latter
includes scene work and scheduling. Reconstructing instead measured 46.276 ms
CPU p95. Prepare during loading and retain the document for ordinary opening.

The 30-second active-UI soak processed 13,184 frames. A separate run warmed the
same resources, released the UI, and drove the same number of scene/model frames
in 37.966 seconds. Private memory between first and last soak markers grew
**5.121 MiB with UI**, and fell **5.715 MiB without UI**. Active-UI node/object counts
stayed at 496/1,949; the released arm stayed at 491/1,931. This is evidence of
unresolved retained process memory, not proof of a leak or a long-term bound.
Sequential runs and allocator behavior prevent treating their difference as an
exact UI allocation total. Longer attribution remains required.

[Receipts and memory markers](../../docs/verification/lifecycle145.json).

## Current build: automatic 1080p 3D qualification (checkpoint 144)

The installed runtime (checkpoint 143) passed functional checks in all six runs,
three per renderer, with 600 measured frames and 120 warmups per workload.
**Timing qualification failed: 274 of 276 checks passed.** The explicit profile
was defined before measurement; failed limits have not been relaxed.

All combined-scene whole-frame p95 checks met the 16.667 ms target. All core
processing checks passed. Two elapsed input-dispatch checks failed in OpenGL run 2:

| Workload | Input-dispatch p95 | Limit | Changed core p95 |
| --- | ---: | ---: | ---: |
| Settings typing | 1.156 ms | 1.000 ms | 0.131 ms |
| Settings toggle | 3.022 ms | 3.000 ms | 0.886 ms |

This narrows investigation to input-dispatch elapsed time; it does not establish
which host, OS or engine operation caused the overrun. API and core timings
overlap and cannot be added. Sequential whole-frame percentiles cannot be
subtracted to claim isolated UI overhead. This is RTX 5080 evidence, not lower-end
hardware, higher-resolution or long-lifecycle qualification.

[Full six-run evidence](../../docs/verification/load144.json).
[Timing profile](tests/performance_budget_desktop_3d.json).

## Preview114 (historical automatic 3D measurement) — aligned core updates in the animated scene

All twelve workloads pass with automatic updates at 1920×1080 in both renderers
(one run each, 600 measured frames and 120 warmups). Cumulative counters now include
all core updates between process-frame boundaries, rather than only the latest
update sampled before deferred work.

| Changed workload | OpenGL core p95 | Vulkan core p95 |
| --- | ---: | ---: |
| Continuous vitals | 0.2084 ms | 0.2195 ms |
| Inventory reorder | 0.5614 ms | 0.5677 ms |
| Settings typing | 0.1748 ms | 0.4150 ms |
| Settings toggle | 1.0560 ms | 1.0838 ms |

Disabled UI records zero core updates/time; idle records one update per frame.
Continuous vitals record two updates per frame; toggles can record four. This
explains why a last-update-only sample undercounted work. Native tests check
synchronous/deferred accumulation, read-only counters, disabled and automatic paths.

These are core intervals, not total UI cost: binding refresh, font setup, host
texture upload, handlers, canvas drawing and GPU remain outside them. API timing
overlaps synchronous core work and must not be added to these totals. Whole-frame
and viewport figures include the scene and scheduling. No 3D timing profile was
applied. [Full evidence](../../docs/verification/timing-preview114.json).


## Preview113 (historical) — automatic updates in the animated 3D scene

All twelve interaction workloads pass in both OpenGL and Vulkan at 1920×1080,
with automatic updates enabled, 600 measured frames and 120 warmups per workload.
Each renderer ran once. The scene reports 115,490 primitives.

| Workload | OpenGL whole-frame p95 | Vulkan whole-frame p95 |
| --- | ---: | ---: |
| UI disabled | 13.687 ms | 15.837 ms |
| Idle HUD | 14.896 ms | 15.410 ms |
| Continuous vitals | 13.959 ms | 12.525 ms |
| Inventory reorder | 13.813 ms | 13.893 ms |
| Settings typing | 13.759 ms | 14.171 ms |
| Settings toggle | 15.324 ms | 13.505 ms |

These intervals include scene work and frame scheduling. The cases run sequentially;
subtracting their percentiles cannot establish isolated UI overhead. Substantial
frame time is present with the UI disabled. API CPU excludes deferred automatic
updates, while `core_cpu` is sampled before the next process frame; neither gives
an aligned total UI cost for a changed automatic frame. Correcting that measurement
alignment is still necessary. These values are not comparable to historical runs
as a causal regression or improvement claim.

No timing profile was applied. This is functional and observational evidence,
not acceptance for busy-scene UI overhead, total GPU cost or other hardware.
[Portable evidence](../../docs/verification/load-auto-preview113.json) retains all
cases and identities. Raw results: `.utmp/safe-engine71/number113-3d/summary.json`.
Cold/reuse and long lifecycle evidence below remains historical preview100.


## Preview100 — automatic updates in the animated 3D scene

The preview100 Windows release export passed all twelve interaction workloads at
1920×1080 in OpenGL and Vulkan with automatic updates enabled. Each renderer ran
once, with 120 warmup frames and 600 measured frames per workload. The deterministic
scene contains 480 mesh instances, 96 movers, lights and a moving camera. Observed
scene primitives were 115,490, with 1,126 OpenGL / 402 Vulkan scene draw calls.

| Workload | OpenGL whole-frame p95 | Vulkan whole-frame p95 |
|---|---:|---:|
| UI disabled | 2.946 ms | 1.773 ms |
| Idle HUD | 3.082 ms | 1.732 ms |
| Continuous vitals | 4.174 ms | 1.610 ms |
| Inventory sort | 4.389 ms | 2.573 ms |
| Settings typing | 3.559 ms | 1.807 ms |
| Settings toggle | 4.925 ms | 3.076 ms |

These intervals include the scene, UI and frame scheduling. The cases are sequential;
subtracting their percentiles does not establish isolated UI overhead. API CPU
excludes deferred automatic updates, and `core_cpu` is sampled before the next process
frame, so neither is an aligned total for a changed automatic-update frame. Viewport
CPU/GPU measurements also include the scene. All renderer metrics and twelve cases
are preserved in [portable evidence](../../docs/verification/load-auto-preview100.json).

This is functional and observational load evidence on the RTX 5080 development
machine. No timing profile was applied (`timing_passed: null`); the static/manual
desktop budget does not certify this scenario, lower-end hardware or other games.
Raw results: `.utmp/safe-engine71/load100-auto1080/summary.json`. The export uses
preview100 and the patched Windows engine with embedded ICU described in
[product readiness](../../docs/PRODUCT_READINESS.md).

## Preview100 — cold opening, reuse and ten-minute Unicode lifecycle

The same release export passes a Vulkan 1920×1080 run with the animated 3D scene,
200 measured reuse/recreation cycles and a 600.024-second soak. It performs 59,916
unique name updates across the phases and 356,853 soak frames. Cold and prepared
captures are byte-identical. The names exercise a fixed mixed-script glyph
vocabulary and exceed the 4,096-entry string-measurement cache capacity many times.

| Operation | Measured CPU |
|---|---:|
| Scene resource load | 40.066 ms |
| First document construction | 91.338 ms |
| Subsequent hidden preparation | 24.810 ms |
| Prepared first show | 0.016 ms |
| Hidden data update and reuse, p95 | 0.567 ms |
| Recreate, p95 | 32.233 ms |

Prepared first show takes 1.292 ms through draw; reuse through draw is 1.971 ms p95.
Preparation happens after the first document has warmed resources, so 24.810 ms
does not establish first-ever hidden construction cost. Construction still exceeds
an ordinary frame budget; prepare ahead and retain the document when possible.

At soak markers, nodes stay at 496 and objects range from 1,949 to 1,958, settling
at 1,951. Final teardown returns to 491 nodes. Process-private memory starts at
815.13 MiB and ends at 834.12 MiB, also the observed minimum and maximum. Quarter
positions are 815.13, 828.34, 825.18, 832.10 and 834.12 MiB, with 32 declining
intervals. Final teardown reduces private memory to 819.43 MiB. The approximately
19 MiB soak increase remains an **open retention finding**, not proof of a UI leak
or a bounded plateau. A matching UI-disabled lifecycle run is needed to separate
scene/engine retention from UI retention.

The fixed histogram avoids retaining a timing entry per frame. Whole-frame soak
p95 is 6.98 ms (0.01 ms quantization), with a 130.09 ms maximum and one overflow
sample. This is not a hitch-free or timing-budget pass. Updates are frame-driven
and uncapped, so their wall-clock frequency is higher than a game's nominal 10 Hz.
No concurrent builds or benchmark jobs were launched by this task; desktop
scheduling and unrelated machine activity were not controlled.

[Portable lifecycle evidence](../../docs/verification/lifecycle-preview100.json)
records hashes, timings, memory observations and limitations. Raw evidence is in
`.utmp/safe-engine71/lifecycle100-unicode600/`. Lower-end hardware, 4K on preview100,
other renderers' long soaks and an indefinite memory bound remain unverified.

### Comparing memory at the same workload count

`run_frontier_lifecycle.py --soak-frames 36000` runs exactly that many soak frames
instead of stopping by duration. `--seconds` then supplies the timeout allowance.
Add `--soak-without-ui` for a baseline that performs identical construction/reuse
warmup, destroys the document, and retains the same game model and 3D scene.
Both arms publish the same health/name changes and inventory sorts at the same
frame indices. Only the retained-UI arm can toggle a modal or validate HUD output;
the baseline instead checks that no binding listeners survive teardown.

For ownership isolation the harness also reads `WEVA_FRONTIER_SOAK_SETTINGS`,
`WEVA_FRONTIER_SOAK_SORT` and `WEVA_FRONTIER_SOAK_NAMES` (set to `0` to drop that
soak workload) and `WEVA_FRONTIER_SOAK_ROUNDS` (repeat the soak plus teardown that
many times, emitting a `round_teardown` marker after each). Flat object counts
across rounds classify retention as bounded; a rising count means accumulation.
The runner does not expose these; launch the project directly with `-- --lifecycle`.

Use the same export, renderer, cycles and frame target in two sequential runs with
different output directories. A frame target of 36,000 generates 6,000 soak name
updates, exceeding the string-cache limit. Compare memory by matching marker
indices, not elapsed seconds: execution speed can differ between arms. The baseline
still loads the addon and warms engine/font resources, so it measures incremental
retention after UI teardown, not an engine that has never loaded Weva.

The first preview100 comparison passes both arms: 36,000 frames, 6,000 soak name
updates (6,080 including warmup), 20 measured reuse/recreation cycles, Vulkan at
1920×1080 with the animated 3D scene. Export and sample-source hashes match between
arms. The updated fixture also passes the original duration-based mode in a short
ASCII smoke test, and fresh-project integration/import/export checks pass.

| Process-private memory | UI destroyed before soak | UI retained |
|---|---:|---:|
| First marker | 823.18 MiB | 824.47 MiB |
| Last marker | 814.79 MiB | 829.24 MiB |
| Observed minimum | 814.66 MiB | 818.10 MiB |
| Observed maximum | 823.18 MiB | 833.39 MiB |
| Change between first/last | −8.39 MiB | +4.77 MiB |

Nodes stay at 491 without UI and 496 with it. Object counts are 1,931–1,934 and
1,949–1,950 respectively. The UI-free arm does not reproduce growth; the retained
arm shows smaller net growth than the earlier ten-minute run, with fluctuations.
This narrows the investigation to activity involving the UI, but a sequential
pair cannot distinguish live cache payloads, allocator retention and engine font
storage. Absolute process deltas are not isolated live UI allocations. The earlier
19 MiB observation remains valid; neither run proves a leak or an indefinite bound.
The next diagnostic should account for retained cache payloads at matching points.

[Portable comparison evidence](../../docs/verification/memory-comparison-preview100.json)
includes both reports, hashes and aligned memory markers. Raw directories are
`.utmp/safe-engine71/memory100-{without-ui,with-ui}/`; the fresh export is in
`frontier-memory-comparison/`, and duration-mode smoke in `memory100-duration-smoke/`.
Only the fixture and verification tools changed; the native preview100 DLL did not.

### Retained shaping-cache diagnostics

An instrumented build accepts `run_frontier_lifecycle.py --font-cache-log`, which
sets `WEVA_FONT_CACHE_LOG=1`. The runner requires diagnostic output when requested;
a native build without the diagnostic cannot silently satisfy that request.
`WEVA_SHAPE_CACHE` JSON records identify each backend and report entry count,
string capacity, glyph-vector capacity in bytes, entry-structure storage and map
bucket count. Records appear every 256 retained runs, around capacity/shaper resets,
and on destruction. No text contents are logged.

These are payload capacities, not total heap allocations or OS private memory:
string capacity includes inline storage, and map/allocator overhead is excluded.
The diagnostic is disabled by default and scans only when explicitly enabled;
it changes neither cache keys nor eviction policy. Diagnostic-run timing is not
a replacement for an uninstrumented timing-budget run.

An **uninstalled diagnostic preview101** passes all ten headless core suites and
fresh-project import, integration and release-export checks. Its 36,000-frame
Vulkan Unicode lifecycle run passes with 6,080 total name updates and two complete
cache-clear/refill cycles. Reported cache payload peaks at **13.81 MiB** and is
4.49 MiB immediately before final backend destruction. Both capacity clears report
4,096 entries before clearing and zero entries/string/glyph/entry payload afterward;
the 4,096 map buckets remain allocated.

Process-private memory starts at 825.14 MiB and ends at 824.36 MiB, ranging from
818.16 to 833.52 MiB. Cache warmup and allocator retention are therefore plausible
contributors to the earlier growth. This does not attribute every allocation or
prove an indefinite bound. The observed wholesale eviction of roughly 14 MiB is
a concrete target for evaluating a byte budget and incremental eviction; neither
optimization has been implemented or shown faster by this diagnostic.

[Portable payload evidence](../../docs/verification/cache-payload-diagnostic101.json)
contains cache events, binary/source hashes and the lifecycle receipt. Raw evidence:
`.utmp/safe-engine71/cache101-retained/`. The runner's negative check against
preview100 exits nonzero because the requested diagnostic is absent, while that
run's ordinary lifecycle checks pass (`cache101-missing-diagnostic/`). Installed
preview100 remains unchanged; the diagnostic build is not a fully qualified release.

### Uninstalled preview102 — incremental eviction

The subsequent candidate replaces wholesale capacity clears with least-recently-used
eviction. The same 36,000-frame Unicode workload passes, with 6,080 name updates and
20 sampled eviction pairs showing 4,096 → 4,095 entries before insertion restores
4,096. No wholesale capacity clear occurs. Reported payload peaks at 13.93 MiB and
is 13.90 MiB before destruction, versus diagnostic101's 13.81 MiB peak and 4.49 MiB
final payload. The list links add entry storage, and retaining hot runs keeps more
payload live between updates; this is not a memory-reduction claim or byte budget.

Process-private memory is 825.85 → 834.30 MiB, with a range of 819.73–834.45 MiB.
This shorter fixed-frame run is not a matched duration comparison or an indefinite
memory bound. [Payload evidence](../../docs/verification/cache-lru-payload-preview102.json)
contains the diagnostic records and source/binary metadata. Raw results are in
`.utmp/safe-engine71/cache102-retained/`. The separate uninstrumented timing gate
has an unresolved redundant-signal failure; see [performance](PERFORMANCE.md).
Preview102 remains uninstalled while qualification continues.

## Preview70 with the patched Windows engine — Unicode lifecycle (historical)

The fresh release export now supports `run_frontier_lifecycle.py --unicode-names`.
It verifies embedded ICU, publishes unique long player names containing combining
marks, emoji, Cyrillic and Arabic during hidden reuse, recreation and sustained
updates, and checks the resulting HUD text. The fixed glyph vocabulary separates
changing strings from continually introducing new characters. The ordinary ASCII
mode remains available and passes a short smoke check.

At 1920×1080 with Vulkan and the animated 3D scene, 200 measured recreation/reuse
cycles plus a 120-second soak pass. There are 2,229 name updates across the phases
and 10,731 soak frames. Cold/prepared pixels match. Soak node/object counts stay
at 496/1,949, with 491 nodes after teardown. Process-private memory grows from
839.85 to 850.80 MiB across the soak markers, gradually rather than reaching a
plateau. This does **not** establish a memory bound or prove a leak: the run has
fewer unique strings than the core's 4,096-entry measurement-cache limit.
A subsequent 600-second run goes beyond that capacity: 9,368 name updates across
the phases and 53,568 soak frames pass, with the same fixed node/object counts.
Private memory starts at 814.33 MiB and ends at 825.84 MiB. Intermediate markers
repeatedly fall (for example 830.27 → 822.80 MiB), rather than continuing the
short run's steady increase. This supports bounded cache turnover during the
observed interval; it does not prove an indefinite bound. Evidence is in
`.utmp/safe-engine71/unicode-soak-long/verification.json` and `process-memory.json`.
Core compilation/tests ran concurrently during portions of this follow-up, so
its frame times are not an isolated performance benchmark.

First construction measures 110.191 ms CPU, hidden preparation 43.246 ms and
prepared first show 0.020 ms CPU (4.516 ms through draw). Hidden name-update reuse
is 0.619 ms CPU p95. These are one-run measurements with a deliberately long name;
they are not a matched engine comparison or an ordinary HUD frame budget.

Evidence: `.utmp/safe-engine71/unicode-soak/verification.json` and
`process-memory.json`; fresh consumer/export checks are in
`.utmp/safe-engine71/frontier-unicode-lifecycle/verification.json`. The executable
uses preview70 with the verified script-iterator-patched Windows release template
and embedded TextServer data. Stock Godot is not covered by this pass.

## Memory observer correction — 2026-09-08

The original soak retained an `Array[float]` entry for every frame. Its own
unbounded timing storage confounds the process-memory measurements below. They
remain valid observations of the whole test process, but cannot establish UI
memory growth on their own.

The fixture now uses 10,001 fixed 64-bit histogram counters (about 80 KB),
allocated before the soak starts. Count, mean and maximum remain exact; median
and p95 are upper bounds quantized to 0.01 ms. Quantiles in the overflow bucket
use the actual maximum conservatively and report the overflow count. An isolated
histogram check passes. The corrected 600-second run passes with 374,819 frames
and 200 recreate/reuse cycles; prepared and cold pixels match. Process-private
memory is 816.33 MiB at the first soak marker and 810.43 MiB at the last. The
node count stays at 496; observed object counts range from 1,945 to 1,968.
The previous apparent growth does not reproduce with fixed observer storage.
This establishes the observed ten-minute behavior, not an indefinite bound.
CPU validation jobs ran concurrently, so its frame intervals are not an isolated
performance comparison. Evidence: `.utmp/parity68/bounded-lifecycle`.
The run uses the control-text and synthetic-bold candidate (`705e0278…8baad00`);
the subsequent slider change removes default box decoration only.

## Runtime68 candidate — 2026-09-08

The isolated inline-bounds candidate (`58ab825f…60901a00`) passes a fresh Vulkan
1080p run with the deterministic animated 3D load, 200 recreate/reuse cycles and
a 120-second soak (86,258 frames). Prepared and cold pixels match exactly.
First document construction costs 58.963 ms CPU; hidden warm preparation costs
17.015 ms. First prepared show costs 0.010 ms CPU / 0.924 ms through the draw.
Recreation p95 is 18.643 ms; reuse with hidden data changes is 0.591 ms CPU p95
and 1.646 ms through-draw p95. Through-draw values include scene/render work.
Prepare during loading and reuse the document for gameplay opening.

Node/object counts stay at 496/1,946 throughout the soak. Process-private memory
ends 29.42 MiB above its initial soak sample, with a single large increase near
79 seconds and a plateau afterward. This is not evidence of a continuing leak,
but the short run does not establish a long-term bound. The 600-second follow-up also passes (404,516 frames), with node/object counts
fixed at 496/1,944. Private process memory rises from 817.3 to 853.0 MiB across
that run, including one large allocation increase and several smaller increases.
This quantifies the observed growth but does not prove an indefinite memory bound.
Evidence: `.utmp/parity68/inline-lifecycle-long/verification.json` and
`process-memory.json`. These measurements precede the subsequent control-text
and synthetic-bold changes. Evidence: `.utmp/parity68/inline-lifecycle/verification.json` and
`process-memory.json`. Runtime67 remains installed.

## Historical runtime65 measurements

Measured 2026-09-07 with runtime65, Windows release exports, Godot 4.7.2,
Ryzen 7 9800X3D and RTX 5080. These are desktop results; lower-end physical
hardware and the final game remain unverified. Interaction CPU comparisons
and the binary hash are in [PERFORMANCE.md](PERFORMANCE.md).

## Animated 3D load

The same seeded 3D scene runs with UI disabled and enabled: 480 mesh instances,
96 moving instances, a moving camera, shadowed directional light and four
point lights. Final-frame visible-scene counters report 115,490 primitives,
1,126 draw calls on OpenGL and 402 on Vulkan. Canvas counters are separate.
This is a deterministic synthetic scene with real rendering, not a completed
survival game or a saturation test for this high-end GPU.

All 12 native runs pass: three per renderer at each actual UI/window resolution,
120 warmup and 600 measured frames for each of eight workloads. Renderer and
workload order alternate. `_process` and deferred bindings use their normal
schedule; the API-only drive timer therefore cannot represent their full cost.
The table uses medians of per-run mean **whole-frame intervals**, in milliseconds.
These include scene updates, UI, renderer work and scheduling/presentation waits.

| Workload | OpenGL 1080p | Vulkan 1080p | OpenGL 4K | Vulkan 4K |
| --- | ---: | ---: | ---: | ---: |
| UI hidden, processing disabled | 2.828 | 3.700 | 3.033 | 4.661 |
| Idle UI | 2.857 | 3.721 | 3.049 | 4.621 |
| Two meters/labels, simulated 10 Hz | 2.781 | 3.780 | 3.029 | 4.610 |
| Inventory reorder | 2.911 | 3.681 | 3.226 | 4.670 |
| Typing | 2.959 | 3.841 | 3.286 | 4.661 |
| Slider | 2.898 | 3.810 | 3.132 | 4.595 |
| Settings toggle | 3.278 | 3.794 | 3.493 | 4.724 |
| Hover | 2.815 | 3.725 | 3.174 | 4.597 |

VSync is disabled, simulation steps are fixed at 1/60 second and presentation
is uncapped in an offscreen window. Event rates above refer to simulation
steps. Scheduling noise sometimes makes an enabled case faster than its
disabled baseline; these differences are not promised FPS gains or a target
game budget. UI-disabled runs retain UI resources in memory.

Median of per-run mean viewport render times, **idle minus UI-disabled**,
in milliseconds. This isolates the reported rendering counters more closely
than subtracting full frame intervals:

| Renderer / resolution | Viewport CPU delta | Viewport GPU delta |
| --- | ---: | ---: |
| OpenGL 1920x1080 | 0.087 | 0.033 |
| Vulkan 1920x1080 | 0.005 | 0.011 |
| OpenGL 3840x2160 | 0.075 | 0.031 |
| Vulkan 3840x2160 | 0.006 | 0.022 |

Changing-frame settings-toggle whole-frame p95 is still worth budgeting:

| Resolution | OpenGL | Vulkan |
| --- | ---: | ---: |
| 1920x1080 | 7.637 | 5.877 |
| 3840x2160 | 8.094 | 6.513 |

These frame tails include the entire scene and platform waits. They cannot
be attributed wholly to the UI. The matched synchronous interaction pass
separately measures modal UI CPU at 2.39–2.63 ms p95.

Each workload asserts the intended controls and data. Captures are taken
outside the measurement window. Source, export and library hashes, raw runs
and screenshots live under `.utmp/runtime65/load-1920x1080/` and
`.utmp/runtime65/load-3840x2160/`.

## Cold opening and preparation: measurement scopes

The lifecycle entry starts before any UI is instantiated. Scene resource
loading is timed separately from document construction. Construction includes
instantiation, `_ready`, binding, layout/paint update and texture preparation.
The 3D world has already been created and drawn. Fresh-process tests do not
flush the operating system's file cache or the driver's shader cache.

A hidden first document measures moving this construction work into loading
time; hiding does not eliminate it. A second hidden preparation in the same
process additionally benefits from warmed font/resource/renderer caches, so
its construction time is not directly comparable to a fresh first document.

Reuse retains the same native document. The test hides and pauses its process,
publishes new health data and lets the deferred binding callback run. It then
times show/resume, binding flush and document update, and asserts the current
data and row order. Binding work that already ran while hidden is outside the
reopen CPU timer. This is a measured reopening policy, not a claim that all
hidden data updates are free.

`through_draw` ends at Godot's `frame_post_draw` signal and includes the
surrounding render-loop work. It is CPU-side frame submission completion,
not a GPU-completion fence or proof that pixels have reached a display.
Cold/prepared PNGs must be byte-identical.

## Lifecycle and memory: measurement scopes

Each run warms 20 reuses/recreations, then performs the requested measured
cycles. Recreating destroys a view with a deferred binding update pending;
checks require its native UI weak reference to be dead, game-state signal
connections to be gone, and a subsequent signal emission to remain safe.
The sustained phase changes names/vitals, reorders inventory and opens/closes
settings while advancing the 3D world. All intended data is asserted.

The Python runner samples OS resident/private process bytes and records
Godot node/object/texture counters at lifecycle boundaries and during the
soak. Godot's static allocation monitor returns zero in this release template;
it is unavailable, not evidence of zero allocations. Process totals include
Godot, fonts, rendering caches, the 3D scene and the benchmark's growing array
of frame samples. Finite stable runs cannot prove the absence of every leak
or replace multi-hour testing on target devices.

## Results: cold opening and retained reuse

Twelve additional fresh-process checks pass at 1080p: three per renderer and
initial visibility mode, with renderer/mode order alternating. All prepared
screens match their cold captures exactly. Values below are medians of three
observations; ranges show every observed first-document CPU time. They are
small-sample startup measurements, not p95 or maximum-latency guarantees.

| Renderer / first UI mode | Scene resources, ms | First UI CPU, ms (range) | Show through draw, ms |
| --- | ---: | ---: | ---: |
| OpenGL / direct | 33.026 | 70.804 (61.490–71.509) | 3.190 |
| OpenGL / hidden | 33.949 | 61.891 (58.325–69.938) | 4.126 |
| Vulkan / direct | 29.169 | 60.136 (59.551–65.704) | 1.686 |
| Vulkan / hidden | 35.698 | 62.148 (57.056–66.034) | 1.707 |

The longer lifecycle runs observed first UI construction of 69.692–74.712 ms.
Resource loading adds another 28–36 ms in these tests. Prepare the screen
during loading and retain it; this remains too expensive to create afresh
inside a normal gameplay frame. Hidden preparation moves the cost and does
not establish a construction speedup. Showing a prepared first document
takes 0.016–0.017 ms CPU here, plus the render-loop work shown above.

Longer lifecycle results (milliseconds):

| Run | Measured reopens and recreations, each | Reopen CPU p95 | Reopen through draw p95 | Recreate CPU p95 |
| --- | ---: | ---: | ---: | ---: |
| gl_compatibility-1920x1080 | 200 | 0.374 | 3.408 | 32.390 |
| mobile-1920x1080 | 500 | 0.424 | 5.261 | 26.357 |
| mobile-3840x2160 | 200 | 0.365 | 3.631 | 20.720 |

The 900 measured reuses and 900 recreations all pass, in addition to warmups.
The three sustained phases total nine minutes. Reuse CPU remains well below
recreating a warmed document, whose p95 is still 20.720–32.390 ms.

## Results: memory and sustained updates

| Run | Soak duration / frames | Private memory, first → last soak marker (MiB) | Growth (MiB) | Nodes / objects throughout soak |
| --- | ---: | ---: | ---: | --- |
| gl_compatibility-1920x1080 | 120s / 40,483 | 365.52 → 368.08 | 2.56 | 496 / 1938 |
| mobile-1920x1080 | 300s / 259,564 | 808.70 → 818.39 | 9.69 | 496 / 1944 |
| mobile-3840x2160 | 120s / 104,256 | 998.65 → 940.55 | -58.10 | 496 / 1946–1947 |

Before/after the measured recreation phase, node and object counts are
identical within each run. After the final UI teardown, nodes return to 491
in all three runs. Process-private memory after recreation is lower than
before it in these runs. Sustained-process memory grows by 2.56–9.69 MiB at 1080p and falls
by 58.10 MiB in the 4K run; the frame-sample array and renderer caches are included. This is
evidence against accumulating live UI nodes on these paths, not a zero-growth
or leak-free guarantee. Multi-hour runs and target-hardware memory budgets
remain open.

Receipts, process samples and cold/prepared/soak PNGs:

- `.utmp/runtime65/lifecycle-gl_compatibility-1920x1080/`
- `.utmp/runtime65/lifecycle-mobile-1920x1080/`
- `.utmp/runtime65/lifecycle-mobile-3840x2160/`
- `.utmp/runtime65/cold-native/` (12 fresh-process runs)

## Original generic HUD cold fixture

The older approximately 68 ms figure was the core fixed-font HUD benchmark,
a different UI and timing scope from Frontier Camp. Remeasuring the same
`Tools/oracle/corpus/samples/hud.html` and `hud.css` with the current Windows
Release core gives **62.863 ms median of five run means**, each with 50 fresh
documents. Run means are 61.949, 78.751, 94.600, 62.863 and 60.552 ms. The
large variation and absence of a matched old-binary control preclude claiming
a reliable improvement over the historical 67.836 ms result. It is still a
loading-time cost.

The last cold allocation snapshot is 19,301 allocations / 46,784,583 requested
bytes; this is construction traffic, not retained memory or per-frame allocation.
Logs are `.utmp/runtime65/cold-core-1.log` through `cold-core-5.log`. Reproduce
using the current Release `weva_bench` executable:

```sh
weva_bench Tools/oracle/corpus/samples/hud.html \
  Tools/oracle/corpus/samples/hud.css 50 --cold
```
