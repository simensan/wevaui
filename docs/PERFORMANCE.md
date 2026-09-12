# Performance

## Runtime60: ordinary in-game UI (2026-09-07)

The Windows development project now uses the verified runtime60 DLL. This pass
targets retained gameplay UI: health/mana/stamina updates, labels, bindings,
menus, inventory and chat. Ordinary style/text/value/class writes no longer
flush the preceding write through layout and paint. Unchanged direct text and
serialized inline styles skip invalidation; idle updates retain the host's
texture map. Changed text still takes the conservative box-rebuild path.

Three alternating native A/B pairs compare the frozen release59 DLL with
runtime60 using Godot **4.7.2**, Vulkan Mobile, a Ryzen 7 9800X3D / RTX 5080,
1280x720, 120 warmup frames and 600 measured frames per workload. A/B order
reverses in alternate pairs. Own builds and tests do not overlap measurement;
other system load, including other Godot processes, is uncontrolled.

**CPU time includes the complete GDScript drive operation**, all setters and
updates it triggers, host texture preparation and events. It excludes the
later scheduled draw and GPU execution. Numbers below are medians across run
means and medians across run p95s, respectively.

| Runtime workload | Before mean | After mean | After p95 |
|---|---:|---:|---:|
| Idle HUD, 49 elements | 0.008 ms | 0.005 ms | 0.007 ms |
| Three HUD bars at 60 Hz, two labels at 10 Hz | 0.442 ms | **0.296 ms** | 0.875 ms |
| Unchanged HUD state sent every frame | 1.344 ms | **0.020 ms** | 0.026 ms |
| Three bars/two labels through `data` at 60 Hz | 0.581 ms | 0.594 ms | 0.969 ms |
| Menu hover at 10 Hz | 0.024 ms | 0.022 ms | 0.087 ms |
| Menu opacity transition | 0.087 ms | 0.075 ms | 0.128 ms |
| 96-slot inventory scrolling, 294 elements | 0.950 ms | 0.873 ms | 1.017 ms |
| Chat typing at 10 Hz, 40 retained messages | 0.066 ms | 0.055 ms | 0.284 ms |

Active HUD means improve **33%**, and redundant HUD writes improve **98.5%**;
both improve in all three pairs. Label-change frames specifically fall from
1.421 to 0.783 ms mean. Actual typing frames average 0.302 ms after the change;
the table's chat average also includes the frames between keystrokes. No
improvement is established here for data bindings or inventory scrolling.

Three separate headless host pairs confirm the HUD changes: active updates
average 0.296→0.173 ms and redundant writes 1.033→0.008 ms. Bound updates are
0.554→0.548 ms and scrolling 0.890→0.924 ms, consistent with neither path
receiving a demonstrated improvement. These dummy-renderer timings remain
separate from the native table (`.utmp/runtime60/headless-pairs/summary.json`).

Candidate active-HUD run means span 0.227–0.336 ms; bound-HUD means span
0.572–0.907 ms and inventory means 0.872–1.263 ms. The largest observed API
samples are 3.041 ms for the active HUD and 3.493 ms for inventory. These are
observations, not worst-case bounds or results for lower-end hardware.

Native whole-frame median run means are 0.830 ms for active HUD and 1.723 ms
for inventory, versus 0.475 ms for the empty window. This includes rendering
and scheduling waits; it is not a full game's frame time or a GPU cost obtained
by subtracting the empty-window result. First-run scheduling variation is
visible in the raw evidence. All **24 final UI PNG comparisons are identical**.

All nine Linux Release CTest targets pass. The full 47-sample mutation corpus,
core suite and allocation guards pass under ASan/UBSan. ASan's deliberate
overflow control initially timed out after reporting the overflow; the control
passes with `ASAN_OPTIONS=symbolize=0`, without changing instrumentation or
runtime behavior. All 25 Godot host suites pass **8,337 checks**. The new host
regression fails against release59 and passes with runtime60. Installation
retains a hash-checked preview56 backup and passes 348 actual-project host
checks plus the complete native workload benchmark. Its eight captures also
match the private candidate captures exactly. The release59 ZIP is unchanged;
these optimizations are in source and the installed Windows development DLL.

Evidence: `.utmp/runtime60/native-pairs/summary.json`, adjacent per-run JSON,
logs and PNGs, `core-tests.log`, `asan-tests.log`, `asan-control-recheck.log`,
`host-suites/result.json`, `install.json` and `installed-check/`.
DLL SHA-256: `4be4308824a9d3acf65e900ae29177401935d21f02680862d4f8c8cb261e8090`.
The [runtime benchmark guide](RUNTIME_PERFORMANCE.md) explains reproduction,
metrics and the remaining limits. Older installation notes below are historical.

## Preview56: input baseline verification (2026-09-07)

Windows preview56 is **packaged, export-verified and installed**, with a
hash-checked backup of preview53. This build fixes form
baselines, type-change invalidation, active-font centering and short-field
caret/selection painting. These are correctness changes.

Five alternating candidate55/candidate56 pairs use the same private Godot 4.7
Vulkan Mobile fixture, 240 warmups, 3,000 measured frames and a simulated 1/60
step. Headless pairs use 100 cold builds or 1,000 animated incremental updates.
No builds or other tests from this work overlap benchmarks; other system load
is uncontrolled.

| Layout-stress, median across run means unless noted | Candidate55 | Candidate56 |
|---|---:|---:|
| Headless cold document | 12.090 ms | 11.979 ms |
| Headless animated update | 2.440 ms | 2.483 ms |
| Native whole frame | 2.114 ms | 2.109 ms |
| Native core update | 0.814 ms | 0.824 ms |
| Native median run p95, whole frame | 3.765 ms | 3.807 ms |

Candidate native run means span **2.090–2.145ms**, versus 2.075–2.191ms before.
The largest observed frame is 7.440ms, versus 6.086ms before. The results show
similar performance; they do not establish a speedup or a worst-case bound.
They are standalone private-fixture measurements, not editor timings.

Cold allocation counts remain 44,586; requested bytes increase by 16, from
25,064,827 to 25,064,843, for the stored input-type identity in this fixture.
Animated allocation snapshots retain the same observed counts/bytes at
matching final animation states. Geometry, clip and draw-mesh guard digests
are unchanged. All nine CTest targets and 25 Godot host suites pass, and four
gallery PNGs remain byte-identical. Oracle findings remain in [ORACLE.md](ORACLE.md).

Evidence: `.utmp/input-baseline56/core-summary.json`,
`.utmp/layout-stress-41/input-baseline56-animated-summary.json`, adjacent raw
logs and `.utmp/input-baseline56/export-check.log`. Candidate DLL SHA-256:
`e1b123983bcd76c7177372957580af5c61d625afdac405a38593728d4b86d0a1`.

After installation, three runs in the actual development project average
**2.489–2.730ms**, median 2.512ms, with median run p95 4.984ms and largest
observed frame 9.240ms. These use 240 warmups and 1,500 measured frames with
the same fixed simulated step and Godot 4.7. The editor was closed. They are
separate installation checks, not paired evidence of a performance change.
The installed project also passes 245 host checks and reproduces the exact
layout-stress capture. Records: `.utmp/input-baseline56/after-summary.json`,
`install.json` and `verification.json`.

## Candidate55: border and button verification (2026-09-07)

Candidate55 remains **unpackaged and uninstalled**; the development project
still contains preview53. This candidate adds multicolor border mitres,
inset/outset shading and browser button defaults. It is a correctness change.

Five alternating candidate54/candidate55 pairs use the same private native
layout-stress fixture: Godot 4.7, Vulkan Mobile, 240 warmups, 3,000 measured
frames and a simulated 1/60 step. The headless pairs use 100 cold document
builds or 1,000 full-pipeline animated updates. Engine builds and other engine
tests did not overlap these benchmarks; system load was uncontrolled.

| Layout-stress, median across run means unless noted | Candidate54 | Candidate55 |
|---|---:|---:|
| Headless cold document | 12.723 ms | 12.724 ms |
| Headless animated update | 2.552 ms | 2.538 ms |
| Native whole frame | 2.779 ms | 2.354 ms |
| Native core update | 1.116 ms | 0.925 ms |
| Native median run p95, whole frame | 5.290 ms | 4.252 ms |

Native run means range **2.165–2.847ms** before and **2.227–3.146ms** after;
three pairs favor the candidate and two do not. The largest observed candidate
frame is 11.460ms, versus 20.015ms before. This variance does **not establish
a speedup**, and these are standalone private-fixture timings, not editor
measurements or worst-case bounds.

Cold allocation snapshots increase from 44,503 / 25,053,091 requested bytes
to 44,586 / 25,064,827 bytes with the additional UA rules. Animated snapshots
vary with the final animation state and are not per-frame memory bounds.
The multicolor geometry guard retains its ten-allocation budget. All nine
CTest targets, 24 Godot host suites and four byte-identical gallery PNG
comparisons pass. Oracle acceptance findings remain; see [ORACLE.md](ORACLE.md).

Evidence: `.utmp/button55/core-summary.json`,
`.utmp/layout-stress-41/button55-animated-summary.json` and adjacent raw logs.
Candidate DLL SHA-256:
`00482306c17c410c0a501d19ed3124ad9f8a9126392a50ae1e54afc9351f7462`.

## Candidate54: item containment verification (2026-09-07)

The flex/grid formatting-context correction is a **working candidate**, not an
installed preview. Preview53 remains in the development project. This change
fixes layout correctness; the measurements do not establish a speedup.

Five alternating preview53/candidate54 pairs use the private layout-stress
fixture. Native runs use Godot 4.7, Vulkan Mobile, 240 warmups and 3,000 measured
frames at a simulated 1/60 step. Headless runs use 100 cold document builds or
1,000 animated full-pipeline updates (`--full` means incremental C ABI updates,
not forced whole-document recomputation). No builds or other tests from this
work overlap these timings; other system load remains uncontrolled.

| Layout-stress, median across run means unless noted | Preview53 | Candidate54 |
|---|---:|---:|
| Headless cold document | 12.684 ms | 12.619 ms |
| Headless animated update | 2.515 ms | 2.516 ms |
| Native whole frame | 2.206 ms | 2.181 ms |
| Native core update | 0.867 ms | 0.845 ms |
| Native median run p95, whole frame | 4.009 ms | 4.011 ms |

The candidate's native whole-frame mean is lower in three of five pairs and
higher in two. Its largest observed frame is 6.605ms. Cold allocation counts
and bytes are identical; animated allocation snapshots vary with the final
animation state and are not a per-frame memory bound. Four gallery captures
remain byte-identical to preview53, and all nine CTest targets and 23 native
host suites pass. The original-capture oracle exposes a menu button-default
gap; acceptance remains pending, with the details in [ORACLE.md](ORACLE.md).

Evidence: `.utmp/oracle54/core-summary.json`,
`.utmp/layout-stress-41/item-context54-animated-summary.json` and adjacent raw
logs. Candidate DLL SHA-256:
`b8e322a260c53403e9203c6f826e775ec96910f79651dadab063b2fd6bfd5fee`.

## Preview53 Windows: gradient clamps and installed-project check (2026-09-07)

The development project now loads verified **Windows preview53**. This supersedes
the preview51 installation state below. The runtime change is two value clamps
in `background.cpp`: interpolation parameter conversion and final RGBA8 output.
MSVC 14.44 generated stack spills, pointer selection and a load for the previous
reference-returning `std::clamp`; equivalent value comparisons remove those
operations. Interpolation, sample count, texture resolution, compositing order
and byte rounding are unchanged. SIMD and two-stop search experiments were
discarded after inconsistent or slower results.

Five alternating pairs compare frozen preview51 with preview53 on a Ryzen 7
9800X3D. Headless runs build 50 fresh HUD documents; native runs open 40 fresh
HUD documents through the gallery with Godot 4.7, Vulkan Mobile/RTX 5080,
1520x800 gallery and 1280x720 document, at fixed simulated step 1/60. Own builds
and tests do not overlap benchmarks; other system activity is uncontrolled.

| HUD cold, median of run means | Preview51 | Preview53 | Faster pairs |
|---|---:|---:|---:|
| Headless | 72.512 ms | 67.836 ms | 5/5 |
| Native gallery | 89.120 ms | 84.159 ms | 2/5 |

The headless reduction is **6.4%**. Native timings varied substantially, with
run means spanning 74.259–100.164 ms before and 71.857–107.614 ms after; the
native result **does not establish a reliable speedup**. Last-pass headless
allocation snapshots remain 19,012 allocations / 46,712,456 requested bytes.
These snapshots are not bounds on retained memory.

Two 100-pair raster comparisons preserve every pixel. In the second, HUD body
median raster time falls 9.746→8.641 ms (90/100 pairs faster), aurora
10.121→9.191 ms (89/100), minimap 14.587→13.266 ms (91/100), and the hint fixture
0.946→0.837 ms (92/100). Conic cooldown results are less consistent. A broader
frozen-source comparison covers **864 cases / 15,904,512 identical bytes**,
including transparent stops, hints, hard stops, all gradient kinds, repetition,
positioned partial tiles and image layers; digest `50326d33b9e039d9`.

All **nine CTest targets** pass, including **616,389 core checks** and the full
incremental mutation corpus. The native host passes 245 checks. HUD,
layout-stress, stats and landing PNGs match preview51 exactly. The new ZIP passes
fresh-project, PCK and native debug/release/embedded export checks, including
relocation, source hiding and exact example pixels. Linux and sanitizer checks
for changes after preview40 remain pending; preview40 remains the latest
combined-platform package.

No Godot process was running at installation. Preview51 was backed up to
`.utmp/hud-raster53/weva_godot-before-preview53.dll`, and preview53 was staged,
hash-verified and atomically installed without reloading an editor. The actual
project then passes 245 host checks and the exact layout-stress capture.
Three standalone layout-stress runs, each with 240 warmups and 1,500 measured
frames, give whole-frame means **2.125, 2.164, 2.191 ms** and core update means
0.825, 0.834, 0.851 ms. Median run p95 is **3.995 ms**; largest observed frame
is **6.361 ms**. These verify current performance, not a paired improvement
over preview51 or an editor frame-time bound. `frameprobe.gd` retains its
pre-existing user edits.

Evidence, raw logs, disassembly, rejected experiments, frozen comparison source,
package and guarded installation/restore helpers are in `.utmp/hud-raster53/`.
Artifact: `weva-godot-windows-preview53.zip`, **19 files / 2,138,475 bytes**.
ZIP SHA-256: `3a4f6ea7fe4d9980c51926e306fe62c5df089a18b4b34abfcd8891654f061df5`.
Installed DLL SHA-256: `102e8e829900dbc9683d0e052b89759f01311b0eb56a029c1d43502d6ad35762`.

## Development project updated to preview51 (2026-09-07)

At this checkpoint the development project loaded verified Windows preview51. No Godot
process was running before measurement or at replacement time, so no editor
reload was performed. The older DLL was backed up, then the verified replacement
was staged and installed with an atomic rename. Its SHA-256 matches the already
tested preview51 package:
`e94b27afa54b11dd8c0e83a33bcde49409bb7ba11d87e327c7359f8a9ef629e5`.

The comparison runs the actual `hosts/godot/project`, with its gallery
and `layout_stress_probe.gd`. There are three runs before replacement followed
by three runs after replacement, **not interleaved pairs**. Each uses 240
warmups and 1,500 measured frames at a fixed simulated step of 1/60. Godot 4.7
standalone, Vulkan Mobile/RTX 5080, Ryzen 7 9800X3D, 1520x800 gallery and 1280x720
document; vsync is disabled by the probe. No builds or tests from this work
overlap the timings. Other system load is uncontrolled, and the editor UI is
not part of these runs.

| Actual project, layout-stress | Older installed DLL | Preview51 |
|---|---:|---:|
| Median run mean, whole frame | 18.530 ms | 2.668 ms |
| Median run mean, core update | 11.937 ms | 1.103 ms |
| Median run p95, whole frame | 26.362 ms | 5.005 ms |
| Largest observed frame | 38.911 ms | 10.334 ms |

Before-run frame means are 22.537, 17.301 and 18.530 ms; after-run means are
2.668, 3.196 and 2.541 ms. Every older-DLL run contains frames above 30 ms;
none of these new-DLL runs does. The roughly **86% lower median frame mean**
reflects the accumulated changes between the old installed library and
preview51, not the line-height fix alone. This confirms the stale development
DLL was materially affecting the reported layout-stress performance; it does
not establish a worst-case frame-time bound.

The installed project then passes **245 host checks** and **2,000 line-height
checks**. Its layout-stress capture matches the verified preview51 image exactly:
`1d717425618f1c9dc53479e59ac4b182fd6cacee1bdd529c8e615e7d4af6c392`.
Only the Windows addon DLL was replaced; `frameprobe.gd` retains its preceding
user edits. The previous DLL remains at
`.utmp/project-update52/weva_godot-before-preview51.dll`, SHA-256
`821f143ef5641ec8600f0ea88af5793143af505dedfcba75544ac277a0cf225b`.
The guarded `install.py --restore` helper in that folder can restore it only
while the target still matches preview51. Evidence, install metadata and raw
logs are in `.utmp/project-update52/`. This installation produces no new binary
version: the package at that checkpoint remained Windows preview51.

## Preview51 Windows: computed line-height inheritance (2026-09-07)

This is a correctness change. Inherited percentage/em line heights now use the
declaring element's computed font size, while unitless numbers and numeric
calc/min/clamp expressions use the consuming element's font size. Explicit
inherit/unset/rollback and pseudo values retain their source; changes to that
source invalidate the line-height property even when raw CSS text is equal.
Normal line-height still uses the existing font metrics. No new used-value
cache is introduced. See [ORACLE.md](ORACLE.md) for browser rules and scope.

Chrome 152 passes **508 computed-value checks**. A native fixture covers
**400 states** across both fonts, block/contents ancestors, parent font changes,
own/inherit/unset declarations, pseudo text and subsequent flow. Preview50
fails **960 of 2,000 headless checks**; preview51 passes **2,400 rendered checks**,
including exact pixels against independent explicit-pixel line-height controls.
All **nine CTest targets** pass, including **616,389 core checks** and the full
incremental mutation corpus. All **22 Godot host suites / 4,035 checks** pass.

Five alternating pairs compare frozen preview50 and preview51 on the Ryzen 7
9800X3D. Headless runs use 100 cold documents or 1,000 animated `--full` updates
at step 1/60. Native runs use Godot 4.7, Vulkan Mobile/RTX 5080, a 1520x800
gallery with a 1280x720 document, 240 warmups and 3,000 measured frames at step
1/60. No builds/tests from this work overlap benchmarks; other activity is
uncontrolled. Results do not demonstrate a performance improvement:

| Median of run means | Preview50 | Preview51 | Faster pairs |
|---|---:|---:|---:|
| Headless cold | 12.534 ms | 12.577 ms | 2/5 |
| Headless animated | 2.518 ms | 2.523 ms | 4/5 |
| Native whole frame | 2.234 ms | 2.233 ms | 3/5 |
| Native core update | 0.877 ms | 0.889 ms | 2/5 |

Median native run p95 is **4.105 -> 4.116 ms** whole frame and
**2.415 -> 2.413 ms** core. Largest observed frames are 6.977 ms and 6.635 ms,
not worst-case bounds. Cold allocation snapshots are unchanged at
**44,503 / 25,053,091 requested bytes**. Animated final-pass median counts/bytes
are **3,255 / 6,656,969 -> 3,290 / 6,687,771**. Individual allocation counts vary
from 3,255–3,315 in the baseline and 3,255–3,290 in the candidate; these snapshots
are not whole-program memory bounds.

HUD, layout-stress, stats and weva-landing native captures remain byte-identical
to preview50. Official Windows Godot 4.7.2 fresh-project import, PCK and relocated
debug/release/embedded exports pass, with all three native example captures
matching the project. The development project still contains its older DLL with SHA-256
`821f143ef5641ec8600f0ea88af5793143af505dedfcba75544ac277a0cf225b`.
Its reported 30ms editor case has not been revalidated with the new build.
The historical harvest form/font calibration differences remain separate work.
Linux/sanitizer verification is pending for previews41–51; preview40 remains
the latest combined-platform package.

Artifact: `.utmp/line-height51/weva-godot-windows-preview51.zip`, 19 files,
2,138,584 bytes; SHA-256
`0714756e2bb2f7d086d0f9f936bead447421af81b0d43f0f65933cadeccf20c4`.
DLL SHA-256:
`e94b27afa54b11dd8c0e83a33bcde49409bb7ba11d87e327c7359f8a9ef629e5`.
Evidence: `.utmp/line-height51/` and
`.utmp/layout-stress-41/line-height51-animated-*`.

## Preview50 Windows: mesh ownership at submission (2026-09-07)

The refreshed layout-stress allocation profile identifies vertex/index copies
inside `draw_mesh`. All 29 callers build temporary meshes; submission now
consumes them, applies colour filters/transforms/opacity in place, and moves
the buffers into the backend. Clipping still uses the same operation order and
interpolation. The backdrop region consumes its temporary shape too. Input
text decoration width is measured before moving its glyph mesh, preserving
the original untransformed width and draw order. Retained cache keys and the
public rendering API are unchanged.

The new ownership/allocation guard passes **56 rendering cases / 36,027 checks**,
covering combined effects, nested clips, text, input edit markers/decorations
and an earlier backend's buffers surviving subsequent paints. Frozen preview49
fails the three solid-quad allocation budgets; preview50 needs only the two
original mesh buffers. Both produce digest **61ef3248558085fe**, including draw
order, vertex/index data, backdrop parameters, scissors and generated textures.
This is a same-platform differential check, not a cross-platform golden.

Five alternating pairs compare against frozen preview49. Headless runs use
100 cold documents or 1,000 animated `--full` updates at step 1/60. Native runs
use Godot 4.7, Vulkan Mobile/RTX 5080 on the Ryzen 7 9800X3D, a 1520x800 gallery
and 1280x720 document, 240 warmups and 3,000 measured frames at step 1/60.
No builds or tests started by this work overlap the benchmarks; other system
activity is uncontrolled.

| Median of run means | Preview49 | Preview50 | Faster pairs |
|---|---:|---:|---:|
| Headless cold | 12.600 ms | 12.736 ms | 1/5 |
| Headless animated | 2.527 ms | 2.491 ms | 5/5 |
| Native whole frame | 2.258 ms | 2.287 ms | 1/5 |
| Native core update | 0.905 ms | 0.917 ms | 1/5 |

Native frame p95 improves in three pairs, ties in one and regresses in one;
core p95 improves in all five. Median run p95 is **4.138 -> 4.138 ms** whole
frame and **2.464 -> 2.441 ms** core. The largest observed frames are
6.532 ms and 6.349 ms respectively, not worst-case bounds. Cold and native
mean timings regress slightly; this change demonstrates reduced allocation
traffic, **not a reliable overall frame-time improvement**.

Median final-pass animated allocations/bytes fall
**3,796 / 7,408,056 -> 3,290 / 6,687,771**: 13.3% fewer allocations and
9.7% fewer requested bytes. Cold counts/bytes fall
**45,057 / 25,807,328 -> 44,503 / 25,053,091**. One baseline animated sample
has 3,821 allocations; these final-pass snapshots are not memory bounds.
Moves retain the builders' existing reserved capacity instead of producing
size-only copies, so reduced requested bytes do not measure retained/peak
memory. Separate instrumented 20-update profiles fall
**4,188 / 8,083,540 -> 3,647 / 7,224,925**; submission copies leave the leading
stacks. Instrumented timing includes stack capture and is not latency evidence.

All **nine CTest targets** pass, including **615,475 core checks**, and all
**21 Godot host suites / 2,035 checks** pass. HUD, layout-stress, stats and
weva-landing native PNGs match preview49 exactly. Official Windows Godot 4.7.2
fresh-project import, PCK and relocated debug/release/embedded exports pass;
each exported example capture matches the project. Linux and sanitizer
verification remain pending for previews41–50. The development project's DLL
still has SHA-256 `821f143ef5641ec8600f0ea88af5793143af505dedfcba75544ac277a0cf225b`;
the user's reported 30ms editor case remains unverified with this build.

Artifact: `.utmp/mesh50/weva-godot-windows-preview50.zip`, 19 files,
2,138,306 bytes; SHA-256
`4f787dfd22a22b3c802a72de698ae242ce3d8a1991c7c4e988b1879e0774e7df`.
DLL SHA-256:
`1ff1dd70f6eae8bb92b09c1d98045fcd4f5a73e588ccc905bfb6ffedcc54a0e3`.
Evidence: `.utmp/mesh50/` and `.utmp/layout-stress-41/mesh50-animated-*`.
Preview40 remains the latest combined-platform package.

## Preview49 Windows: clip preparation buffers (2026-09-07)

Clip preparation now compacts its owned polygon copy in place when removing
consecutive duplicates, reserves the maximum triangle-piece count before ear
clipping, and reserves rounded outline points from the clamped corner radii.
The original polygon, triangulation order and clipping interpolation are
preserved. Preparation stays lazy at the existing clip boundary; there is no
new cross-frame cache or invalidation key.

The dedicated allocation guard exercises **1,076 polygons**, including both
windings, concave/degenerate shapes, duplicate and near-duplicate points,
repeated preparation and clipping of attributed vertices. Frozen preview48
fails **2,155 allocation-budget checks**, while the candidate passes. Both
produce geometry digest **b9e74ff454c5008e**, including prepared metadata and
clipped vertex/index data. The digest is compared between builds on the same
platform, not hardcoded as a cross-platform trigonometry golden.

Five alternating native pairs use Godot 4.7, Vulkan Mobile/RTX 5080, a 1520x800
gallery and 1280x720 document, 240 warmups and 3,000 measured frames at step
1/60. Native median run means are **2.713 -> 2.478 ms** whole frame (4/5 pairs
faster) and **1.132 -> 1.020 ms** core (3/5 faster, one equal at reported
precision). Baseline frame means vary from 2.167 to 2.781 ms. All five p95
frame measurements improve; the largest observed frames are 10.049 ms and
7.138 ms, respectively. These are observations, not a worst-case bound.

The first headless comparison regresses and is retained in the evidence:

| Median of run means | Preview48 | Preview49 | Faster pairs |
|---|---:|---:|---:|
| Headless cold, unrestricted affinity | 14.403 ms | 31.860 ms | 2/5 |
| Headless animated, unrestricted affinity | 2.721 ms | 5.577 ms | 1/5 |
| Headless cold, same CPU | 13.459 ms | 13.485 ms | 4/5 |
| Headless animated, same CPU | 2.786 ms | 2.758 ms | 3/5 |

The diagnostic rerun pins only owned benchmark children to CPU0, selected from
the allowed mask `0xffff` on the Ryzen 7 9800X3D. Each run still uses 100 cold
documents or 1,000 animated `--full` updates. Whole-process CPU time, including
startup and teardown, has median **1,375 -> 1,265.625 ms** for cold runs and
**2,359.375 -> 2,484.375 ms** for animated runs. The latter is worse even though
wall means are nearly flat. The cause of the unrestricted timing variation
was not isolated; these results do **not** establish a reliable overall latency
gain. Builds/tests started by this work do not overlap benchmark runs, but
other system activity is uncontrolled.

A focused same-CPU benchmark measures 12,000 calls per operation/run on the
same 36-point rounded rectangle. Five alternating pairs, equal output checksums:

| Median time per operation | Preview48 | Preview49 | Faster pairs |
|---|---:|---:|---:|
| Rounded outline | 0.760 us | 0.439 us | 5/5 |
| Fresh clip preparation | 5.225 us | 4.579 us | 4/5 |
| Repeated clip preparation | 4.749 us | 4.417 us | 5/5 |

The change is retained for this measured preparation improvement and reduced
heap traffic. In the unrestricted full-update samples, median final-pass
allocations/bytes fall **4,776 / 7,689,640 -> 3,796 / 7,408,056** (20.5% fewer
allocations). Cold counts/bytes fall **46,037 / 26,088,912 -> 45,057 / 25,807,328**.
The same-CPU samples vary in allocation history (median 4,801 -> 3,786); these
final-pass counts are not whole-program memory bounds. Separate instrumented
20-update profiles fall **5,108 -> 4,188 allocations**; preparation growth
leaves the leading sites. Stack-capture timings are not latency evidence.

All **eight CTest targets** pass, including **615,475 core checks** and the new
clip guard. All **21 Godot host suites / 2,035 checks** pass. HUD, layout-stress,
stats and weva-landing native captures match preview48 exactly. Official
Windows Godot 4.7.2 fresh-project import, PCK and relocated debug/release/
embedded exports pass with matching example pixels. Linux and sanitizer
verification remain pending for previews41–49. The development project still
contains its older DLL; installation/reload and the reported 30ms editor case
remain pending.

Artifact: `.utmp/clip49/weva-godot-windows-preview49.zip`, 19 files,
2,138,377 bytes; SHA-256
`acdfdedb19125a40aee89f337e76a45d41de29e9736d121dd8be6e56b641431e`.
DLL SHA-256:
`fa070a7011c33d85af0bf58ff41fb10649c73138be5481e613b100631951e8e8`.
Evidence: `.utmp/clip49/` and `.utmp/layout-stress-41/clip49-animated-*`.
Preview40 remains the latest combined-platform package.

## Preview48 Windows: cached corner-radius parsing (2026-09-07)

A refreshed preview47 allocation profile identifies repeated corner-radius
parsing after child-order buckets leave the leading sites. Painting now reads
the longhand's existing `ComputedStyle::parsed` entry and resolves its one or
two components through `resolve_length_value`. Only syntax is cached; used
radii still consume the current box size, font/root/viewport/line-height/DPI
inputs. Style writes, unset and clear use the existing invalidation contract.
Initial zero radii avoid populating parse-cache entries.

The shared CSS value parser also fixes radius pairs separated by tabs,
newlines or comments, which the former literal-space split could turn into
square corners. The dedicated **6,823-check** guard covers these values, live
length contexts, style mutation/move/clear and allocation budgets. Frozen
preview47 fails **1,215 checks**; the candidate passes. Chrome 152 passes
**52 computed-value comparisons**; details and the spec link are in
[ORACLE.md](ORACLE.md#corner-radius-parsing-2026-09-07).

Five alternating pairs compare preview47 and preview48. Native runs use Godot
4.7, Vulkan Mobile/RTX 5080, a 1520x800 gallery and 1280x720 document, 240 warmups
and 3,000 measured frames at simulation step 1/60. Headless runs use 100 cold
documents or 1,000 animated `--full` updates with the core's fixed font:

| Median of run means | Preview47 | Preview48 | Faster pairs |
|---|---:|---:|---:|
| Native whole frame | 2.186 ms | 2.164 ms | 5/5 |
| Native core update | 0.893 ms | 0.861 ms | 5/5 |
| Headless cold document | 12.469 ms | 12.032 ms | 5/5 |
| Headless animated update | 2.694 ms | 2.456 ms | 5/5 |

The native whole-frame gain is small. All five native p95 frame measurements
improve, but the largest observed frame is **6.568 ms** in preview48 versus
6.521 ms in preview47. Benchmarks do not overlap this work's builds/tests;
external system load remains uncontrolled. These runs do not verify the
reported 30ms editor case with the newer library: the development project
still has its older DLL, and installation/reload remains pending.

Median final-pass animated allocations fall **8,160 -> 4,776** (41.5%), with
requested bytes **7,786,984 -> 7,689,640**. Baseline counts vary 8,125–8,160,
candidate counts 4,741–4,776; sampled final-pass allocations are not a bound
on whole-program memory. Cold counts/bytes fall
**47,801 / 26,139,504 -> 46,037 / 26,088,912**. Instrumented 20-update symbol
builds show **8,661 -> 5,108 allocations**, and radius parsing leaves the
leading stacks. Those timings include stack capture and are not latency data.

All **seven CTest targets** pass, including **615,475 core checks**, radius,
paint-order, geometry and style-lookup allocation guards, and exact blur
comparisons. Added incremental radius/box/font mutations agree with fresh
rebuilds. All **21 Godot host suites / 2,035 checks** pass. HUD, layout-stress,
stats and weva-landing native PNGs match preview47 exactly. Official Windows
Godot 4.7.2 fresh-project import, PCK and relocated debug/release/embedded native
exports pass; their example captures match the project. Linux and sanitizer
verification remain pending for previews41–48.

Artifact: `.utmp/paint-profile48/weva-godot-windows-preview48.zip`, 19 files,
2,138,268 bytes; SHA-256
`7222f1e3968f23a9a6d98e81a18dca2e20df7c92f8dea7bd02ff15e79aee29cf`.
DLL SHA-256:
`e613d5777bce9a9cca6c5bae14fb43f0c33130a0e8967b9a4151c2f387063d4b`.
Evidence: `.utmp/paint-profile48/` and `.utmp/layout-stress-41/radius48-animated-*`.
Preview40 remains the latest combined-platform package.

## Preview47 Windows: child paint-order traversal (2026-09-07)

The preview46 allocation profile showed temporary child-order buckets among
the most frequent paint allocations. `ChildPaintOrder` now checks the current
sibling sequence and walks the existing links when they already follow paint
order. Reordering uses one entry vector and an explicit tree-sequence tie-breaker,
replacing four buckets, stable-sort scratch and an output vector. Painting and
reverse hit testing share this view. There is no cross-frame order cache or
change to layout/paint invalidation keys.

Five alternating pairs compare preview46 and preview47 with the same native
gallery setup as below: Godot 4.7, Vulkan Mobile/RTX 5080, 1520x800 window,
1280x720 document, 240 warmups and 3,000 measured frames at simulation step
1/60. Headless runs use 100 cold documents or 1,000 animated `--full` updates:

| Median of run means | Preview46 | Preview47 | Faster pairs |
|---|---:|---:|---:|
| Native whole frame | 2.921 ms | 2.484 ms | 5/5 |
| Native core update | 1.281 ms | 1.060 ms | 5/5 |
| Headless cold document | 12.273 ms | 12.018 ms | 5/5 |
| Headless animated update | 2.775 ms | 2.768 ms | 4/5 |

Native baseline run means vary from **2.480 to 3.257 ms**, versus 2.444–2.612 ms
for the candidate; the apparent native gain should be read with that variation.
The largest observed frame is 12.151 ms in preview46 and 7.577 ms in preview47.
Headless animated latency is effectively flat. Benchmarks do not overlap this
work's builds/tests, but external system load is uncontrolled. The development
project still has its older DLL; these runs do not close the reported editor
30ms issue or establish a worst-case latency bound.

Median final-pass animated allocations fall **8,969 -> 8,160** (9.0%). Requested
bytes are **7,776,802 -> 7,786,984**, slightly higher in these sampled passes.
Baseline counts range 8,969–9,029 and candidate counts 8,125–8,160; these final
pass samples are not whole-program memory bounds. Cold allocations/bytes fall
**48,705 / 26,161,588 -> 47,801 / 26,139,504**. The dedicated ordering guard
verifies zero allocations for ordinary forward/reverse traversal and hit testing
with up to 4,096 children, zero for already-ordered mixed stacking groups, and
one when sorting is needed.

All **six CTest targets** pass: **615,403 core checks**, the new **113,803-check
paint-order/allocation guard**, style lookup, blur variants and geometry budgets.
The guard covers shuffled siblings, negative/positive/tied z-index, positioned
auto/zero, static flex/grid items, null styles, inline kinds and hit targets.
All **21 Godot host suites / 2,035 checks** pass. HUD, layout-stress, stats and
weva-landing native PNGs match preview46 exactly. Official Windows Godot 4.7.2
fresh-project import, PCK and relocated debug/release/embedded native exports
pass, with all native example captures matching the project. Linux and
sanitizer verification remain pending for previews41–47.

Artifact: `.utmp/paint-order47/weva-godot-windows-preview47.zip`, 19 files,
2,138,184 bytes; SHA-256
`61268722a2c5bb39caa2f8c7a515a959d39ecebc39d7fd8daca6910b8b5737ee`.
DLL SHA-256:
`ae5f724fb4fe4ec43f0bf9fb479dd48c95752c02de63d93216be5fe7fea53c1d`.
Evidence: `.utmp/paint-order47/` and
`.utmp/layout-stress-41/paint-order47-animated-*`. Installation/reload remains
pending; preview40 is still the latest combined-platform package.

## Preview46 Windows: geometry buffer growth (2026-09-07)

`WEVA_REUSE_LOG=1` now names the inputs that reject retained paint at a layout
boundary. On layout-stress, the grid survives layout but its position changes
from **(29, 278.8) to (29, 278.80576)** on the next animated frame, together
with its rounded ancestor clip. Repaint is required; the trace does not support
weakening the reuse key. An allocation-stack profile instead identifies
repeated vertex/index buffer growth in rounded fills and text generation.

`Mesh::reserve_append` reserves known vertex/index counts before emission and
keeps geometric capacity growth across successive appends. Rectangles, rounded
fans, borders and mesh concatenation use it. Text resolves atlas slots in
64-glyph stack batches, reserves only visible quads and then emits in the same
order. Empty glyphs still advance the pen and long whitespace runs allocate
no geometry. Slot pointers remain local to the call; no cache or version key
changes. Clipping, sample positions, vertex values and ordering are preserved.

Five alternating pairs compare preview45 and preview46. Native runs use Godot
4.7, Vulkan Mobile/RTX 5080, 1520x800 gallery, 1280x720 document, 240 warmups
and 3,000 measured frames at fixed simulation step 1/60. Headless runs use the
core's fixed font, 100 cold documents or 1,000 animated `--full` updates per run:

| Median of run means | Preview45 | Preview46 | Faster pairs |
|---|---:|---:|---:|
| Native whole frame | 2.232 ms | 2.215 ms | 4/5 |
| Native core update | 0.932 ms | 0.917 ms | 4/5 |
| Headless cold document | 12.531 ms | 12.125 ms | 5/5 |
| Headless animated update | 3.021 ms | 2.787 ms | 5/5 |

The native mean gain is small. All five native p95 frame measurements improve,
but the largest observed frame is **7.614 ms** in preview46 versus 7.445 ms in
preview45. Benchmarks do not overlap this work's builds/tests; other system
load is uncontrolled. This is no guarantee against the reported editor's
30ms frames, and the development project still has the older DLL.

Median final-pass animated allocations fall **14,448 -> 9,004** (37.7%), with
requested bytes **9,909,333 -> 7,807,604**. Counts vary between 14,413/14,448
in the baseline and 8,969/9,004 in the candidate; their sampled animation state
is not used as a whole-program memory bound. Cold counts/bytes fall
**54,567 / 28,358,309 -> 48,705 / 26,161,588**. Separate instrumented 20-update
symbol builds show **15,402 -> 9,565 allocations**; rounded-fill and text-buffer
growth leave the top allocation stacks. Stack-capture timings are not used
as latency measurements. The small allocation difference first observed in
preview45 was not isolated separately.

Final Windows Release passes **615,403 core checks**, all five CTest targets,
the **139-check geometry allocation guard**, the **192-case blur comparison**
and 8,000 allocation-free style reads. The geometry probe linked against the
frozen preview45 library fails 133 budget checks but produces the same geometry
hash, **d1b787d255a9fd0c**. All **21 Godot host suites / 2,035 headless checks**
pass. Rendered inheritance, intrinsic sizing and font context suites add
**896 + 432 + 336 passing checks**, through both font backends.

HUD, layout-stress, stats and weva-landing native PNGs match preview45 exactly.
Official Windows Godot 4.7.2 fresh-project import, PCK and relocated native
debug/release/embedded exports pass; all three native example captures match
the project. Linux and sanitizer checks remain pending for previews41–46.

Artifact: `.utmp/layout-profile46/weva-godot-windows-preview46.zip`, 19 files,
2,139,134 bytes; SHA-256
`fabf46cff8efb67f47162add300b50dc7402a0297c8086c465b790257c282a98`.
DLL SHA-256:
`32a8a7ec6ed3a48c2be790f2c0c91ed31828a4ae9d45eaa31dde2ec6914861cf`.
Evidence: `.utmp/layout-profile46/` and
`.utmp/layout-stress-41/geometry46-animated-*`. Editor installation/reload
remains pending; preview40 is still the latest combined-platform package.

## Preview45 Windows: computed font inheritance (2026-09-07)

Nested relative font sizes now resolve along the complete DOM style chain.
Inherited sizes forward to the parent's computed result; they do not apply
the parent's raw `em` or percentage again. Explicit inheritance retains its
source in `ComputedStyle`, including generated content and CSS-wide rewrites.
Changing authored `2em` to inherited `2em` now changes the version and the
font-size cascade diff, preserving incremental layout. Empty inherited links
forward directly instead of checking a redundant memo at every ancestor.
This corrects a known C# divergence; see [ORACLE.md](ORACLE.md).

Five alternating Windows Godot 4.7 pairs measure 3,000 animated layout-stress
frames after 240 warmups, Vulkan Mobile/RTX 5080, 1520x800 gallery and 1280x720
document, fixed simulation step 1/60. The comparison is against preview44:

| Measurement, median of run means | Preview44 | Preview45 |
|---|---:|---:|
| Native whole frame | 2.705 ms | 2.885 ms |
| Native core update | 1.234 ms | 1.283 ms |
| Headless cold build, 100 documents/run | 12.979 ms | 12.949 ms |
| Headless animated update, 1,000 passes/run | 2.995 ms | 3.106 ms |

Results are mixed, not evidence of a speedup. Three of five native pairs are
faster, but the aggregate medians are worse and run means vary substantially.
The largest observed native frame is **19.716 ms** in the candidate versus
11.297 ms in preview44. An earlier candidate retaining inherited-link memos
measured **2.247 -> 2.259 ms** whole frame and **0.946 -> 0.956 ms** core.
No benchmark overlaps this work's builds/tests; other system load is
uncontrolled. These measurements do not establish a latency bound or prove
the reported editor's 30ms behavior resolved.

Cold allocation counts/bytes remain **54,567 / 28,358,309**. The median reported
last-pass allocation count in the animated-update bench rises **14,438 -> 14,448**,
while requested bytes fall **10,014,071 -> 9,909,333**. One candidate run reports
14,413 / 9,878,531. The source flag itself adds no separate heap allocation;
the changed update allocation pattern remains unprofiled. The correctness fix
is retained with that measured cost rather than described as an optimization.

Final Windows MSVC Release passes **615,403 core checks**, all four CTest
targets, 8,000 custom-property reads without allocations, and 192 exact
portable/SSE2 blur comparisons. Chrome 152 passes **134 inheritance checks**.
All **21 native host suites / 2,035 headless checks** pass. The new inheritance
scene passes **896 rendered checks** through both font backends, including
generated-content pixels and incremental/fresh parity; preview44 fails 108
of its 560 headless checks. It is now part of `check.sh`.

HUD, layout-stress, stats and weva-landing native PNGs match preview44 exactly.
Official Windows Godot 4.7.2 project import, packed resources and relocated
debug/release/embedded exports pass. All three native exports pass 13 runtime
and 17 example checks and match project pixels. Linux and sanitizer validation
remain pending for previews41–45; preview40 remains the latest combined package.

Artifact: `.utmp/nested-font/weva-godot-windows-preview45.zip`, 19 files,
2,138,275 bytes; SHA-256
`f28fbfa8cceadc7a0997bca96698f54820c6190f798c1c3ec1227b872acd25d6`.
DLL SHA-256:
`776213cd6668051e37fee98c2536482d821905de9a79deaf08860c8040a54fda`.
Evidence is in `.utmp/nested-font/` and
`.utmp/layout-stress-41/nested-font-{animated,final-animated}-*`.
The development project DLL remains unchanged; editor installation/reload
is still pending.

## Preview44 Windows: blur kernels and scratch buffers (2026-09-07)

`WEVA_BLUR_LOG=1` now separates buffer preparation, horizontal passes, vertical
passes and output conversion. The initial HUD filter diagnostic measured
**3.295 / 4.828 / 2.977 / 1.733 ms**, respectively, for a 1024x656 texture at
sigma 30.356. Processing four rows together improved one-channel shadows but
regressed the four-channel horizontal pass, so row interleaving is limited to
shadows. Scratch buffers now use owned arrays without value initialization:
conversion writes every input float, and each horizontal pass writes every
temporary float before the vertical pass reads it.

On SSE2 targets, the four-channel passes use explicit packed channel arithmetic.
They retain float subtraction, double running sums, sample order and exact
conversions; no approximate reciprocal or reassociation is introduced. Other
targets compile the portable loop. Intrinsic operations are documented in the
[Intel reference](https://www.intel.com/content/dam/develop/external/us/en/documents/18072-347603.pdf).
The `WEVA_BLUR_FORCE_PORTABLE` compile definition lets the test target build both
production paths together; its renamed kernel template prevents cross-linking
the implementations.

A private benchmark links frozen preview43 and current blur implementations in
one process. Each fixture alternates order for 100 pairs and compares all bytes,
including transparent RGB. Median kernel times, with logging disabled:

| Fixture | Preview43 | Preview44 | Faster pairs |
|---|---:|---:|---:|
| HUD filter, 1024x656 | 12.058 ms | 10.267 ms | 98/100 |
| Random RGBA, 513x257 | 2.559 ms | 2.151 ms | 90/100 |
| Shadow, 360x456 | 1.442 ms | 1.253 ms | 90/100 |
| Shadow, 128x128 | 0.180 ms | 0.162 ms | 86/100 |
| Shadow, 129x33 | 0.052 ms | 0.046 ms | 79/100 |
| Shadow, 1x41 | 0.023 ms | 0.016 ms | 99/100 |
| Shadow, 41x2 | 0.007 ms | 0.007 ms | 59/100 |

The buffer-only/interleaved-row candidate did not improve the warmed HUD filter
comparison (**12.576 -> 12.717 ms**); the retained four-channel gain includes
explicit SIMD. An instrumented final native first open measured 12.524 ms inside
blur, illustrating why diagnostic first-open numbers and warmed kernel timings
are not interchangeable.

Five paired native HUD runs, 15 fresh documents each, improve mean medians
**72.526 -> 71.457 ms**, with all five pairs faster. First-open medians are
**80.024 -> 78.785 ms**. The setup is Windows Godot 4.7, Vulkan Mobile/RTX 5080,
1520x800 gallery and 1280x720 document. Five paired headless runs, 50 documents
each, improve mean medians **70.037 -> 68.408 ms**, with four pairs faster; the
last candidate run is an outlier at 125.292 ms. Allocation counts remain
**22,428**, with **47,875,996 requested bytes** in the candidate versus
47,876,854 previously. Benchmarks do not overlap this work's builds/tests; other
system load is uncontrolled. The native gain is small and cold HUD still exceeds
a frame budget; these results do not establish a latency bound.

Windows MSVC Release passes **614,536 core checks**, including scalar blur
comparisons and additional row tails/radii. The separate **192-case blur-variant
test** compares normal and forced-portable output byte-for-byte and is included
in CTest and `check.sh`. All four CTest targets pass, as do all **20 Godot host
suites / 1,475 checks**. HUD, layout-stress, stats and weva-landing native PNGs
match preview43 exactly. Official Windows Godot 4.7.2 import, packed-resource and
debug/release/embedded export checks pass; each native export passes 13 runtime
and 17 example checks with exact project/example pixels. Linux and sanitizer
validation remain pending for previews41–44.

Artifact: `.utmp/blur-passes/weva-godot-windows-preview44.zip`, 19 files,
2,137,407 bytes; SHA-256
`75c1ddf9561c12dec45831048e11e800165f5581b027dd1e519dec97df3118f6`.
DLL SHA-256:
`e4833fa84c144e5b5b98b47d34d72ea871f7e158662eb0accb26e9ea2bce3db0`.
Evidence is in `.utmp/blur-passes/` and
`.utmp/layout-stress-41/blur-passes-native-*`. The development editor DLL has
not been replaced; reload remains pending.

## Preview43 Windows: gradient interpolation preparation (2026-09-07)

HUD's cold paint is now attributed with the existing filter sub-scopes:
one preview42 diagnostic run measured **74.17 ms** total, comprising backgrounds
**38.56**, filters **24.94**, shadows **6.93**, glyphs **1.62**, text **0.10**
and residual **2.02 ms**. The filter contains **10.71 ms** rasterization,
**13.79 ms** convolution and **0.25 ms** upload. Nested scopes must not be added
to their parent totals. The full script's cold-open duration also includes
font/host initialization outside paint.

`WEVA_GRADIENT_LOG=1` now adds elapsed time per rasterized texture. It identified
HUD's 518x110 minimap background at **18.79 ms** in a separate diagnostic run:
hard-edged repeating stripes require nine samples of all three layers, including
the radial and smooth linear layers. Preview43 prepares premultiplied endpoints,
their differences and constant-span colors once per gradient. Sampling density,
compositing order and scalar multiply/divide rounding remain unchanged. The
existing reciprocal array becomes a span array; no extra allocation is added.

A private benchmark links the frozen preview42 rasterizer beside the candidate
and alternates their order within one process, comparing every output byte.
Each fixture runs 100 pairs; these are texture costs, not Godot frame times:

| Texture | Old median | New median | Faster pairs | Pixels |
|---|---:|---:|---:|---|
| Body, 512x512, three layers | 11.423 ms | 11.451 ms | 48/100 | Exact |
| Aurora, 842x474, two layers | 13.134 ms | 13.166 ms | 47/100 | Exact |
| Minimap, 518x110, three layers | 22.192 ms | 20.892 ms | 79/100 | Exact |
| Cooldown, 157x80, conic | 3.141 ms | 3.200 ms | 39/100 | Exact |
| Hinted linear, 173x117 | 1.122 ms | 1.060 ms | 65/100 | Exact |

Whole-document evidence is mixed. Five paired headless runs of 50 fresh HUD
builds have mean medians **79.885 -> 80.918 ms**; three pairs improve and two
regress. Allocation counts remain **22,428**; requested bytes rise by **1,640**
to **47,876,854** for temporary span data. Five native pairs of 50 fresh documents
have mean medians **93.064 -> 76.219 ms**, but run means shift between roughly
75 and 96 ms in both builds and two pairs regress. First-open medians are
**86.507 -> 82.612 ms**. An earlier endpoint-preparation-only candidate was also
mixed (**75.586 -> 75.165 ms**, 15-document native runs). These results do not
establish a whole-document speedup. The retained gain is the smaller minimap
raster cost with exact pixels. Benchmarks run without overlapping this work's
builds/tests; other system load is uncontrolled.

Windows MSVC Release passes **614,330 core checks**, including exact comparison
with scalar interpolation for transparent, low-alpha, constant, hinted,
multi-stop and repeating gradients. All three CTest targets pass, including
the benchmark CLI and the 8,000-read allocation guard. All **20 Godot host
suites / 1,475 checks** pass. HUD, layout-stress, stats and weva-landing native
PNGs are byte-identical to preview42. Official Windows Godot 4.7.2 fresh import,
packed resources and debug/release/embedded exports pass; each native export
passes 13 runtime and 17 example checks, with exact project/example pixels.
Linux and sanitizer validation remain pending for previews41–43.

Artifact: `.utmp/gradient-raster/weva-godot-windows-preview43.zip`, 19 files,
2,136,960 bytes; SHA-256
`9c8c03a16876b187d306eef1a9cd42a6ebb127ce5ffc6f58b52e3e6cbf50b561`.
DLL SHA-256:
`10ade3c21acb9f1281e752a45166031f2dd5d14d7694252ba85c0e3e7f9ace28`.
Evidence: `.utmp/gradient-raster/`, `.utmp/cold-profile-42/`,
`.utmp/gradient-times-result.log` and
`.utmp/layout-stress-41/gradient-raster-final-native-*`.
Cold HUD remains well over a frame budget. The development editor still has
the older DLL; the pending reload has not been performed.

## Preview42 Windows: custom-property lookup (2026-09-07)

Name-based style reads previously constructed an owned `std::string` for every
ancestor's side-map search. The map now uses transparent comparison and accepts
the original `string_view`; get/contains walk the parent chain once without
repeating registry lookup. This adds no cache. Stored keys still own their data,
and case-sensitive names, empty shadowing values, live changes and parent changes
retain their behavior. C++ callers can name the map type with
`ComputedStyle::CustomPropertyMap`; the C ABI is unchanged.

The complete Windows MSVC Release suite passes **593,800 checks**. A separate
CTest/check.sh guard validates **8,000 reads, zero allocations** through 32
styles, including sliced long names, inherited hits, misses and presence checks.
A positive control verifies that allocations inside the linked core reach the
counter. All 20 native host suites pass. Hud, layout-stress, stats and
weva-landing native-font PNGs are byte-identical to preview41.

Five interleaved headless layout-stress pairs measure cold mean medians
**12.966 -> 12.683 ms** and final-build allocations **58,273 -> 54,567**
(**28,476,661 -> 28,358,069 requested bytes**). Cold builds use 100 passes at
time zero and exclude destruction. For 1,000 animated updates after the initial
update, mean medians are **3.090 -> 2.966 ms** and final-update allocation
medians **17,796 -> 14,448** (**10,121,447 -> 9,909,253 bytes**). Four cold
pairs and all five animated pairs improve. Some animated allocation counts
vary between runs in both builds; these are measured medians.

Native cold tests use 15 fresh documents per process. Median run means are
**17.226 -> 15.211 ms**, four of five pairs faster; first-open medians are
**20.203 -> 19.254 ms**. The initial 300-frame animated comparison regresses:
whole-frame mean medians **2.258 -> 2.360 ms**, core **0.971 -> 1.022 ms**.
That uncertainty prompted five longer pairs with 3,000 measured frames each.
Four longer pairs improve: whole-frame mean medians **2.296 -> 2.256 ms**,
core **0.991 -> 0.953 ms**. The largest new long-run frame is 7.182 ms.
Animated native runs use 240 warmups and fixed 1/60-second updates. Native tests
use Windows Godot 4.7, Vulkan Mobile on RTX 5080, and the same isolated
1520x800 gallery/1280x720 document. Benchmarks do not overlap this work's builds or tests; other system
load is uncontrolled. Both the short regression and longer result are retained.
The allocation reduction supports keeping the change; the timing evidence does
not establish a latency bound or resolve first-open cost and earlier long stalls.

The Windows-only package passes fresh import, packed-resource checks, and native
debug/release/embedded exports with official Godot 4.7.2 and matching templates.
Every export passes 13 runtime and 17 example checks after source hiding and
relocation. Example pixels match the project and preview41 exactly. Linux and
sanitizer checks have not been repeated for this core change.

Artifact: `.utmp/custom-lookup/weva-godot-windows-preview42.zip`, 19 files,
2,136,256 bytes; SHA-256
`1e7b6b9ef9623f14019e1112a989b9730a0d5949cce6be20a1cb649d39446aaf`.
DLL SHA-256:
`2c0931a2ab400416043ba44b5298d9e2ff4ec2e715f18293448ebf524e06f423`.
Core/native/export evidence lives in `.utmp/custom-lookup/`; native timing logs
are `.utmp/layout-stress-41/custom-lookup-native-*` and `custom-lookup-long-*`.
The repository editor DLL remains unchanged and reload approval is pending.

## Preview41 Windows: preserved-newline intrinsic sizing (2026-09-06)

The preformatted sizing issue recorded during preview38 is fixed in source.
Line boxes now retain whether they end at a preserved newline or `<br>`.
Intrinsic measurement uses that boundary instead of joining every line up to
the container's final line. Soft wraps still join when recovering max-content
width. The existing alignment/final-line flag keeps its previous behavior.
With 8px monospace advances, `aa bbbb\ncc` under `white-space:pre` now measures
**56px / 56px**, rather than 72px / 72px.

Before the fix, the new targeted cases fail 18 assertions. The final Windows
MSVC Release suite passes **593,783 checks, 0 failures**, including actual
inline-block/flex/grid widths and incremental whitespace changes compared
with fresh documents. A local Chrome check passes 24 intrinsic-width assertions
for normal, nowrap, pre, pre-wrap, pre-line, preserved blank lines, inline spans
and explicit breaks. This follows the forced-break distinction in
[CSS Text](https://www.w3.org/TR/css-text-3/#white-space-property) and
[CSS Sizing](https://www.w3.org/TR/css-sizing-3/#max-content).

Five interleaved Windows headless comparisons against preview40's core show
no material layout-stress timing change. Median cold run means are
**13.547 -> 13.488 ms**; median animated core means are **2.981 -> 2.993 ms**.
Cold work includes document creation, CSS/HTML loading, update and destruction
(5 warmups, 100 measured documents). Animated runs use 240 warmups and 1,000
fixed 1/60-second updates. Both use the default headless font, 1280x720 and
the same CSS/HTML. Cold allocations are identical at 58,393 calls / 28,479,477
requested bytes per document; animated medians are identical at 18,456.949
calls / 10,656,133.379 bytes per update, with variation between runs in both
builds. These are headless measurements, not rendered Godot frame times.

Helpers and raw evidence are in `.utmp/forced-break/`, including `before.log`,
`after.log`, `final-tests.log`, `chrome.json`, and `bench-summary.json`.
The canonical Windows host CMake build now includes the fix. Its new native
scene fails 30 assertions against preview40, then passes **336 headless** and
**432 rendered** checks against preview41, covering both font backends, live
text/whitespace changes, sibling positions, idle geometry and fresh-document
pixel parity. All **20 host suites / 1,475 headless checks** pass. The scene
is included in `check.sh`. Native-font captures of hud, layout-stress, stats
and weva-landing remain byte-identical to preview40.

Five paired native comparisons use Windows Godot 4.7 stable, Vulkan Mobile on
RTX 5080, a 1520x800 offscreen window and 1280x720 documents. Cold medians of
15-document run means are **16.642 -> 16.200 ms**, with two pairs faster and
three slower; first-open medians are **23.742 -> 22.247 ms**. Animated runs
use 240 warmups and 300 fixed 1/60-second frames. Median whole-frame means are
**2.252 -> 2.226 ms**, and median core means **0.963 -> 0.956 ms**. The largest
new animated frame is 5.959 ms. These mixed small differences do not establish
a speedup or a latency bound; cold opening and previously observed long stalls
remain work. Benchmarks do not overlap this work's builds/tests; other system
load is uncontrolled. Logs are `.utmp/layout-stress-41/forced-break-native-*`.

The Windows-only ZIP passes fresh-project import, packed resources and native
debug/release/embedded exports with official Godot 4.7.2 and matching templates.
Every native configuration passes 13 runtime and 17 example checks after source
hiding and relocation; each example PNG exactly matches the editor project.
The export check is unchanged except for a private wrapper directing engine logs
into its writable fixture. Native evidence is in `.utmp/forced-break/native/`,
including `export-check.log`, `host-suite/`, `new-rendered/`, and `pixels/`.

Artifact: `.utmp/forced-break/native/weva-godot-windows-preview41.zip`,
19 files, 2,136,532 bytes, SHA-256
`c38c57f42eeb04c89a922494d077e9e85c5777cfd98a9be880f58bb846b1526a`.
Its DLL SHA-256 is
`ab1ee695e8ead0ae2653996ed0027300af39621151f563a1d9678534b07c571c`.
Linux and sanitizer validation have not been repeated for this core change;
preview40 remains the latest combined Windows/Linux addon. The development
project's installed DLL is unchanged and editor reload remains pending.

## Windows core benchmark build and attribution (2026-09-06)

The standard MSVC core build previously failed in `weva_bench`: it included
`execinfo.h` and `sys/time.h` unconditionally and passed GCC-only options to
MSVC. The complete default Release target now builds all tools and tests.
Windows allocation profiles use CaptureStackBackTrace and DbgHelp; matching
RelWithDebInfo PDBs produce named frames, while Release can print addresses.
The existing SIGPROF time sampler remains POSIX-only and Windows rejects
`--sample` explicitly.

`--profile` now reports allocation sites in cold and full-update modes too;
those modes previously returned without an attribution report. Cold counts
cover the final fresh build through its initial update, excluding destruction.
Invalid sample depths, simultaneous time/allocation profiling, missing input
files and nonpositive pass counts fail explicitly. Timing output under
allocation profiling is labelled as including stack-capture overhead.

The new CTest CLI gate passes 15 invocation cases in both Windows Release and
RelWithDebInfo. It exercises every workload with and without profiling, checks
equal allocation counts/bytes, accepts an empty optional stylesheet, and checks
the invalid-input diagnostics. Testing is enabled before adding the tools so
CTest actually registers this gate. The POSIX branch has not been rerun here.

A named layout-stress cold profile records 58,273 allocations / 28,476,661
requested bytes in the final build at time zero. Its 778 stack groups include
temporary strings in recursive custom-property lookup, glyph-vector growth,
tessellation vertices and matched declarations. The largest individual group
contains 1,600 string allocations through `ComputedStyle::get`; source inspection
locates the temporary `std::string(property)` lookup on every custom-property
ancestor. These counts identify a candidate for measurement, not a time saving.
This work changes the tool, not the runtime addon.

See [benchmark usage](../tools/weva_bench/README.md). Evidence includes
`.utmp/windows-tools-before.log`, `.utmp/windows-tools-after.log`,
`.utmp/windows-bench-symbol-build.log`, and
`.utmp/windows-bench-named-profile-final.log`.

## Cold-layout follow-up: rejected inline style reads (2026-09-06)

A native stack sample of preview40 over 500 fresh document openings captures
2,701 stacks, including 668 in layout. Of those layout samples, 106 stop in
`ComputedStyle::get(int)`, the largest individual function count. Sampling
suspends only the test process's main thread, unwinds it with `StackWalk64`,
and resolves extension addresses against preview40's matching linker map.
These counts locate work; the instrumented run is not a latency benchmark.

An experiment moves the unchanged indexed `get`, `contains` and raw-value
accessors into the header for compiler inlining. It passes all **593,614**
checks in the Windows MSVC Release mutation suite, but five interleaved native
cold comparisons against preview40 regress: median run means **19.872 ->
21.287 ms**, with four pairs slower and one faster. First-open medians are
27.218 -> 24.600 ms. The aggregate cold result does not justify retaining the
change, so the runtime sources are restored. There is no new preview artifact.
The existing background test now includes `<algorithm>` explicitly: its
`std::clamp` calls previously prevented the complete suite from compiling
with MSVC. Test behavior is unchanged.
After restoring the runtime sources and rebuilding, the full MSVC Release
mutation suite again passes **593,614 checks, 0 failures**; its log is
`.utmp/style-read-host/restored-core-tests.log`.

A separate native font-preparation probe records 554 uncached shaping calls
across gallery startup and three stress documents. UTF conversion/byte-offset
preparation totals 0.329 ms, shaped-text RID creation 0.232 ms, and font-list
construction 0.529 ms. Font-list rebuilding is too small in this probe to
explain the earlier first-open spike. Per-call printing adds overhead outside
those sub-scopes; aggregate build/prepare timing from that probe is not a
release measurement.

Helpers, rejected sources/binary and logs remain under the repository's
`.utmp`: `sample-preview40-native.py`, `layout-stress-41/preview40-native-*`,
`profile-font-detail-run.py`, `layout-stress-41/font-prepare-detail.log`,
`style-read-rejected.*`, `style-read-host/`, and
`layout-stress-41/style-read-cold-*`. The native comparison uses the exact
preview40 Godot binding/ICU archives with their recorded flags and the rebuilt
core. No benchmark overlaps a build or test from this work; other system load
is uncontrolled. The installed Windows project DLL remains unchanged.

## Installed Windows DLL compared with preview40 (2026-09-06)

The development project still contains the older Windows DLL with SHA-256
`821f143ef5641ec8600f0ea88af5793143af505dedfcba75544ac277a0cf225b`.
Three interleaved comparisons against the verified preview40 DLL use the same
current gallery scripts, Windows Godot 4.7, Vulkan Mobile, RTX 5080, 1520x800
windows and 1280x720 documents. Each process warms up for 240 frames, then
measures 300 fixed 1/60 animation updates. All three pairs improve:

| Median of run means | Installed DLL | Preview40 |
|---|---:|---:|
| Whole frame | 15.849 ms | 2.561 ms |
| Core update | 10.094 ms | 1.117 ms |

Whole-frame run means span 14.603–16.194 ms for the installed DLL and
2.517–2.562 ms for preview40. Their worst measured frames are 33.401 and
9.405 ms respectively. This comparison measures the accumulated changes
between the installed binary and preview40; it does not attribute the whole
improvement to shared shaping. The project DLL has not yet been replaced.
Graphics load is uncontrolled, so these three runs do not establish a latency
bound or invalidate the previously observed long stalls.

A separate seven-pair comparison using the same preview40 DLL with shared
shaping disabled/enabled measures fresh-document mean medians of
18.025 -> 16.095 ms, with all seven pairs faster. First-open medians remain
21.824 -> 22.061 ms. That isolates the repeated-opening benefit without
demonstrating a first-open improvement. The previously completed logs are
`product-shape-switch-*` in the external Windows build directory.

The resumed instrumented cold run enables the existing `WEVA_LAYOUT_LOG`
sub-scopes. The first stress document spends 10.418 ms in layout flow:
5.828 ms inclusive inline work (3.869 ms in shaping), 1.600 ms box-model
resolution and 0.358 ms finalization. Subsequent fresh documents spend
6.689–7.422 ms in flow. These scopes overlap and instrumentation adds cost;
they are diagnostic attribution, not release timing.

The comparison helper and complete logs are retained in
`.utmp/layout-stress-current-comparison.py` and
`.utmp/layout-stress-41/installed-comparison-*` at the repository root.
The cold helper/log are `.utmp/layout-stress-resume.py` and
`.utmp/layout-stress-41/cold-layout-profile.log`. Tests run in a separate
project with a portable engine and explicit log paths. The sandbox prevents
reading Windows' certificate store; only that exact engine diagnostic is
excluded from the helper's error check. Earlier setup attempts reported denied
editor-cache/log paths, including a runtime crash before the engine banner.
After redirecting those paths the run succeeds; this does not diagnose that
initial crash. No product source changed in this run.

## Sharing immutable native shaped runs (2026-09-06)

Preview40 reuses shaped runs across documents that use the same immutable
synthetic primary font. A separate instrumented profile records **116 shared
hits**, reducing native shaping calls on later fresh stress documents from
**205 to 89**. Native glyph indices are remapped into each receiving backend's
handles. Exact file/synthesis inputs identify the font; exact source bytes,
rounded native size and the ordered immutable font chain identify a run.
Arbitrary resource fallback chains and runs containing fallback glyphs retain
normal shaping. Font changes still follow the existing invalidation path.

The extra retained storage is bounded per synthetic font: 128 runs, 4,096
allocated glyph slots, at most 512 source bytes and 512 glyphs per run, and
at most 64 input fonts. Individual LRU eviction preserves other stable labels.
The existing pool retains eight fonts; live documents may retain additional
font owners after pool eviction. Native allocation counts were not measured.
`WEVA_GODOT_DISABLE_SHAPE_CACHE=1` bypasses this reuse for comparisons.

Five final interleaved comparisons against preview39 use Windows Godot 4.7,
Vulkan Mobile, RTX 5080, engine fonts, 1520x800 windows and 1280x720 documents.
Each process opens 15 fresh gallery documents; timing includes parsing and
host preparation, excludes rendering, and permits shared fonts to be warm.
Median run means improve **19.335 -> 17.927 ms (7.3%)**. Three pairs improve
and two regress. Baseline means span 17.873–20.551 ms, candidate means
16.379–19.363 ms; maximum builds are 33.528 and 25.137 ms. The first stress
open in each process is measured separately: median **20.294 -> 24.551 ms**.
This change does not demonstrate a first-open improvement or a 16.7 ms bound.
An earlier five-pair pass measured 19.373 -> 17.608 ms with all pairs faster;
the final pass follows the storage-capacity and font-chain bounds review.

Five animated comparisons (240 warmups, 300 fixed 1/60 updates) measure
**15.683 -> 15.920 ms** whole frames and **1.266 -> 1.239 ms** core updates,
as medians of run means. Two pairs improve in whole-frame time and three in
core time. The candidate reaches **91.017 ms** in a run whose core maximum
is 4.808 ms; the baseline reaches 118.002 ms. Agent builds, tests and profiling
are stopped during comparisons, but another user Godot game/editor remains
active and the shared graphics workload is uncontrolled. Steady-state shapes
already have per-document reuse, so this cold-path change is not evidence of
a whole-frame improvement. Rendered stalls remain open.

Native adapter checks pass **10,236 on Windows / 10,226 on Linux**. They compare
positions, byte clusters, metrics and bitmap bytes with independently configured
TextServer fonts across local handle differences, font replacement, fallback
chains, fractional sizes, marks, Arabic, emoji, invisible controls and eviction.
The adapter/test-only Linux ASan/UBSan run passes all 10,226 checks. An initial
Linux metrics mismatch also reproduced with sharing disabled; initializing
both sides' raster state before reading metrics fixes that test oracle.
The first fresh Linux native-test import exits unsuccessfully after editor
initialization without an engine error or crash stack; a fresh retry passes.
The earlier intermittent import failure remains unresolved.

Both platforms pass all 19 host suites, 75/79 headless/rendered theme checks
and 208/336 font-context checks. Eight native stress PNGs per platform match
preview39 byte for byte. Native debug, release and embedded-pack exports pass
on both platforms with unchanged example pixels. The headless core is unchanged
from preview38 (593,614 Release and sanitizer checks); those suites and the
47 backend / 12 live comparisons are not rerun for this native-adapter change.
The repository Windows DLL and running editor remain unchanged.

Artifact: `weva-godot-preview-20260906-40.zip`, 20 files, 5,392,882 bytes;
SHA-256 `1c6951bdbbe117029f5f8031a45556edae6f18febacfa6e930bb253e7c4afb23`.
Final logs use `product-shaped-runs-final-*` under the external Windows build
directory; native checks and export logs use `product-shaped-runs-*` there
and under `/root/weva` on Linux. Cold startup, rendered stalls and the earlier
preformatted-newline intrinsic-sizing issue remain open.

## Default font array ownership (2026-09-06)

Preview39 fixes a source of accumulating native font work: `Font.get_rids()`
returns a shared Array, and copying its `TypedArray` handle does not copy the
list. The host appended compatibility symbol fonts to that borrowed array.
The observed Windows font chain grew 9 -> 17 -> 25 -> 33 entries across fresh
documents, also changing the default Font seen by other native controls.
The host now duplicates the array before appending. Native RID/resource owners
and the existing font-change invalidation remain unchanged.

Forty new theme-font checks exercise six document open/toggle/destroy cycles
while another document remains live. Preview38 fails **20 checks** on Windows
and Linux; the new build passes **75 headless and 79 rendered theme checks**
on each platform, alongside **208 headless / 336 rendered font-context checks**.
The native-width oracle sums positioned glyph advances and repeats: the
default font's rounded TextServer extent was 43px where the actual advances
sum to 42.25px. The initial negative logs include that separate oracle mismatch;
the final negative logs isolate the 20 ownership failures.

Five interleaved native cold comparisons against preview38 use Windows Godot
4.7, Vulkan Mobile, RTX 5080, engine fonts, 1520x800 windows and 1280x720
documents. Each process opens 15 fresh gallery documents. Timing includes
parsing and host preparation but excludes rendering; shared fonts may be warm.
Median run means **17.249 -> 17.505 ms** regress slightly. Two pairs improve
and three regress. Baseline means span 16.882–18.027 ms, candidate means
16.148–17.716 ms; maximum individual builds are 23.440 and 23.965 ms. This is
an ownership fix, not a demonstrated native cold speedup.

Five animated comparisons (240 warmups, 300 fixed 1/60 updates) measure
**24.067 -> 25.523 ms** whole frames and **1.400 -> 1.380 ms** core updates,
as medians of run means. Three pairs improve and two regress in each metric.
A candidate frame reaches **117.644 ms**, in a run whose core maximum is
6.369 ms. The baseline reaches 106.359 ms. Agent builds, tests and profiling
are stopped during comparisons, but another user Godot game/editor is active;
the shared graphics workload is uncontrolled. These whole-frame results are
not comparable to a quiet-machine latency guarantee, and the stalls remain open.

The headless core is unchanged from preview38, whose Release and ASan/UBSan
suites passed 593,614 checks; they are not rerun for this host-only change.
Both platforms pass all 19 host suites. Native font-adapter checks remain
797 on Windows and 707 on Linux. Eight native stress PNGs per platform match
preview38 byte for byte. Both platforms pass native debug, release and
embedded-pack exports with unchanged example pixels. The repository Windows
DLL remains unchanged.

Two exploratory approaches led to this finding and are not retained. A
stage-use profile shows all **197 layout-stress runs / 1,158 glyphs** measured
during cold layout are consumed by painting too; deferring their extraction
alone would move the work. A bounded shared-shaping prototype first excludes
the nine-font chain, then admits the private immutable fallback inputs; it
still gets zero repeated-document hits because the borrowed source array has
grown before the next adoption. Its sources and logs stay outside the repo.
The exploratory binaries also retained diagnostic record fields after a
timestamp-preserving source restore, so their timings are not release evidence.
The final restore updates source timestamps and recompiles all affected objects;
the final DLL contains neither the stage-use nor shared-shaping instrumentation.

Artifact: `weva-godot-preview-20260906-39.zip`, 20 files, 5,382,134 bytes;
SHA-256 `f4d4ee0cbc67513198dcb9578474ee101a651cc879ce2b8cf08d2a16cefe2038`.
Cold native cost, rendered stalls, the earlier intermittent Linux import abort
and the preformatted-newline intrinsic-sizing issue remain open.

## Combined intrinsic-size measurement (2026-09-06)

Preview38 computes an item's min-content and max-content widths in one tree
walk when flex/grid capture `ParentLayoutInput`. Child classification, style
reads and min/max constraint resolution are shared. Single-size callers use
specializations that omit the other calculation. Both results still use the
current geometry; no retained cache, box fields or invalidation keys are added.
Summation order and the existing sizing behavior are preserved.

Five interleaved Windows Godot 4.7 comparisons against preview37 use Vulkan
Mobile, RTX 5080, engine fonts, 1520x800 windows and 1280x720 documents. Each
process opens 15 fresh gallery documents; timing includes parsing and host
preparation but excludes rendering. Shared fonts may be warm. Agent builds,
tests and profiling are stopped; unrelated machine/GPU load is uncontrolled.
Median cold run means are **19.370 -> 19.292 ms**. Four pairs regress and one
improves; baseline means span 18.025–28.992 ms, candidate means 18.764–33.795 ms.
Maximum individual builds are 37.733 and 40.754 ms. This batch does not establish
a native cold speedup.

Five animated pairs (240 warmups, 300 fixed 1/60 updates) measure **2.400 ->
2.688 ms** whole frames and **1.041 -> 1.000 ms** core updates, as medians of
run means. Core means improve in four pairs and regress in one; whole-frame
means improve in two and regress in three. A candidate **213.181 ms** stall
occurs in a run whose core maximum is 4.799 ms; another run reaches 94.850 ms.
The baseline also stalls at 92.156 ms. Long rendered frames remain unresolved.

Three headless pairs improve cold means **10.035 -> 8.914 ms**, with all three
pairs improving. Animated means improve **2.216 -> 2.104 ms**, with two pairs
improving and one regressing. Steady allocations remain **15,245 allocations /
8,180,301 bytes** per measured update. The retained change reduces repeated
core work without adding allocation or a new geometry cache; it does not yet
deliver a reliable native cold-opening improvement.

An external test executable links the unchanged preview37 intrinsic routines
as an independent oracle. Across the full mutation corpus, **772,629 exact
comparisons** agree: 523,548 parent captures, 14 minimum-only measurements,
37,077 maximum-only measurements and 211,990 block contributions. The oracle
and its linker wrappers stay outside the runtime. Release and ASan/UBSan each
pass **593,614 checks**, including 117 added checks for wrapped/nowrap/preformatted text,
forced breaks, inline decoration, flex wrapping/direction, grid tracks,
percentage widths, frames, margins, constraints, excluded out-of-flow boxes
and geometry changes without style changes.

Eight native stress PNGs match preview37 on each platform. All 47 backend
rows retain their preceding results and all 12 live states remain at zero
difference. Windows/Linux each pass 19 host suites, rendered theme/viewport-font
checks and native debug, release and embedded-pack exports with unchanged
example pixels. One Linux fresh import aborts with exit -6 after initialization,
without a diagnostic; its fixture/log are retained. A separate fresh fixture
passes the complete export check. The intermittent import failure is not
explained by this retry and remains open.

A separate preformatted-newline fixture exposes existing behavior in both
implementations: with 8px monospace advances, `aa bbbb\ncc` exports 72px for
both intrinsic sizes, joining the two forced lines. Its negative log is kept
as `product-intrinsic-pair-gcc-targeted.log`; this optimization does not change
that behavior. The preformatted case in the retained measurement tests uses
one line; explicit `<br>` coverage remains separate.

Artifact: `weva-godot-preview-20260906-38.zip`, 20 files, 5,381,598 bytes;
SHA-256 `648beb15093de23ea39924447747e15da3a4f0b78c28aca360dd8e8efe59552a`.
No C ABI or runtime host API changes are introduced. The repository Windows
DLL remains unchanged, so the open editor still uses its older build.

## Reusing parsed border widths (2026-09-06)

Preview37 routes layout's border widths through the existing computed-style
parse cache for owned declarations. Previously even repeated `1px` widths went through raw numeric
parsing, while relative and calculated widths built temporary parsed values.
The cache retains syntax, not resolved geometry: changes to font size, viewport,
root metrics and DPI still take effect on every resolution. Declaration writes,
unset and clear use the existing parsed-value invalidation. Border styles
`none` and `hidden` still skip width work. The raw-string API also replaces
its locale-dependent `strtod` path with the engine's `from_chars` wrapper.

Final review found that registry initial values can change without advancing
a style's version. A negative probe changes the registered initial width from
1px to 2px and observes a stale cached 1px. The final build keeps inherited and
registry-provided widths on raw resolution; the same probe now returns 2px.
The cache therefore applies only to declarations invalidated by style writes.

Five final interleaved Windows Godot 4.7 comparisons against preview36 use Vulkan
Mobile, RTX 5080, engine fonts, 1520x800 windows and 1280x720 documents. Each
process opens 15 fresh gallery documents; timing includes parsing and host
preparation, excludes rendering, and can benefit from warm shared fonts.
Agent builds, tests and profiling are stopped; unrelated machine/GPU load
remains uncontrolled. Median cold run means are **19.205 -> 18.874 ms**;
three pairs improve and two regress. Baseline means span 17.490–22.535 ms,
candidate means 17.814–20.884 ms. Maximum individual builds are 33.655 and
32.094 ms. The small median change does not establish a robust cold speedup.

Five animated pairs (240 warmups, 300 fixed 1/60 updates) measure
**2.743 -> 3.853 ms** whole frames and **1.069 -> 1.063 ms** core updates,
as medians of run means. Whole-frame means regress in four pairs; core means
improve in four. Candidate whole-frame means span 2.880–4.161 ms, versus
2.537–4.735 ms. A **95.750 ms** candidate stall recurs, with the core update
peaking at 4.148 ms in that run; preview36 also stalls at 81.855 ms. This batch
does not establish a whole-frame improvement. Three headless cold pairs measure
**7.871 -> 7.994 ms**, and three animated pairs **2.136 -> 2.110 ms**. Both show
variation: two pairs regress and one improves in each batch. Steady allocations
remain **15,245 allocations / 8,180,301 bytes** per measured update.

The preliminary candidate, before the registry-value guard, measured
18.749 -> 18.634 ms cold and 1.090 -> 1.060 ms animated core. Those logs are
retained with a `preliminary-` prefix; the final build's comparisons above are
the release evidence.

An allocation-only headless control links the same counting driver against
both core archives and measures the third fresh document. Cold allocations
rise **52,805 -> 54,041**, and allocated bytes **24,091,828 -> 24,128,084**:
1,236 more allocations and 36,256 more bytes for retained parsed widths.
These totals include document creation, CSS/HTML parsing and first update;
destruction is excluded. Its timing is not used as performance evidence.

Two earlier investigations were not retained in the runtime. Release
link-time optimization passes all 591,870 pre-change core checks and preserves
Windows native stress pixels, but measures **17.458 -> 17.801 ms** cold,
**1.101 -> 1.109 ms** animated core and **2.144 -> 2.143 ms** headless animated.
The build flags and helper module were removed. Box-model instrumentation
finds 1,880 distinct inputs and 1,824 repeats among 3,704 calls. Direct-mapped
64/256/1,024-slot simulations yield 0/100/1,152 hits; the small-cache proposal
was not implemented. The diagnostic source and logs are retained outside the
repo as `block-layout-box-input-profile.cpp` and `product-box-inputs-*`.

Release and ASan/UBSan each pass **593,497 checks** with the complete mutation
corpus. The 1,627 added checks cover all four border widths, keyword/numeric/
relative/calculated syntax, changing font/context inputs, declaration changes,
unset/clear, hidden/none styles and registry initial-value replacement. Eight
native stress PNGs match preview36 on each platform. All 47 backend rows
retain their preceding results and all 12 live states remain at zero difference.
Windows/Linux each pass 19 host suites, the rendered theme/viewport-font
checks, and native debug, release and embedded-pack exports with unchanged
example pixels. No C ABI layout or runtime host API changes are introduced.

Artifact: `weva-godot-preview-20260906-37-final.zip`, 20 files, 5,375,851 bytes;
SHA-256 `c5b34904332d2141e85a34fb579dc356f3294069133bf54691ea4f8ad2fd2b60`.
The open editor still uses its older DLL. Cold layout remains above the target
and the long-frame issue remains unresolved.

## Font-size context validity (2026-09-06)

Preview36 fixes a cache error exposed while profiling layout-stress. An empty
box with `font-size:10vw;width:1em;height:1em` stayed 10x10 after its viewport
width changed from 100 to 200; a fresh document correctly produced 20x20.
The font-size memo now includes viewport width/height, root font/line metrics
and DPI. Owned pixel/number values can skip parent/context resolution while
their declaration version matches. Inherited and relative values retain all
dependency checks. This adds five doubles and an absolute-value flag per style;
it does not change unit interpretation or fix the existing nested em chain.

Five interleaved comparisons against preview35 use Windows Godot 4.7 stable,
Vulkan Mobile, RTX 5080, engine fonts, a 1520x800 window and 1280x720 documents.
Each process opens 15 fresh documents, including parsing and host preparation
but excluding rendering. Agent builds, tests and profiling are stopped;
unrelated system/GPU load remains uncontrolled and shared fonts may be warm.
Median cold run means **18.175 -> 18.838 ms** regress by 3.6%; two pairs improve
and three regress. Baseline means span 17.589–19.203 ms and candidate means
17.509–19.671 ms; maximum individual builds are 26.997 and 27.457 ms.
This is a correctness fix with a measured cost, not a demonstrated speedup.

Five animated pairs (240 warmups, 300 fixed 1/60 updates) measure
**3.256 -> 3.321 ms** whole frames and **1.157 -> 1.201 ms** core updates,
as medians of run means. Three pairs improve in each metric, but their medians
increase. Candidate whole-frame means span 2.969–3.537 ms. A **94.249 ms**
candidate stall recurs, with core updates in that run peaking at 7.410 ms;
preview35 also stalls at 92.810 ms. Three headless animated pairs measure
**2.335 -> 2.441 ms**, with the allocated volume unchanged at
**15,245 allocations / 8,180,301 bytes** per measured update. Cold cost and
long frame latency remain priorities.

A separate three-pair control runs an otherwise empty Godot project with one
ColorRect changing color each frame and verifies that `WevaDocument` is absent.
It uses the same engine, renderer, window, disabled VSync and offscreen position,
with 240 warmups and five seconds of measurement. Its maxima are 8.538, 7.365
and 12.418 ms. Interleaved 1,500-frame layout-stress runs peak at 7.966, 8.157
and 87.602 ms. This smaller workload does not reproduce the stall; it neither
isolates the cause nor rules out shared GPU contention. Private fixture and
logs are retained under `weva-build/frame-stall-control` and
`product-frame-stall-*`.

The final cold profile still puts most work in flow layout. Five instrumented
builds spend 10.397–14.966 ms there, including 3.435–5.134 ms in 197 native shape
calls. Four completed 205-run font profiles spend 1.404–1.651 ms extracting
Godot's glyph dictionaries, versus 0.884–1.063 ms shaping and 0.394–0.463 ms
converting them. The supported TextServer consumer API returns dictionaries;
the pointer-returning method is a TextServerExtension implementation hook.
No private engine interface is used to bypass extraction. These instrumented
times direct further investigation and are not another speed comparison.

Release and ASan/UBSan each pass **591,870 checks**, including 837 new context,
mutation and fresh-render comparisons. The preceding implementation fails
142 of the initial core regressions and 126/168 native headless/rendered
checks. The final build passes 208 native geometry checks and 336 rendered
checks on each desktop platform, alongside 35/39 theme-font checks. Chrome
148 passes 80 matching geometry checks. Eight native stress PNGs match
preview35 on each platform; all 47 backend rows retain their preceding results
and all 12 live states remain at zero difference. Both platforms pass 19 host
suites and native debug, release and embedded-pack exports with unchanged
example pixels.

Artifact: `weva-godot-preview-20260906-36.zip`, 20 files, 5,375,371 bytes;
SHA-256 `0dfd49e2fa908a32d10a6c0358c399ba23c6955d59814e6744d28165d357624a`.
The running editor still uses the older DLL. These results come from isolated
projects; no editor reload has been performed.

## Declaration walks and glyph conversion (2026-09-06)

Preview35 removes two repeated costs identified in the cold profiles:

* Computed-style declaration enumeration now walks the existing occupancy
  bitset in ascending property order and reserves its known result count.
  The cascade's three resolution passes no longer scan all 334 slots per
  style or repeatedly grow each result vector. Presence, input versions and
  cascade order keep their existing contracts.
* Godot glyph conversion constructs its six native String/Variant dictionary
  keys once per text run, instead of once per glyph. Text and glyph counts
  are also read once. TextServer still supplies all glyphs, offsets, advances,
  repeat counts and fallback-font identities through its supported API.

`WEVA_LAYOUT_LOG=1` now separates inline collection, atom sizing and line
construction. A native cold profile records 211 collections (~0.15–0.20 ms),
1,774 atom-sizing calls (~0.04–0.06 ms) and 518 line builds (~4–5 ms), with
shaping included in line construction. Native glyph conversion drops from
roughly 0.56–0.68 ms to 0.30–0.39 ms in separate instrumented runs. The three
cascade resolution scopes previously totalled about 1 ms; after enumeration
changes they total roughly 0.40–0.65 ms. These scopes explain the work removed;
the uninstrumented comparisons below measure its overall effect.

Five final interleaved comparisons against preview34 use Windows Godot 4.7
stable, Vulkan Mobile, RTX 5080, engine fonts, a 1520x800 window and 1280x720
documents. Each process builds 15 fresh gallery documents; timing includes
parsing and host preparation and excludes rendering. Godot/shared-font caches
can already be warm. Agent builds, tests and profiling are stopped during
measurement; unrelated system/GPU load remains uncontrolled.

Median run means improve **19.182 -> 17.522 ms** (9%); four pairs improve and
one regresses. Baseline means range 18.021–19.574 ms; candidate means range
17.099–18.586 ms. Maximum individual builds are 24.734 and 24.672 ms. The
earlier glyph-only comparison measured 17.084 -> 16.753 ms; the final combined
build's results above are the release evidence. Cold opening still exceeds
16.7 ms. Absolute times from different batches are not cumulative speedups.

Five animated comparisons (240 warmups, 300 fixed 1/60 updates) are nearly
flat: **2.921 -> 2.961 ms** whole frames and **1.125 -> 1.134 ms** core updates,
as medians of run means. Whole-frame means improve in two pairs and regress
in three; candidate means range 2.799–3.585 ms, versus 2.716–3.446 ms.
A **95.933 ms** candidate whole-frame stall recurs; core updates in that run
peak at 5.051 ms. Preview34 also stalls at 81.522 ms. Long frame latency
remains unresolved; these optimizations do not establish a rendering fix.

Three interleaved GCC headless comparisons measure **9.388 -> 9.294 ms** cold
and **2.285 -> 2.202 ms** animated as median run means. Both batches show
variation, and the allocated volume of the measured animated update remains
**15,245 allocations / 8,180,301 bytes**.

Release and ASan/UBSan each pass **591,033 checks** with the full mutation
corpus. Added enumeration checks verify every id in a densely populated style
written in reverse order, alongside existing sparse, high-id, unset, clear
and move cases. Native font tests pass **797 Windows / 707 Linux checks**.
Eight native stress PNGs match preview34 byte-for-byte on each platform.
All 47 backend rows retain their previous results and all 12 live states
remain at zero difference. Both platforms pass 18 host suites and native
debug, release and embedded-pack exports, with unchanged example pixels.

The first Linux host-suite fresh-editor import exited unsuccessfully without
an error diagnostic, before executing the suites. A new fixture passes the
same 60-frame import and all 18 suites; native exports also pass. The failed
log is retained. This single retry does not establish the cause or eliminate
the documented stock-engine import/shutdown timing risk.

Artifact: `weva-godot-preview-20260906-35.zip`, 20 files, 5,374,468 bytes;
SHA-256 `9fd2ac3a450644add829ac7e11722e5283ffc6ac4515a5cdf089102f21a8d151`.
The running editor still uses the older DLL. An independent empty-box resize
probe also exposed a viewport-dependent font-size memo bug, fixed in preview36
above.

## Sparse raw style storage (2026-09-06)

Preview34 allocates computed-style strings in stable pages of 16 values,
using a property-id slot table. Previously every style constructed strings
for all 334 registered properties, although layout-stress sets only 8,860
values across 719 styles. The new cold build allocates 835 pages (13,360
string cells), compared with 240,146 cells in the dense representation.
Presence, importance, parsed-value caches, inheritance and input versions
retain their existing semantics. Adding pages or growing metadata cannot
invalidate another property's string view. Clearing retains page/string
capacity at the style's high-water usage, while resetting values and caches.
The tradeoff is an extra indirection on raw reads and additional page/table
allocations, in exchange for much less string construction and storage.

`WEVA_CASCADE_LOG=1`, together with `WEVA_STAGE_LOG=1`, splits cascade work
into matching, match copying, declarations, logical/custom properties,
attr/env, variables, keywords and pseudo queries. It separately records
metadata and raw-value allocation time. Clocks are disabled by default.
Native allocation profiles measured **2.31–2.89 ms** with dense strings and
**0.52–1.32 ms** with pages; these are separate instrumented runs, not a
controlled timing comparison. Declaration application falls from roughly
2.9–3.4 ms to 1.05–1.87 ms in those profiles. Full flow layout remains about
9.8–10.2 ms under instrumentation and is the largest remaining cold stage.

Five final interleaved comparisons against preview33 use Windows Godot 4.7
stable, Vulkan Mobile, RTX 5080, engine fonts, a 1520x800 window and 1280x720
documents. Each process builds 15 fresh gallery documents; the timer includes
parsing and host preparation but excludes rendering. Godot and shared-font
caches can already be warm. No agent builds, tests or profiling run alongside
these comparisons; other system/GPU load remains uncontrolled.

Median run means improve **19.354 -> 17.415 ms** (10%); all five pairs improve.
Baseline means range 19.061–19.507 ms and candidate means 17.286–18.767 ms.
Maximum individual builds are 25.358 and 25.027 ms. An earlier storage
candidate measured 19.147 -> 17.347 ms, with a 32.716 ms candidate run mean;
the final batch above follows the moved-from-clear fix and is the release
evidence. Cold opening still exceeds 16.7 ms and no latency bound is claimed.

Five final animated comparisons (240 warmups, 300 fixed 1/60 updates) measure
**4.009 -> 3.952 ms** for whole frames and **1.123 -> 1.132 ms** for core
updates, as medians of run means. Whole-frame means improve in four pairs;
core improves in two, regresses in two and ties in one at printed precision.
Candidate whole-frame means range 3.710–4.042 ms, versus 3.641–4.265 ms.
Candidate maximum is 28.475 ms, versus 34.351 ms. Long rendering stalls remain
open; this is a cold-storage improvement, with nearly flat animated core time.

Three interleaved GCC headless cold comparisons are flat within variation:
**8.658 -> 8.720 ms**. Three animated comparisons measure **2.085 -> 2.128 ms**,
with **15,245 allocations / 8,180,301 bytes** unchanged per measured update.
The native Windows cold gain does not establish a Linux timing improvement.

Release and ASan/UBSan each pass **590,698 checks** with the full mutation
corpus. The 368 added checks cover sparse/dense insertion order, metadata
growth, string-view and parsed-value lifetimes, unset/refill, move/swap,
clearing a moved-from object and semantic diffs between different slot orders.
Eight native stress PNGs match preview33 byte-for-byte on each platform.
All 47 backend rows retain their previous results, and all 12 live states
remain at zero difference. Windows and Linux each pass 18 host suites plus
native debug, release and embedded-pack exports with matching example pixels.

Artifact: `weva-godot-preview-20260906-34.zip`, 20 files, 5,373,512 bytes;
SHA-256 `7c1cd83b18b1ed0743253cc6f4c08e78a1ff72d50dc2a8fdd0c18d7132c00c37`.
The running editor still uses the older DLL; the candidate was verified in
isolated projects. No editor reload has been performed.

## Sharing immutable synthetic fonts (2026-09-06)

Preview33 retains up to eight recently used synthetic primary fonts across
Godot documents. Their exact file bytes, TextServer owner, emboldening strength
and italic transform form the key. These remain independent fonts, so changing
their synthesis cannot affect the regular face's glyph cache. Active backends
hold references independently of the LRU; eviction cannot invalidate a live
document. Module shutdown releases the pool before the engine disappears.
The memory tradeoff is retained file data and native glyph caches for eight
fonts, plus any evicted fonts still held by active documents.

Profiling found approximately **1 ms** opening the same synthetic bold face
for each fresh layout-stress document. With sharing, its seven face-metric
queries take only a few microseconds after the gallery has warmed those fonts.
The native shape and glyph caches can also survive between documents. This
does not eliminate initial font preparation for a new file or synthesis mode.

The work also fixes a rendering error: the per-document key stored only
`bold`, although the adapter already distinguishes 0.6 and 0.9 emboldening.
Requesting 800 before 700 therefore rendered both with the heavier result.
Keys now distinguish those strengths. Mixed-weight stress screenshots change
intentionally; lower-weight labels no longer inherit the header's heavier
synthesis. Native tests compare each weight and italic combination against
independently configured TextServer fonts, including exact raster coverage.

Five final interleaved comparisons against preview32 use Windows Godot 4.7
stable, Vulkan Mobile, RTX 5080, engine fonts, a 1520x800 window and 1280x720
documents. Each process builds 15 fresh gallery documents; the timer includes
parsing and host preparation but excludes frame rendering. The preceding
gallery screen and later documents can warm Godot and shared-font caches.
No agent build/test runs during measurement; shared system/GPU load is
uncontrolled and varied substantially during this batch.

Median run means improve **22.750 -> 20.523 ms** (10%); four pairs improve,
one regresses. Baseline means range 20.862–37.192 ms and candidate means
19.631–33.445 ms. Maximum individual builds are 43.559 and 39.789 ms,
respectively. An earlier candidate measured 21.849 -> 18.876 ms, but the final
build's paired results above are the release evidence. Cold opening still
exceeds 16.7 ms and the observed variation prevents a latency guarantee.

An independent three-pair comparison uses the final library with sharing
disabled/enabled, keeping the weight correction in both modes. Every pair
improves; medians of run means are **23.338 -> 22.747 ms**. Disabled means
range 23.185–39.164 ms and enabled means 19.489–27.508 ms. This control supports
a sharing benefit but its timing is also affected by the changing load.

Five separate native animated comparisons (240 warmups, 300 fixed 1/60 updates)
measure **2.617 -> 2.539 ms** for whole frames and **1.142 -> 1.080 ms** for
core updates as medians of run means. Whole-frame mean improves in two pairs
and regresses in three; core mean improves in three. Candidate frame means
range 2.347–3.839 ms, versus 2.318–3.777 ms for preview32. Candidate maximum
is 10.683 ms. Previously observed long rendering stalls remain unresolved;
they did not recur in this batch.

`WEVA_LAYOUT_LOG=1` adds inclusive box-model, inline and finalization scopes
inside full root layout. Before this optimization they attribute 1.2–1.6 ms
to box-model resolution and 6–7.4 ms to inline work, including roughly 4 ms
of shaping. `WEVA_FONT_LOG=1` also reports individual native metric calls.
Disable all logging for comparisons. `WEVA_GODOT_DISABLE_VARIANT_CACHE=1`
provides the independent sharing control.

Release and core ASan/UBSan each pass **590,330 checks** with the complete
mutation corpus. Native adapter tests pass **797 checks on Windows and 707
on Linux**, with sharing enabled and disabled. The 250 added checks cover
weight/italic combinations, exact native raster coverage, data changes,
eviction and active-owner lifetimes; restoring the old key fails eight checks.
An instrumented Linux adapter/test extension also passes its 707 checks under
ASan/UBSan. See [the native test notes](GODOT_TEXT_SHAPING.md#synthetic-font-ownership)
for its loader setup and scope.

Eight native cache-disabled/enabled stress PNGs match byte-for-byte on each
platform. All 47 backend rows retain preview32's results, and all 12 live
states remain at zero difference. Each platform passes 18 host suites and
native debug/release/embedded exports; example pixels are unchanged. A
headless allocation control keeps **15,245 allocations / 8,180,301 bytes**
on the final animated update, matching preview32. The new retention cost is
in Godot's bounded font pool, not in that headless allocation count.

Artifact: `weva-godot-preview-20260906-33.zip`, 20 files, 5,367,808 bytes;
SHA-256 `b47e05baff516f110f9795758b85e5e0d6550e17e07bc404e1b432ddf86faba9`.
The repository editor still loads its older Windows DLL.

## Reusing inline results during sizing probes (2026-09-06)

Preview32 retains bounded plain-text line results within each layout pass.
One cold layout-stress build calls inline layout **1,774 times**; **1,256**
calls repeat a previously seen constraint and now reuse their lines.
Most repeats are nonconsecutive. Each container retains up to three results
of at most eight lines, keyed by exact width, padding/border origin and style
inputs. Active floats, atoms and inline fragments keep the ordinary walk.
Reused lines restore their original positions before button or table-cell
alignment; failing to do this initially caused a button label to drift.

Five interleaved native comparisons against preview31 use Windows Godot 4.7
stable, Vulkan Mobile, RTX 5080, engine fonts and the same 1520x800 window /
1280x720 document setup as below. Each process builds 15 fresh documents;
the gallery timer includes parsing and host/font preparation, excludes frame
rendering, and can benefit from Godot's resource caches. No agent build/test
runs during timing; shared system/GPU load remains uncontrolled.

The median of run means improves **24.309 -> 22.063 ms** (9%). All five pairs
improve. Baseline means range 23.201–25.635 ms, maximum individual build
34.542 ms; candidate means range 21.283–23.728 ms, maximum 31.296 ms.
This is a separate paired comparison from preview31's 30.730 -> 23.437 ms
measurement. Cold opening still exceeds a 16.7 ms frame budget.

Three interleaved GCC Release stub-font comparisons, each with 100 fresh
documents at 1280x720, improve **9.576 -> 8.348 ms** as medians of run means.
Baseline means range 9.159–9.853 ms; candidate means 8.290–9.133 ms.

Five separate native animated comparisons use 240 warmups and 300 measured
fixed 1/60 updates per run. Median run means are **2.837 -> 2.783 ms** for
whole frames and **1.189 -> 1.167 ms** for core updates. Three pairs improve
and two regress in both means. Candidate whole-frame means range
2.593–2.891 ms versus 2.492–2.934 ms for preview31; this is a small change.
The candidate peaks at 8.049 ms in this batch. Previously observed long
rendering stalls did not recur here, which does not establish that they are
fixed. The repository editor continues to load its older Windows DLL.

`WEVA_STAGE_LOG=1` now attributes host-font calls, hits and elapsed time by
operation. `WEVA_FONT_LOG=1` reports Godot shaping phases when its backend
clears. `WEVA_INLINE_LOG=1` traces result hits; disable all logging for timing.
`WEVA_DISABLE_INLINE_REUSE=1` provides an independent ordinary-layout control.
Font-metric and native font-array caching experiments showed no reliable
paired speedup and were removed; their diagnostic scopes remain available.

Three separate GCC Release animated comparisons (300 updates, stub fonts)
measure **2.148 -> 2.179 ms** as medians of run means, effectively unchanged.
Baseline means range 2.089–2.204 ms and candidate means 2.097–2.287 ms.
The final update keeps **15,245 allocations**, while allocated bytes rise
**8,149,517 -> 8,180,301** (+30,784, 0.38%). The bounded result records enlarge
the existing per-container entries; no extra allocation is made per record.
This small animated memory cost buys the measured cold-build reduction.

Release and ASan/UBSan each pass **590,330 checks** with the complete mutation
corpus. The additional 145 checks compare every reachable box with fresh
layout across alternating widths, buttons, table alignment, inline fragments,
atoms, floats, more than eight lines and ellipsis, and prove nonconsecutive
reuse occurs. The disabled-reuse control passes 590,329 checks (omitting the
one assertion that specifically requires reuse). Eight native stress PNGs
match preview31 byte-for-byte; all 47 backend rows retain their preceding
results and all 12 live states remain at zero difference. Both platforms
pass 18 host suites and native debug/release/embedded exports, with example
pixels unchanged.

Artifact: `weva-godot-preview-20260906-32.zip`, 20 files, 5,360,143 bytes;
SHA-256 `09865fcb967d386f9a644f81da6b24fa1ab437ea0a51e6b57a697707fcea8ae2`.

## Preparing backdrops only for top-layer hosts (2026-09-06)

Preview31 avoids computing the universal UA `::backdrop` rule for ordinary
elements. Style preparation uses the box builder's existing eligibility rule;
modal dialogs and open popovers still receive their cascaded backdrops. A
previously used style stays dormant while closed and is recomputed before
reopening, including changes made while it was absent. The general pseudo
cascade API and C ABI are unchanged.

On layout-stress this removes **719 unused styles**, reducing owned styles
from **1,438 to 719** and directly stored property values from **249,006 to
8,860**. Three interleaved GCC Release comparisons, with 100 fresh documents
per run, reduce cold build time **13.500 -> 9.372 ms** as medians of run means.
Baseline means range 13.018–13.517 ms; candidate means 8.733–9.482 ms.
This benchmark includes parsing and uses stub fonts at 1280x720.

Five interleaved Windows Godot 4.7 stable comparisons use Vulkan Mobile,
RTX 5080, a 1520x800 window and engine fonts. Each process opens 15 fresh
layout-stress documents at the gallery's 1280x720 document size. The gallery
timer includes parsing, document construction, fonts and host texture
preparation; later documents can benefit from Godot's resource caches. It
excludes rendering the resulting frame. No agent build/test runs during
measurement; shared system/GPU load remains uncontrolled.

The median of run means drops **30.730 -> 23.437 ms** (24%). All five pairs
improve. Candidate means range 22.658–24.237 ms, with a maximum individual
build of 33.701 ms; baseline means range 29.928–31.679 ms, maximum 37.740 ms.
This improves opening latency, but does not establish a 16.7 ms build budget.

The separate animated-frame comparison uses five interleaved pairs, 240
warmups and 300 measured fixed 1/60 updates per run. Median run means are
**3.736 -> 3.841 ms for whole frames** and **1.121 -> 1.099 ms for core
updates**. This change targets startup; it shows no whole-frame speedup.
Whole-frame mean improves in one pair and regresses in four. Candidate
means range 3.345–4.787 ms, versus 3.340–3.906 ms for preview30. An **87.219
ms frame** recurs in the candidate, while that run's core updates peak at
4.276 ms; the previously observed long rendering stalls remain unresolved.
Median frame times overlap (candidate 2.414–2.530 ms, baseline 2.376–2.536 ms).

Reproduce with `WEVA_GALLERY_PROBE_COLD_BUILDS=15` and
`--script res://layout_stress_probe.gd --fixed-fps 60 --resolution 1520x800`.
The probe reports the first layout-stress build separately. Core update time
is deliberately absent: the gallery's later bounds queries can overwrite
`get_last_update_ms()` with an idle update. A separate `WEVA_STAGE_LOG=1`
run attributes cascade storage and full layout's flow, positioning, overflow
and incremental-index costs. Logging stays off for timing comparisons.
The headless cold sampler now excludes destruction, matching its timer.
In the separate native cold profile, normal layout flow takes 11.4–13.2 ms,
versus 0.38–0.40 ms to build the incremental index. Flow remains the largest
measured cold stage; the architectural index is not the main remaining cost.

Release and ASan/UBSan each pass **590,185 checks** with the complete mutation
corpus. The additional 110-check sequence compares incremental/full draws
through repeated dialog/popover opens, closes, declaration changes while
closed, inherited custom properties, host removal and document reload.
Eight native stress snapshots match preview30 exactly. All 47 backend rows,
12 interactive states, 18 host suites per platform and native
debug/release/embedded exports retain their preceding results.

Artifact: `weva-godot-preview-20260906-31.zip`, 20 files, 5,352,844 bytes;
SHA-256 `f5825670bbe4b10a9b1507eb22aa38e8c7a9734e0ed0ffafafb32080436673c9`.
The repository editor still loads its older Windows DLL; these checks use
isolated projects.

## Skipping prepared glyph subtrees (2026-09-06)

Preview30 skips the glyph prepass for subtrees whose text/font input versions
and atlas slot version are unchanged. Previously every repaint shaped and
looked up text across the retained grid, even when its drawing was replayed.
Atlas clearing bumps the slot version; adding glyphs and changing the uploaded
texture preserve existing slots. Paint can still rebuild when position or clip
inputs change. Font/provider resets and text mutations prepare glyphs again;
popup labels keep their separate prepass. The C ABI is unchanged.

A separate Windows engine-font profile with reuse enabled/disabled measures
mean glyph preparation **0.167 -> 0.015 ms**, over 300 updates after 240
warmups. The retained grid skips preparation on all 300 updates, including
the 23 that rebuild its paint. Those instrumented runs overlap verification
work and provide attribution rather than whole-frame timing.

Five interleaved comparisons against preview29 use Windows Godot 4.7 stable,
Vulkan Mobile, RTX 5080, a 1520x800 window and engine fonts. VSync/native input,
logging and GPU timing are disabled. Each run uses fixed 1/60 steps, 240 warmup
and 300 measured frames; no agent build or test runs during measurement.

| Mode | Mean frame | Median frame | Mean core update |
|---|---:|---:|---:|
| Preview29 | 2.925 ms | 2.772 ms | 1.347 ms |
| Preview30 | 2.895 ms | 2.666 ms | 1.202 ms |

Each column is the median of that statistic across five runs. Four pairs
improve in mean core update, and three improve in mean/median frame time;
the first pair regresses in both. Whole-frame means are nearly unchanged.
Candidate means range 2.666–3.009 ms, p95 4.920–5.583 ms, maximum 8.197 ms.
Baseline means range 2.638–3.130 ms, p95 4.859–5.865 ms, maximum 7.782 ms.
Shared system/GPU load remains uncontrolled. The previously observed long
stalls did not recur in this short batch, which does not prove them fixed.

Three interleaved GCC Release stub-font comparisons at 1280x720 with 300
animated updates measure **2.410 -> 2.399 ms**, as medians of run means;
baseline/candidate ranges are 2.343–2.476 / 2.387–2.414 ms. The last update
makes **16,452 -> 15,245 allocations**, allocating **8,301,581 -> 8,149,517
bytes**: 1,207 fewer calls and 152,064 fewer bytes, with nearly unchanged time.

Release and ASan/UBSan each pass **590,075 checks** with the complete mutation
corpus. The new 76-check ABI sequence compares every frame with forced full
layout/paint, including form text, font changes, offscreen/revealed text,
viewport changes, renderer reset and document reload. Empty glyph raster calls
prove the unchanged text walk is skipped: preview29 fails all three skip
assertions, while preview30 passes. Atlas tests distinguish slot invalidation
from texture replacement and adding glyphs.

Windows Vulkan Mobile and Linux OpenGL each produce 26 byte-identical PNGs
with glyph reuse enabled/disabled; the mutation sequence exercises ten subtree
skips per platform. Eight native layout-stress snapshots match preview29.
All 47 backend rows, 12 interactive states, 18 host suites per platform and
native debug/release/embedded exports retain their preceding results. The
packed-array cache's enabled/disabled pixel regression also remains green.

Artifact: `weva-godot-preview-20260906-30.zip`, 20 files, 5,352,534 bytes;
SHA-256 `16dcd34fa7b66ade7ff9c215be7f62943f8e0263887a9651cff89e4a00cb0bc8`.
The repository editor still loads its older Windows DLL; these checks use
isolated projects.

## Reusing Godot packed draw arrays (2026-09-06)

Preview29 adds document-lifetime command versions through ABI minor 12,
without changing the published draw struct. Replayed commands preserve their
versions; rebuilt commands get new versions. Godot reuses a batch's converted
vertex/color/UV/index arrays when its ordered command versions match exactly.
Batch boundaries, canvas items, materials and texture lookup are unchanged.
Every redraw still submits/uploads geometry; this removes CPU packing work.

Five interleaved comparisons against preview28 use Windows Godot 4.7 stable,
Vulkan Mobile, RTX 5080, a 1520x800 window and engine fonts. VSync/native input,
logging and GPU timing are disabled. Each run uses fixed 1/60 steps, 240 warmup
and 300 measured frames; no agent build or test runs during measurement.

| Mode | Mean frame | Median frame | Mean core update |
|---|---:|---:|---:|
| Preview28 | 4.338 ms | 2.982 ms | 1.237 ms |
| Preview29 | 3.685 ms | 2.553 ms | 1.237 ms |

Each column is the median of that statistic across five runs. All five pairs
improve in mean and median frame time. Candidate mean frames range
3.449–4.675 ms, p95 11.017–13.614 ms, and maximum **92.748 ms**. Baseline
means range 3.875–4.733 ms with maximum 89.755 ms. Core updates in the candidate
run containing the long frame never exceed 4.268 ms. Shared system/GPU load
remains uncontrolled, and the long-frame issue remains open.

A separate instrumented comparison with cache reuse enabled/disabled measures
mean packing **0.488 -> 0.101 ms** over the final 300 draws. Submitted vertices
stay at 44,767.54 per frame on average, with 87 submissions in either mode.
Packed vertices fall to 8,751.11 per frame, averaging 54.48 reused batches.
These logged runs overlap other verification work and serve as attribution,
not the whole-frame benchmark above. The cache retains packed arrays in CPU
memory: 32 bytes per vertex plus four bytes per index and eight bytes per draw
version in the standard float build, excluding container/allocator overhead.
Unused batch slots are released after drawing.

Three interleaved GCC Release stub-font comparisons at 1280x720 with 300
animated updates measure **2.193 -> 2.086 ms**, as medians of run means;
baseline/candidate ranges are 2.029–2.272 / 2.058–2.105 ms. The ranges overlap.
Both make 16,452 allocations / 8,301,581 bytes in the last update. Godot's packed
array cache does not run in this headless benchmark.

Release and ASan/UBSan each pass **589,995 checks** with the complete mutation
corpus. The new ABI regression verifies version uniqueness, exact retained
command contents, shifted draw positions, mixed retained/rebuilt output,
font/renderer resets, empty/reloaded documents and idle view lifetimes.
Windows Vulkan Mobile and Linux OpenGL each produce **26 byte-identical PNGs**
with cache reuse enabled/disabled, while requiring actual cache hits. Cases
include redraws, removed/moved geometry, texture/material boundaries,
font/viewport changes, native modulation and instance shader parameters,
SDF/backdrop changes, visibility, empty documents and reloads.

Eight native layout-stress snapshots match preview28 byte-for-byte. The 47
backend comparison rows remain unchanged; all 12 interactive states remain
at zero difference. All 18 Windows/Linux host suites and debug, release and
embedded desktop exports pass, with unchanged example pixel hashes.

Artifact: `weva-godot-preview-20260906-29.zip`, 20 files, 5,351,334 bytes;
SHA-256 `d0d6eacdda349340ff20aeb13b6fff259e85fb17c927d7014024c5c01e24d4ef`.
The repository editor still loads its older Windows DLL; these checks use
isolated projects.

## Retaining paint across ancestor layout (2026-09-06)

Preview28 preserves paint input versions for grid subtrees that incremental
layout retains while replacing an ancestor. Previously the replacement
invalidated their entire paint subtree even when the incoming drawing inputs
were unchanged. Each boundary now compares absolute origin, accumulated
opacity/transform/filter, scissor, complete clip polygons and canvas owner.
Changed inputs bump the paint version before replay; actual style and scroll
changes still invalidate descendants normally. See [ARCHITECTURE.md](ARCHITECTURE.md).

A separate Windows Godot 4.7 engine-font probe finds unchanged grid and clip
bounds on **277 of 300 frames**, after 240 warmups at fixed 1/60 steps. The
instrumented candidate replays the retained grid on exactly those 277 frames.
Reused draw buffers move into their new command positions and keep referenced
textures alive through the existing cache. This does not eliminate GPU drawing.

Five interleaved comparisons against preview27 use Windows Godot 4.7 stable,
Vulkan Mobile, RTX 5080, a 1520x800 window and engine fonts. VSync/native input,
logging and GPU timing are disabled; each run uses fixed 1/60 steps, 240 warmup
and 300 measured frames. No agent build or test runs during measurement.

| Mode | Mean frame | Median frame | Mean core update |
|---|---:|---:|---:|
| Preview27 | 5.305 ms | 4.728 ms | 3.045 ms |
| Preview28 | 3.667 ms | 2.867 ms | 1.177 ms |

Each column is the median of that statistic across five runs. All five pairs
improve in mean frame/core time. Candidate mean frames range 3.602–4.409 ms,
p95 9.876–12.737 ms, and maximum **86.693 ms**; baseline means range
5.225–5.402 ms with maximum 26.990 ms. Core updates in the candidate run with
that long frame never exceed 4.346 ms. Shared system/GPU load remains
uncontrolled, and the frame-stall issue remains open despite the CPU saving.

The headless GCC Release benchmark with stub fonts, 1280x720 and 300 animated
updates measures **2.127 -> 2.164 ms**, as medians of three interleaved run means.
Baseline means range 2.104–2.156 ms and candidate means 2.125–2.217 ms. Both
make 16,452 allocations / 8,301,581 bytes in the last update. This small
headless regression is recorded separately from the native improvement;
replay depends on unchanged incoming paint inputs, not just retained boxes.
A separate stage trace confirms zero subtree replays across all 300 animated
stub-font updates; their incoming grid positions keep changing.

The 768-check regression exercises repeated ancestor replacement, earlier
siblings gaining/losing draws, moving origins, fractional clip changes,
rectangular/rounded/unclipped containers, rotation, ancestor effects and
descendant mutations. Every resulting frame is compared with forced complete
layout/paint, including exact geometry and texture pixels. The previous build
fails all 24 buffer-retention assertions. Omitting origin or clip comparison
causes two stale-frame failures each; omitting transform comparison causes
eight. Release passes **589,873 checks** with the complete mutation corpus.
Sanitizer coverage comprises a **589,681-check complete-corpus run** and all
**768 expanded retention checks** against the same runtime implementation.

Eight native layout-stress snapshots remain byte-identical to preview27.
The 47 backend comparison rows are unchanged; all 12 interactive states remain
at zero difference. All 18 Windows/Linux host suites and debug, release and
embedded desktop exports pass, with unchanged example pixel hashes.

Artifact: `weva-godot-preview-20260906-28.zip`, 20 files, 5,346,461 bytes;
SHA-256 `894c09c8cf930766002515ee2bdca803f0626f150f39009ab69462af65fc3895`.
The repository editor still loads its older Windows DLL; these checks use
isolated projects.

## Triangle clipping buffers (2026-09-06)

Preview27 replaces the triangle cutter's temporary dynamic polygons with two
bounded stack buffers, reused across clip pieces. Three half-plane passes can
emit at most 24 vertices from three input vertices, even retaining duplicate
boundary vertices. Output indices are appended in one resize per clipped
polygon. The half-plane arithmetic, interpolation, vertex order and output
geometry are unchanged; no cache or invalidation keys change.

Five interleaved Windows gallery comparisons against preview26 use Godot
4.7 stable, Vulkan Mobile, RTX 5080, a 1520x800 window and engine fonts.
VSync/native input, logging and GPU timing are disabled; each run uses fixed
1/60 steps, 240 warmup and 300 measured frames. No agent build or test runs
during measurement.

| Mode | Mean frame | Median frame | Mean core update |
|---|---:|---:|---:|
| Preview26 | 5.794 ms | 4.896 ms | 3.195 ms |
| Preview27 | 5.553 ms | 4.734 ms | 3.082 ms |

Each column is the median of that statistic across five runs. Four pairs
improve; the first candidate run regresses. Candidate mean frames range
5.324–5.956 ms, p95 9.063–13.165 ms, and maximum 31.236 ms; baseline means
range 5.518–5.932 ms with maximum 33.453 ms. Shared system/GPU load remains
uncontrolled. This is a modest improvement in typical/core time, not a fix
for the still-observed 30 ms frame stalls or a latency guarantee.

Three interleaved headless GCC Release comparisons against preview26, at
1280x720 with stub fonts and 300 animated updates, measure **2.242 -> 2.095 ms**
as medians of run means. Baseline means range 2.216–2.290 ms and candidate means
2.037–2.179 ms. The last update makes **16,737 -> 16,452 allocations** and
allocates **8,332,093 -> 8,301,581 bytes**. This removes 285 allocation calls
from that update; animated paint still allocates.

The existing 2,000-case differential clip oracle compares exact vertex order,
positions, UVs, colors and coverage against the original dynamic algorithm,
including repeated vertices and boundary coincidences. Release and ASan/UBSan
each pass **589,105 checks** with the complete mutation corpus. Eight native
layout-stress snapshots are byte-identical to preview26. The 47 backend
comparison rows retain their preceding results and all 12 interactive states
remain at zero difference. All 18 Windows/Linux host suites and debug, release
and embedded desktop exports pass; exported example pixels are unchanged.

The running repository editor still loads the older DLL with SHA-256
`821f143ef5641ec8600f0ea88af5793143af505dedfcba75544ac277a0cf225b`.
Two interleaved private-gallery comparisons using that DLL versus preview26
measure 13.649–13.739 versus 5.426–5.520 ms mean frame time, and 8.894–8.931
versus 3.185–3.226 ms core updates. Both use the current gallery script,
Godot 4.7 stable, Vulkan Mobile, a 1520x800 window, engine fonts, disabled
VSync/native input, fixed 1/60 steps, 240 warmup and 300 measured frames.
This confirms the loaded-build discrepancy but does not reproduce or explain
every reported 30 ms frame. The repository Windows DLL remains unchanged.

Artifact: `weva-godot-preview-20260906-27.zip`, 20 files, 5,341,914 bytes;
SHA-256 `57d1a64255ac69ce2b998919b7ccd4965a3c7e2958445c17b97234650fb0bb5c`.

## Live theme fonts and texture ownership (2026-09-06)

The host now resolves the Control's theme font and observes Font resource
changes. New font providers invalidate glyph slots as well as measurements.
Renderer replacement releases the old atlas handle through its owner while
retaining published CPU pixel buffers until the next paint. Those changes run
on their input notifications; clean frames do not look up theme resources.
See [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md) for behavior and regression
checks, including the reproduced texture-view use-after-free.

Five interleaved comparisons against preview24 use Windows Godot **4.7 stable**,
Vulkan Mobile, RTX 5080, a 1520x800 window and engine fonts. VSync/native input
are disabled; each run uses fixed 1/60 steps, 240 warmup and 300 measured frames.
Logging/GPU timing are disabled and agent builds/tests are stopped.

| Mode | Mean frame | Median frame | Last Weva update |
|---|---:|---:|---:|
| Preview24 | 5.567 ms | 4.865 ms | 3.215 ms |
| Theme fonts and texture lifetime | 5.854 ms | 4.865 ms | 3.202 ms |

Each column is the median of that statistic across the five runs. Core update
time and typical frames show no clear regression. Whole-frame means increase
in this comparison, with long frames recurring: candidate means range
5.604–6.299 ms, p95 10.540–13.629 ms and maximum 88.038 ms; baseline means range
5.335–5.775 ms and maximum 32.728 ms. Shared system/GPU load remains uncontrolled.
This is a correctness change, with no claimed frame speedup or latency bound;
the previously profiled GPU waits remain open.

All eight native default-font layout-stress snapshots are byte-identical to
preview24. The 47 backend comparison rows are unchanged and all 12 interactive
states remain at zero difference. The new theme suite passes 35 headless and
39 rendered checks on each platform, including custom resource fallbacks and
mutations. Release and ASan/UBSan each pass **589,105 checks** with the complete
mutation corpus. All 18 host suites pass on Windows/Linux; native debug,
release and embedded exports pass 13 runtime/resource and 17 example checks,
with unchanged example pixel hashes. The repository Windows DLL is unchanged.

Artifact: `weva-godot-preview-20260906-26.zip`, 20 files, 5,342,294 bytes;
SHA-256 `693bb07fb318904bc2129001cf9c628c461f4b206529a0934c6b203519920243`.

## Shared vertices through clips (2026-09-06)

Interior triangles now retain their source vertex sharing when they pass
through a polygon clip. Previously each triangle copied three vertices even
when neighbouring triangles referenced the same input index. The map is local
to a clip invocation and is allocated only when a triangle passes. It respects
existing output offsets and leaves coincident vertices with different attributes
distinct. Triangle order, attributes, boundary interpolation and retained-cache
keys are unchanged.

A separate instrumented Windows layout-stress run reduces the final 30 frames'
median uploaded vertices **47,615 -> 42,790** (10.1%), with 87 submissions in both
builds. Instrumented timings are not used as speedup evidence.

Five interleaved comparisons against preview23 use Windows Godot **4.7 stable**,
Vulkan Mobile, RTX 5080, a 1520x800 window, engine fonts, VSync/native input
disabled, fixed 1/60 steps, 240 warmup and 300 measured frames. Logging and GPU
timing are disabled, and no agent builds or tests run during measurement.

| Mode | Frame time | Last Weva update |
|---|---:|---:|
| Preview23 | 5.073 ms | 3.206 ms |
| Shared clipped vertices | **4.820 ms** | **3.134 ms** |

These are medians of run means. Shared system load varies substantially:
baseline means range 4.964–5.744 ms and candidate means 4.769–7.198 ms;
candidate p95 values range 5.447–10.068 ms, with a maximum frame of 18.193 ms.
The lower aggregate is not a controlled latency bound or proof that the
previously observed GPU waits are resolved.

The headless GCC Release benchmark at 1280x720, stub font and 300 animated
updates measures **2.297 -> 2.342 ms** across three interleaved pairs, as medians
of run means. This is a small core-time regression, not a headless speedup.
The last update allocates **16,702 -> 16,737 objects** and **8,665,721 -> 8,332,093
bytes**: the temporary index maps add allocation calls while smaller output
meshes reduce bytes by 3.9%. The intended saving is less geometry copied and
uploaded by the native host; animated repaint still allocates.

All 47 software sample images are byte-identical to preview23 both at time
zero and after 0.83 seconds. All eight native Windows layout-stress snapshots
are also byte-identical. Tests cover shared indices, distinct attributes at
coincident positions, existing output, nested clips, rounded/rectangular clips
and both windings. Linking those tests against preview23's clipper produces
12 failures; the new clipper passes. Release and ASan/UBSan each pass **589,078
checks** with the complete mutation corpus.

The 47 backend comparison rows remain unchanged and all 12 interactive states
remain at zero difference. All 17 host suites pass on Windows/Linux. Six native
batching comparisons are byte-identical on Windows Vulkan and Linux OpenGL.
Debug, release and embedded desktop exports pass 13 runtime/resource and 17
example checks on each platform, retaining the example's previous pixel hashes.
The development editor still uses the older repository DLL; these checks use
isolated projects and the explicitly selected candidate library.

Artifact: `weva-godot-preview-20260906-24.zip`, 20 files, 5,337,644 bytes;
SHA-256 `78bf8c8f53f2fd293159e28963ed86d5231365b618a58c0936eec9242a45559f`.

## Preserving triangles inside clips (2026-09-06)

The convex-clip containment shortcut now preserves triangles with varying
UVs, colors and coverage as well as solid triangles. Previously those interior
triangles were cut against every overlapping piece of the clip's triangulation.
That added vertices even though the clip did not intersect them. It also
changed interpolation and could open one-pixel cracks between the pieces.
Boundary-crossing triangles and concave clips keep their existing algorithm.
This changes no retained-cache keys or C ABI layouts.

The correctness reference is the original, unclipped triangle. Forty-eight
pixel comparisons cover textures, color gradients and alpha ramps, both
windings and eight subpixel positions. The old clipper fails 21 of these
comparisons; the new path passes all 48. Crossing triangles still pass the
original vertex/interpolation oracle, including its 2,000 randomized cases.

Windows Godot **4.7 stable**, Vulkan Mobile, RTX 5080, 1520x800 window,
engine fonts, VSync/native input disabled and fixed 1/60 simulation steps:
five interleaved comparisons against preview22, with 240 warmup and 300
measured frames per run. Logging and GPU timing are disabled.

| Mode | Frame time | Last Weva update |
|---|---:|---:|
| Preview22 | 5.709 ms | 3.474 ms |
| Preserve interior triangles | **5.079 ms** | **3.200 ms** |

Values are medians of run means: **11.0% less frame time** and **7.9% less
core update time**. New means range 4.991–5.189 ms, with p95 5.974–6.520 ms.
These runs had no long GPU waits; the previously observed stalls remain open.
Separate instrumented runs show the final 30 frames' median vertex count
falling **54,077 -> 47,615**, with 87 submissions in both builds.

The headless GCC Release benchmark at 1280x720, stub font and 300 animated
updates measures **2.734 -> 2.333 ms** across three interleaved pairs, as
medians of run means. The last update's allocations fall **16,728 -> 16,702**,
and allocated bytes **10,043,833 -> 8,665,721** (13.7% fewer bytes).

Pixel changes were inspected rather than regenerated as goldens. Of the 47
software samples, static `episode-stats` changes 5,780 pixels by at most 4/255
in its thin textured gradient meters; `layout-stress` changes one pixel by
1/255 and `match3` one by at most 2/255. After 0.83 seconds, only the same
`episode-stats` and `match3` differences remain. All other sample pixels match.
These differences arise from retaining original attributes instead of adding
intermediate interpolated vertices; the direct-render tests above establish
the intended behavior when clipping has no geometric effect.

Eight native Windows layout-stress snapshots change 11–37 pixels each. Five
high-contrast differences fill previously missing pixels inside gradient bars
at frames 45, 90 and 126; every other difference is at most 1/255 per channel.
The 47 software/Godot backend comparison results remain
unchanged, and all 12 interactive states remain at zero difference. Six native
batching comparisons pass byte-for-byte on Windows Vulkan and Linux OpenGL.
All 17 host suites and debug/release/embedded desktop exports pass on both
platforms; the installed example retains its previous pixel hashes.

Release and ASan/UBSan each pass **588,998 checks** with the full mutation
corpus. The count changes with the smaller emitted vertex lists; no test
cases were removed. The 48 direct-render comparisons are additional cases.

Artifact: `weva-godot-preview-20260906-23.zip`, 20 files, 5,336,284 bytes;
SHA-256 `a4eb54258b75c986d336dcdfb56f092e6c66857cdb14446c0c3a625ed8b8e54b`.

## Positioned font shaping regression check (2026-09-06)

The native font adapter now preserves glyph placement offsets, UTF-8 source
clusters and the exact automatic fallback font. A one-run memo bridges its
count/fill callbacks so each cache miss shapes once in TextServer. The core's
existing shaped-run cache remains in place. See [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md)
for the additive ABI and native correctness checks.

The same Windows Godot 4.7 / Vulkan Mobile gallery protocol below, with five
interleaved pairs, compares preview21 with the positioned-shaping candidate:

| Mode | Frame time | Last Weva update |
|---|---:|---:|
| Preview21 | 6.236 ms | 3.534 ms |
| Positioned shaping | 6.211 ms | 3.487 ms |

These are medians of run means, with 240 warmup and 300 measured frames per
run, engine fonts, 1520x800 and fixed 1/60 simulation steps. Logging and GPU
timing are disabled. The difference is within the run variation; this check
shows no clear steady-state regression and does not claim a speedup. Candidate
run means range 5.839–6.317 ms, with p95 values 6.731–7.323 ms. Long GPU waits
recur in both builds, reaching 86.253 ms in the candidate. They remain open.
All eight native layout-stress snapshots match preview21 byte-for-byte.

Release and ASan/UBSan each pass 589,134 checks. Native font adapter/document
checks pass 547 on Windows and 457 on Linux. The 47 backend results are
unchanged, all 12 interactive states remain at zero difference, and all 17
host suites and desktop export checks pass on both platforms.

Artifact: `weva-godot-preview-20260906-22.zip`, 20 files, 5,336,374 bytes;
SHA-256 `8361efee80c87b5357aee16cf4283d2d1f3a202136c58a80cb9b73cf5850a000`.

## Reusing resolved decoration values (2026-09-06)

New `WEVA_PAINT_LOG` sub-scopes attribute plain fill/border construction,
their draw calls and color resolution. These are nested "of which" counters;
subtracting them from the existing buckets would double-count their work.
The previous layered-background scope did not include ordinary solid fills.

Color resolution now uses `ComputedStyle`'s existing parsed-value cache for
registered properties. Its writes/unsets already invalidate the slot, and
inherited reads share the current ancestor's parsed value. Public calls for
custom/unregistered names retain on-demand parsing. The recursive paint walk
also passes its resolved radii and background color into decoration painting,
avoiding a second resolution. The public standalone decoration entry point
uses the same implementation. Draw geometry and cache keys are unchanged.

Windows Godot **4.7 stable** (`5b4e0cb0f`), Vulkan Mobile, RTX 5080, 1520x800,
engine font, VSync/native input disabled, fixed 1/60 simulation steps:
five interleaved comparisons against preview20, each with 240 warmup and 300
measured gallery frames. Logging and viewport/GPU timing are disabled.

| Mode | Frame time | Last Weva update |
|---|---:|---:|
| Preview20 | 6.101 ms | 3.853 ms |
| Reused decoration values | **5.632 ms** | **3.365 ms** |

Values are medians of the five runs' means: **7.7% less frame time** and
**12.7% less core update time**. New means range 5.581–5.722 ms, with p95
6.665–7.156 ms. No long GPU wait occurred in these runs; the earlier observed
waits still require a controlled investigation and are not fixed by this change.

Separate instrumented runs, using the final 30 frames' medians, reduce color
resolution 0.32 -> 0.09 ms and decoration construction 0.67 -> 0.38 ms.
These nested scopes explain the saving and are not added together.

The headless GCC Release benchmark (1280x720, stub font, 300 animated updates,
three interleaved pairs) measures 2.974 -> 2.488 ms, as medians of run means.
Its last update allocates **24,549 -> 16,728 objects** (31.9% fewer), and
**10,259,497 -> 10,043,833 bytes**. The style retains parsed color values until
the corresponding inputs change or the style is released.

All 47 software-rendered samples match preview20 byte-for-byte at time zero
and after advancing 0.83 seconds. All eight real-font Windows layout-stress
snapshots also match. Mutation checks cover inherited colors, own overrides,
unsets, reparenting, invalid color types, cleared styles and custom properties.

Release and Clang ASan/UBSan each pass **589,097 checks** with the full mutation
corpus. The 47 backend comparison results are unchanged and all 12 interactive
states remain at zero difference. All 17 Windows/Linux host suites pass.
Native debug, release and embedded-pack exports pass 13 runtime/resource and
17 example checks on each platform; example pixels match the preceding build.

Artifact: `weva-godot-preview-20260906-21.zip`, 20 files, 5,328,792 bytes;
SHA-256 `b1d71c79de248aa022ec7f2d5b6bab6d4a9d5b3a36e140a263d91c0c114832ff`.

## Solid triangles inside rounded clips (2026-09-06)

Triangles with constant color, coverage and UVs now pass through a convex
clip when all three vertices lie in every inward half-plane. The existing
inscribed-rectangle check misses long triangles inside thin rounded bars;
cutting these against each piece of the clip's triangulation created needless
geometry. Vertex containment is evaluated lazily and shared within one mesh
and clip invocation. Boundary crossings and varying attributes retain their
previous clipping and interpolation path. No retained-cache keys change.

Windows Godot **4.7 stable** (`5b4e0cb0f`), Vulkan Mobile, RTX 5080, 1520x800,
engine font, VSync/native input disabled, fixed 1/60 simulation steps:
five interleaved old/new DLL comparisons, each with 240 warmup and 300 measured
gallery frames. Both builds include consecutive upload batching and frame-ID
diagnostics. Logging and viewport/GPU timing are disabled during measurement.

| Mode | Frame time | Last Weva update |
|---|---:|---:|
| Previous clipping | 7.271 ms | 4.410 ms |
| Contained solid triangles | **6.311 ms** | **3.899 ms** |

Values are medians of the five runs' means: **13.2% less frame time** and
**11.6% less core update time**. New run means range 6.184–6.466 ms, with
p95 values 7.153–7.770 ms. The external GPU load described below remains;
occasional stalls reach 87 ms in both builds. This CPU improvement does not
resolve those GPU waits or establish a frame-latency bound.

A separate instrumented pair, taking the median of the final 30 draws,
reduces vertices **79,082 -> 54,077** while keeping 87 submissions. Godot draw
time falls 2.084 -> 1.566 ms: packing 0.821 -> 0.577 ms and submission
1.230 -> 0.926 ms. These scopes explain the saving; the table above comes
from the runs without instrumentation.

The headless GCC Release benchmark at 1280x720 with stub fonts, 300 animated
updates and three interleaved pairs measures 3.166 -> 2.891 ms (median of run
means). Its last update allocates **24,561 -> 24,549 objects** and
**13,019,656 -> 10,259,497 bytes**. The per-clip containment buffer is outweighed
by smaller emitted meshes; this does not make animated painting allocation-free.

All 47 software-rendered samples match the previous build byte-for-byte both
at time zero and after advancing 0.83 seconds. Six native render fixtures
also match on Windows Vulkan and Linux OpenGL. Eight Windows snapshots of
layout-stress using real engine fonts identify one intentional correction:
five snapshots lose a single dark pixel inside a solid green bar; every other
pixel is unchanged. At time zero, pixel (95,684) changes from the bar's dark
background `#0d1117` to its fill `#34d399`, matching its neighbors and lying
inside the fill's rectangle (40,682,126.5333,6). Avoiding artificial triangle
splits removes this seam. An unrestricted prototype also altered gradient
pixels and was narrowed to constant attributes before validation.

Release and Clang ASan/UBSan each pass **589,083 checks**, including the full
incremental corpus, both clip windings, translucent solids, boundary crossings
and exact interpolation checks for varying attributes. The 47 backend results
are unchanged; all 12 interactive comparisons remain at zero difference.
All 17 Windows/Linux host suites and native debug/release/embedded exports
pass, with example pixels unchanged. These libraries are packaged in preview20.

Artifact: `weva-godot-preview-20260906-20.zip`, 20 files, 5,327,671 bytes;
SHA-256 `a043f0be40317046d9892585307c1eac9053f632d00308eae785e08ee95bd9f2`.

## Attributing long Windows frames (2026-09-06)

`layout_stress_probe.gd` now accepts `WEVA_GALLERY_PROBE_TRACE=/path/frames.csv`.
It writes frame IDs, wall intervals, last core update, and timestamps around
Godot's `frame_pre_draw` / `frame_post_draw` signals after measurement ends.
These IDs match the new frame suffix in `WEVA_GODOT_DRAW_LOG`. Phase durations
are NaN if asynchronous callbacks do not bracket the same completed frame.

GPU/viewport timing is now opt-in with `WEVA_GALLERY_PROBE_GPU_TIMING=1`.
Disabled timing is reported as unavailable, with empty CSV fields. The earlier
tables below used viewport timing enabled; set this flag when reproducing
those conditions. Turning it off does **not** fix the long-frame problem:
three interleaved pairs of 2,000-frame runs observed frames over 50 ms with
both settings. Normal frame means remained around 7.1–7.6 ms.

Correlated traces locate the 70–90 ms outliers after Weva has submitted its
geometry, between Godot's pre/post render signals. A native Windows main-thread
sample and `StackWalk64` unwind on Godot 4.7 (`5b4e0cb0f`) identify:

```text
RenderingDevice::_stall_for_frame
  -> Vulkan driver fence_wait
  -> vkWaitForFences
  -> NVIDIA nvoglv64.dll
  -> Windows WaitForMultipleObjects
```

The Vulkan loader's call site and Godot's fence call were checked against
the loaded binaries. The wait is for earlier GPU work to complete. The sampled
long frames contain roughly 4–5 ms of core update and 2–3 ms of upload work,
followed by approximately 70–80 ms in that wait. Native thread suspension is
diagnostic instrumentation; its timings are not another performance benchmark.

After the test exited, NVIDIA reported **99% GPU utilization** and Windows
attributed **98.8% of the 3D engine** to a separate running application. This
is consistent with shared GPU contention; its contribution still needs a
controlled comparison without that load. The current wall-frame tail is not
an isolated measurement of Weva. Core update and upload scopes remain useful
for directing CPU work.

Controls ruled out several simpler explanations: the stalls also occur with
gallery statistics hidden, a standalone document, and the stub font. Replaying
ten captured mesh/texture frames without Weva stayed below 9 ms in the recorded
runs, including fresh packed-array copies and added CPU work. Running the
document hidden while replaying those draws stayed below 10 ms. Those controls
did not reproduce the wait and do not establish a driver or addon defect.
The capture hook was removed after collecting the data. No runtime optimization
or fix for the wait is claimed from this investigation.

## Consecutive Godot triangle uploads (2026-09-06)

The host now combines adjacent ordinary meshes using the same texture into
one upload. Painter order and vertex attributes are preserved; backdrop
copies and SDF materials end a run. Combined uploads stop at 65,536 vertices,
and a single larger mesh keeps its existing path. No cross-frame cache or
core geometry change is involved.

The user's Windows Godot **4.7 stable** (`5b4e0cb0f`), Vulkan Mobile, RTX 5080,
1520x800, engine font, VSync/native input disabled: five interleaved sweeps
of 240 warmup and 300 measured gallery frames at fixed 1/60 simulation steps.
Both modes use the same final DLL, with `WEVA_GODOT_DISABLE_BATCHING=1`
selecting individual uploads. Logging is disabled during timing.

| Mode | Frame time | Last Weva update |
|---|---:|---:|
| Individual uploads | 8.618 ms | 4.478 ms |
| Consecutive uploads | **7.435 ms** | 4.499 ms |

These are medians of the five runs' means: approximately **14% less frame
time**, with core update cost effectively unchanged. Batched means range
7.152–7.832 ms and p95 values 8.262–10.447 ms. Occasional long stalls remain:
the worst measured batched frame is 77.656 ms, versus 81.768 ms unbatched.
This improvement does not establish a bound on frame latency.

A separate instrumented comparison, using the last 30 draws after the same
warmup interval, holds geometry at 85,834 vertices and reduces submissions
from **281 to 87**. Median Godot draw time is 3.030 -> 2.123 ms, of which
RenderingServer submission is 2.016 -> 1.238 ms; packing is 0.919 -> 0.867 ms.
Those scopes explain the saving but are not the uninstrumented frame benchmark.

`check_triangle_batching.py` compares six exact images with batching on/off:
overlapping transparency, clipped gradients and textures, backdrop filters,
SDF material boundaries, a large batch split and an empty replacement. All
are byte-identical on Windows Vulkan/OpenGL and Linux OpenGL, including the
user's Godot 4.7. The large fixture preserves 215,927 vertices while reducing
1,401 submissions to four. The 47 sample backend results are unchanged; all
12 interactive comparisons remain at zero difference. All 17 Windows/Linux
host suites and native debug/release/embedded exports pass, with example
pixels unchanged. Release and ASan/UBSan each pass 375,184 core checks.

The clipping investigation also adds `WEVA_CLIP_LOG=1`: each actual polygon
cut reports bounds, clip points, input/output vertices, triangles and elapsed
time. Rounded progress bars can expand 73 input vertices to roughly 2,300.
Exact vertex deduplication was discarded: it added approximately 0.4 ms of
core work and produced only a small net frame improvement. Fixed clip storage
and early triangle rejection also failed to show a reliable benefit. The
shipped clipper retains the preceding math and triangle stream.

## Layout-stress in the Windows gallery (2026-09-06)

The repository's Windows DLL was still the 2026-09-05 build (SHA-256
`821f143ef5641ec8600f0ea88af5793143af505dedfcba75544ac277a0cf225b`),
while the tested newer libraries lived in isolated preview builds. The
gallery also called `update_document(delta)` in its parent process callback
while `WevaDocument` advanced itself. A native clock regression measures
twice the expected animation movement before removing that duplicate call.

The actual rendered gallery was measured on Windows, Godot 4.7.2, Vulkan
Mobile, RTX 5080, 1520x800, engine font, VSync disabled and native input
disabled. `layout_stress_probe.gd` uses 240 warmup frames and 300 measured
frames at a fixed 1/60 simulation step. Three interleaved sweeps reverse the
variant order on the middle sweep. Values below are medians of the sweeps'
means; these are wall intervals between frames, not the Godot process monitor.

| Library and gallery | Rendered frame | Last Weva update |
|---|---:|---:|
| Repository DLL, duplicate gallery update | 23.327 ms | 8.885 ms |
| Current retained-layout DLL, one update | 9.399 ms | 4.503 ms |
| Also reuse identical RGB conversions during upload | **8.781 ms** | 4.505 ms |

A separate confirmation using the user's Godot **4.7 stable** executable
(`5b4e0cb0f`) measures **8.514 ms mean frame time**, 8.199 ms median and
10.174 ms p95, with 4.473 ms in the last Weva update. This is one confirmation
run using the same rendered fixture, not the interleaved comparison above.

The approximately 62% reduction combines the earlier core improvements with
the gallery clock fix and a 0.6 ms upload improvement. It is not a new 62%
layout optimization. The last-update column excludes the duplicate call in
the old gallery and Godot drawing, which is why it cannot describe total
frame cost. Final runs' wall-frame p95 values were 11.16–12.74 ms; individual
maxima reached 18.73 ms. Godot's `Performance.TIME_PROCESS` publishes a rolling
maximum once per second (`process_max` in `main.cpp`), rather than the current
frame duration; the probe labels it separately.

`WEVA_GODOT_DRAW_LOG=1` adds scopes for the host's complete draw, PackedArray
packing and RenderingServer submission. The latter remains about 2.1 ms for
roughly 90,000 vertices in the sampled animation phase. Reusing the exact sRGB
conversion for adjacent equal RGB bit patterns lowers packing from roughly
1.6–2.2 ms to 1.0 ms. Alpha is copied independently, preserving coverage ramps.
The memo lasts only for a draw and allocates no cache storage.

The core stage profile attributes roughly 0.5 ms to box preparation, 0.01 ms
to scoped layout and 4–5 ms to paint in the sampled frames. Paint submission
and polygon clipping account for roughly 2.4 ms; text/glyph work is small.
Further work should address that measured paint/upload cost. Cold loading,
hover restyling, full-page mode and other hardware need separate measurements.

Run the gallery probe from its project with `godot --path . --script
res://layout_stress_probe.gd --fixed-fps 60 --resolution 1520x800`. Use
`WEVA_STAGE_LOG=1`, `WEVA_PAINT_LOG=1` or `WEVA_GODOT_DRAW_LOG=1` for attribution;
timed comparisons above leave logging disabled.

## Retaining layout-stress's grid during animation (2026-09-05)

The animated counters change the live panel's height, so the surrounding flex
column must reflow. Its large grid still receives the same width. A probe now
defers that clean grid, checks its actual width and box edges during layout,
and retains its cells and text while rebuilding the small surrounding column.
Input versions exclude changed grids. A width mismatch materializes the grid
and runs ordinary layout; percentage heights depending on auto-height parents
also retain the full path. The enclosing column must pass the existing export
checks before any replacement is committed.

Release core, stub font, 1280x720: five interleaved sweeps of 300 updates at
1/60 second, reversing variant order on alternate sweeps. These are medians of
each sweep's **mean**, covering animation reversals rather than selecting the
cheapest frame. A is the immediately preceding build described below.

| layout-stress animation | ms/update |
|---|---:|
| A: previous subtree implementation | 6.832 |
| Retained grid only | 3.594 |
| Retained grid and clipping change | **3.418** |

Godot 4.7.2 with its real engine font, five interleaved runs of 300 updates
using `WEVA_PROBE_FRAMES=300` and `incremental_probe.gd`, confirms the animation
at **8.334 -> 3.520 ms/update** (58% less time). The leaf padding update remains
0.339 -> 0.324 ms, and its paint-only update 0.282 -> 0.277 ms. Cold loading is
27.733 -> 26.974 ms; no cold-load improvement is claimed from these five runs.
The extra cache bookkeeping has a small cost elsewhere: stats's leaf padding
update measures 0.579 -> 0.621 ms in the same host runs. Hud's animated update
is 0.186 -> 0.196 ms.

The grid path removes about half the update time. The remaining paint profile
points to polygon clipping. Its triangle cutter now carries each vertex's
half-plane distance into the next edge test and reuses those distances for
interpolation. Cut order, float interpolation, and emitted vertex order stay
the same. A differential test compares 2,000 seeded cases, including repeated
vertices and points on clip edges, against the old dynamic-polygon algorithm.
All **47 sample renders are byte-identical** before and after this clipping
change.

A fixed-buffer clipping experiment was discarded: with retained grid layout
already enabled, it measured 3.507 -> 3.738 ms/update in a separate interleaved
run. Reducing allocations alone did not make that version faster.

On the final warmed frame of the 300-update sequence, allocations fall from
26,734 to 24,561; allocated bytes are effectively unchanged at 13,025,756 ->
13,028,320. Draws still use absolute coordinates and CPU clipping, so a moved
grid repaints and the host still receives the complete draw list. This change
does not reuse previously clipped geometry under a different clip.

The regression suite compares animated flex columns and rows with full
recomputation through reversals, viewport changes, grid padding and label
mutations, scrolling, and auto-parent percentage heights. A storage test
repeatedly replaces enclosing wrappers while keeping two descendants' IDs and
imported text alive. Stage logging reports the number of grids retained.
The complete mutation corpus passes **576,944 checks, 0 failures** in both
Release and ASan/UBSan. `check.sh` passes all gates, including the 47-sample
backend comparison, all 12 interactive states and the Godot host suites. The
three known harvest metric differences remain recorded, with unchanged
tolerances and form/font defaults.

## Faster cold filters and safe positioning boundaries (2026-09-05)

This follows the initial subtree work below. A is the build immediately before
this follow-up, already including retained layout and paint; B includes the
changes described here. Release core, stub font, 1280x720. Cold figures are
medians of 28 instrumented loads per binary (seven interleaved sweeps, four
loads per sweep), not comparisons between unrelated single runs.

| page / scope | A -> B, ms |
|---|---:|
| hud filter convolution | 19.240 -> **13.600** |
| hud filter background raster | 12.520 -> **10.920** |
| hud filter path total | 32.290 -> **25.335** |
| hud total cold paint | 78.235 -> **70.825** |
| neon filter path total | 62.315 -> **50.180** |
| neon total cold paint | 64.745 -> **52.560** |
| match3 filter path total | 32.225 -> **25.920** |
| match3 total cold paint | 54.250 -> **47.105** |
| glass total cold paint, no filter textures | 65.700 -> 64.885 |

The four-channel convolution carries wider contiguous strips through the
vertical pass. The one-channel shadow kernel retains its previous strip size.
The byte conversion uses bounded nonnegative truncation after adding 0.5 in
double, preserving the original float product and `lround` result even just
below a half. Square-corner filter rasters copy complete rows into the padded
image. Radius, resolution, sampling, convolution passes and accumulated
precision stay unchanged. A scalar reference checks all output bytes across
small, narrow, partial-strip and large images, including transparent RGB.
All **47 sample renders are byte-identical** to A.

Layout can now replace a static subtree beneath or beside unchanged relative,
absolute and fixed boxes. It still rejects positioned descendants, global
float/sticky/anchor dependencies, and the other unsupported boundaries below.
The corpus exposed why final dimensions alone are insufficient: flex shrinking
could hide a button's added padding, leaving its centred siblings in their old
positions. Flex and grid now record the measurements they consumed **before**
allocating item sizes. A probe checks those previous inputs against a new
measurement, before imposing the old allocated width. The record is copied
with the box and cleared when its arena slot is recycled.

Sanitizing the expanded corpus also exposed shallow copying of prepared box
trees when their vector grew. Their owned strings were copied without
rebinding text views, which then dangled into the destroyed original tree.
`BoxTree` is now move-only, and a regression grows a vector of trees containing
short and heap-backed text before moving and importing each subtree.

Five interleaved sweeps of 60 updates, medians of each sweep's best update:

| page / leaf padding update | A -> B, ms |
|---|---:|
| stats | 1.808 -> **0.385** |
| layout-stress | 0.158 -> 0.163 |
| hud | 1.325 -> 1.344 |
| glass | 4.205 -> 4.145 |
| vendor | 2.412 -> 2.545 |
| load-game | 0.604 -> 0.608 |

Stats becomes eligible for scoped layout; the other fallback rows are not
claimed as wins. Paint-only timings remain effectively unchanged. The stronger
proof has a cost: five doubles per box, an additional natural-size probe for
imposed widths, and extra intrinsic measurements where the parent did not
already need them. Layout-stress's leaf layout update allocates 1,702 times /
435,099 bytes, versus 1,698 / 393,431 before this follow-up. Stats's leaf layout
update drops from 15,213 allocations / 2,177,051 bytes to 3,889 / 1,822,025.

The real-font Godot probe, five interleaved runs of 100 updates, confirms the
stats padding update at **2.702 -> 0.618 ms**. Layout-stress's leaf padding
update is 0.313 -> 0.306 ms; its continuous animation is **7.827 -> 8.136 ms**.
That remaining full-layout cost is not solved here. `WEVA_LAYOUT_LOG=1` now
names rejected probes: animated counters change the live panel's height, and
the enclosing flex column must allocate a different height and origin to the
large scrollable grid. Its old geometry cannot simply be retained.

Reproduce a cold scope with `WEVA_PAINT_LOG=1 weva_bench <html> <css> 4 --cold`;
use `--full --mutate=layout --target=last` for leaf updates and
`--full --dt=0.016666667` for animation. `incremental_probe.gd` now covers stats
as well as hud and layout-stress. The complete mutation corpus reports
**455,108 checks, 0 failures** in both Release and ASan/UBSan builds, including
the ownership regression. The three known harvest differences remain
recorded against the Chrome conformance decision in ORACLE.md.
`check.sh` passes, including the 47-sample Godot backend comparison, all 12
interactive states and the host/demo/binding/hover suites.

## Subtree updates and attributed cold filters (2026-09-05)

`WEVA_PAINT_LOG` now measures the separate `filter: blur()` path, with raster,
convolution and upload sub-scopes. These are disjoint from the background,
shadow and text totals; only the top-level filter total is subtracted from
`rest`. One Release core run on hud measured **72.94 ms** cold paint:

| work | ms |
|---|---:|
| ordinary backgrounds | 38.53 |
| filter background rasterization | 11.52 |
| filter convolution | 17.33 |
| filter upload | 0.40 |
| filter path total, including overhead | 29.35 |
| unattributed rest | 0.86 |

Through Godot with the engine font, the same scope measured **81.31 ms**
paint: **34.19 ms** filters (13.49 raster, 20.34 blur, 0.24 upload), 2.08 ms
rest. These are individual instrumented cold runs, not A/B medians. Warm
updates reuse the filter texture. Instrumentation changes no raster pixels.

Layout builds changed subtrees separately and splices only when outer
dimensions, margins, baselines and intrinsic contributions stay equal. Failed
probes promote to a parent. A changed flex/grid item is reflowed by its parent;
an unchanged item can reuse its allocated width when reflowing descendants.
Every candidate is checked before any splice. Unaffected boxes retain their
IDs; replaced slots are recycled. Imported runs own scratch-backed text and
preserve DOM slices for textarea selection/carets. A 200-splice test checks
bounded storage.

Paint records command ranges per box, keyed on propagated input versions.
Changed styles invalidate descendants (inherited effects) and ancestors
(subtree output). Clean ranges move existing command buffers into the next
frame and retain referenced textures. A changed glyph-atlas handle prevents
reuse. The host receives a complete ordered draw list. Custom render-backend
callbacks retain the full path; Godot uses the collecting backend and receives
the optimization.

Interleaved A/B, Release core with the stub font, 1280x720, five sweeps of 60
updates; median of each sweep's best update. A is the core at `2c71bc95`,
B the new core. Mutations target the last element, a leaf:

| page | layout flip A -> B, ms | paint flip A -> B, ms |
|---|---:|---:|
| layout-stress | 5.630 -> **0.151** | 2.951 -> **0.136** |
| hud | 1.248 -> 1.244 | 0.679 -> **0.049** |
| stats | 1.754 -> 1.744 | 0.605 -> **0.065** |
| glass | 3.987 -> 4.015 | 0.972 -> **0.076** |
| vendor | 2.323 -> 2.332 | 0.439 -> **0.065** |

The layout-only delta on layout-stress is 2.679 -> 0.015 ms. The other layout
rows retain the full fallback; their small differences are not claimed as
wins. Full fallback remains for external constraints/structure, positioned or
floated documents, table/multicol/pseudo/counter subtrees, changes whose
exported sizes differ, and large or numerous dirty regions. In particular,
layout-stress's continuous width/font-size animations still need full layout
when outer sizes change: one Godot probe measured **8.25 ms** for that update.
This change does not eliminate that cost.

The 100-update host probe with the engine font averaged 0.296 ms for the
layout-stress leaf padding change and 0.268 ms for its paint change, including
the GDScript setter and update call. Hud's animated update averaged 0.208 ms.

The full-update benchmark now counts allocations in the final warmed update,
excluding the caller's mutation. On layout-stress:

| update | allocations A -> B | allocated bytes A -> B |
|---|---:|---:|
| leaf layout | 27,833 -> 1,698 | 12,737,735 -> 393,431 |
| leaf paint | 25,467 -> 1,621 | 12,430,421 -> 323,113 |

This reduces allocations; it does not reach zero. Glyph preparation still
walks the tree, and replaying/publishing the complete draw list costs work
proportional to visible output. `Tools/flipbench.sh` now defaults to a leaf;
`WEVA_FLIP_TARGET='*'` measures a root change. The real-font host probe is
`hosts/godot/project/incremental_probe.gd`, run headlessly with
`--script incremental_probe.gd`; `WEVA_PROBE_FRAMES=1 WEVA_PAINT_LOG=1` gives
the cold breakdown without a long warm log.

`check.sh` now runs the incremental corpus: nine updates per sample, comparing
geometry, effect parameters, texture pixels and all element bounds against
full recomputation, with identical animation and glyph histories. The three
pre-existing harvest failures remain unchanged; see ORACLE.md for the decision
to use Chrome as the conformance target.

## Earlier measurements

Measured numbers, and the reasoning behind which ones matter. Re-measure with
the two tools in `Tools/` rather than trusting this file — it is a snapshot,
and the point of writing it down is that a later measurement can disagree with
it visibly.

    Tools/weva_bench      one layout pass, boxes and allocations
    Tools/framebench      idle, hover and animating FRAMES on a real page
    Tools/scrollbench     what a scrolled list costs per frame

All figures below: release build, 1280x720, the corpus in
`Tools/oracle/corpus/samples`.

## What a frame costs

The number a game feels is the per-frame one, not the load. Four cases, in
rising order of how much work they are:

| case | cost | why |
|---|---|---|
| idle | **0.000 ms** | nothing moved, and the update discovers that before doing anything |
| hover | **0.3–2 ms** | the elements whose `:hover` flipped restyle, and nothing else |
| scroll | **0.5 ms** at 200 rows, **4.2** at 2,000 | offset plus a repaint of what is visible |
| animating | **~1 ms** typical (hud 0.88, combat-hud 1.0) | only what is moving |

`layout-stress` is the exception at **8.3 ms** a frame, and deliberately: it
animates `width`, `font-size` and `padding` on purpose, so the relayout path
runs every frame on 6,926 boxes. It is the worst case the corpus can build,
and it is still inside a 16 ms budget.

## What a load costs

First update, which is parse-free (the HTML is already a tree) but does
everything else for the first time:

| page | boxes | first update | dominated by |
|---|---|---|---|
| sample-menu | 123 | 0.03 ms | — |
| menu | 1,313 | 0.4 ms | — |
| layout-stress | 6,926 | 26 ms | cascade 15, layout 8 |
| vendor | 8,162 | 22 ms | cascade 6, layout 6 |
| hud | 1,932 | **127 ms** | **paint 128** |
| glass | 2,299 | **156 ms** | **paint 156** |

hud and glass are not slow because they are big. They are slow because they
rasterise gradient backgrounds and blur them, and the first paint is where
that happens: five million gradient-stop evaluations and 52 blurs on hud
alone. It is a ONE-TIME cost -- the results go into the texture cache, and the
same page's next paint is 1.7 ms -- so it shows up as a hitch when a menu
opens and never again.

Worth knowing before optimising it: every candidate change (rasterising smooth
gradients at reduced resolution, a colour lookup table instead of walking the
stop list) moves pixels, and the render gates compare against the Godot
backend at 0.00%. A faster gradient has to prove it draws the same picture.

## What was slow and is not

Kept because the reasoning is what stops it coming back, not the number.

**A scroll used to restyle the document.** `only_time` treated any pending
invalidation as a reason to re-run the cascade, so scrolling a 2,000-row list
spent 80 ms a frame in the cascade discovering nothing had changed. Scrolling,
a blinking caret and a moving tooltip all ask for `Invalidation::Paint` and
none of them can change a computed style. 100.6 ms -> 4.2 ms.

**Paint walked every box.** The clip already threw the meshes away, but
building them cost ~2 us a box, so a long list did 46,000 boxes of work to
produce the twenty draws you can see. Boxes carry a visual-overflow rectangle
now and paint returns immediately when a subtree cannot reach the clip.

**Moving the mouse restyled the page.** A hover chain is the element and every
ancestor up to `<body>`, so marking both chains whole put `<body>` in the
changed set on every pointer move -- and a touched-subtree walk from `<body>`
is the whole document. Only the elements that actually flipped are marked now.
66.6 ms -> 11.3 ms on layout-stress, 10.5 -> 0.46 on stats.

**The occupancy flags were a bitset pretending to be a byte array.**
`ComputedStyle::occupied_` was a `std::vector<bool>`, so every presence check
-- and `get` does one for every property read in a layout pass -- paid a shift
and a mask to extract one bit. The C# this ports keeps a `bool[]` beside its
`ulong[]` bitset explicitly "for single-load hot readers", and the header here
said so; `std::vector<bool>` simply is not that. As `uint8_t` it is one load,
and the bitset is still there for the word-at-a-time walks that want it. Two to
nine per cent, measured interleaved: vendor -9.4%, stock-dashboard -6.6%,
randhtml -5.8%, glass -5.6%. It costs a byte per property per style rather
than a bit, about a megabyte on the largest page in the corpus.

**Every box resolved four zeroes it did not have.** `resolve_box_sides_px`
substitutes "0" for an absent side, so a box declaring no margin and no padding
-- which is most of them -- put four zeroes through keyword matching, a
parsed-value lookup and a length resolution to arrive back at zero, twice per
box per pass. Recognising them costs four string compares and was worth 5 to
16 per cent on its own, the largest single change in this sequence.

**font_size_px re-derived a number that had not changed.** It runs several
times for every box and again for the parent, and a `calc()` font-size was
evaluated afresh on each -- CssCalc::evaluate was the second-largest cost in a
randhtml pass, behind only grid layout. The style now remembers the answer with
the parent size it was resolved against, keyed on the style's VERSION so any
write invalidates it automatically. The first attempt invalidated by hand at
each mutation and missed the main `set()`; tying it to the version cannot miss.

**A layout pass hashed its property names over and over.** `get(name)` and
`parsed(name)` each resolve the name themselves, so `resolve_length` and
`font_size_px` hashed the same property twice per call -- and font_size_px runs
several times for every box, recursively for the parent as well. Sampling put
`ComputedStyle::get` and `CssPropertyRegistry::id_of` together at 28% of a pass
on randhtml, ahead of any layout algorithm. The ids are resolved once now,
which is safe because the registry keeps an id stable across re-registration
for exactly this reason.

**An anonymous box parsed four zeroes per pass.** `resolve_box_sides_px`
substitutes "0" for every absent side; "0" is not a keyword, and with a null
style there is no memo to hold the parsed result, so a box with no style
re-parsed the same four zeroes on every layout. 3,192 allocations on randhtml,
more than a fifth of the pass. It answers zero directly now.

**A shorthand's sides were re-parsed per side per pass.** `margin: 10px`'s
parts have no longhand slot to memoise against, so each side parsed its
substring again every layout. They read components of the shorthand's own
memoised parse now.

**size_tracks copied its contributions.** It sorts them, so it cannot take
them by reference -- but taking them by value allocated a fresh vector on
every call, seven per grid. It copies into a pooled buffer now, the same
pattern flex and inline layout already use.

Together, measured at 40 passes so the numbers are outside the +-0.5% noise:

| page | before | after |
|---|---|---|
| match3 | 1.507 ms | **1.349** |
| flex-playground | 1.828 | **1.658** |
| layout-stress | 4.721 | **4.336** |
| randhtml | 3.395 | **3.143** |
| glass | 1.648 | **1.586** |
| vendor | 3.265 | **3.191** |

randhtml's allocations fell 16,663 -> 12,195 with it.

With the id conversion and the font-size memo on top, against the same
baseline:

| page | before | after |
|---|---|---|
| match3 | 1.507 ms | **1.057** |
| randhtml | 3.395 | **2.600** |
| stock-dashboard | 1.205 | **0.934** |
| flex-playground | 1.828 | **1.425** |
| layout-stress | 4.721 | **3.738** |
| glass | 1.648 | **1.310** |
| stats | 1.807 | **1.484** |
| vendor | 3.265 | **2.758** |

About a fifth to a third of a layout pass, depending on the page.

And with the zero fast path on top, against that same baseline:

| page | before | after |
|---|---|---|
| match3 | 1.507 ms | **0.893** |
| stock-dashboard | 1.205 | **0.793** |
| layout-stress | 4.721 | **3.249** |
| flex-playground | 1.828 | **1.268** |
| randhtml | 3.395 | **2.413** |
| vendor | 3.265 | **2.463** |
| glass | 1.648 | **1.242** |
| stats | 1.807 | **1.391** |

A quarter to two fifths of a layout pass.

Worth recording because it cost an hour: the first three of these were chosen
from a 258-sample profile, which cannot resolve a 5% effect, and measured at
20 passes, where the run-to-run spread is +-3% and hid every one of them. The
same measurements at 2,340 samples and 40 passes are unambiguous. Sample first,
and check the noise floor before believing a null result.

**A border image re-sliced itself every frame.** The nine pieces were
rasterized into a fresh texture on every paint, where a layered background has
always gone through the texture cache. Twelve 9-sliced panels on a page with
an animation running cost **5.014 ms a frame**; an ordinary border on the same
page cost 0.072. Cached on the resolved slice, width, repeat and destination
size -- the RESOLVED values, because `border-image-width` is a multiple of the
used border width, so two boxes with identical declarations and different
borders are different pictures. 5.014 ms -> 0.072, which is the plain border's
number. The first paint halved too, since same-sized panels now share one
texture.

**A ComputedStyle sized itself 334 times.** `ensure_capacity` grew six vectors
by one slot per property set, three of them `vector<bool>`. It takes the
registry's full size at once now.

## Measuring it

    Tools/layoutbench.sh              # median of 3 sweeps, worst page first
    Tools/layoutbench.sh --ab A B     # two binaries, interleaved

Use `--ab` for anything under about ten per cent, and do not compare two
separate runs. The same binary measured twenty minutes apart read 3.052 ms and
3.262 on layout-stress -- seven per cent of drift with no code between them,
larger than most single optimisations. A median defends against variance
within a sweep and not at all against drift between invocations; `--ab`
alternates the two binaries sample by sample, in both orders, so they see the
same machine.

That distinction has already changed two conclusions. Making box sides look
themselves up by id read 1 to 8 per cent SLOWER on its first sweep and was
nearly reverted; it is a small win. Pooling the grid's occupancy vector looked
like a small win by the same loose method and is a consistent 1 to 6 per cent
LOSS under `--ab` -- clearing the borrowed rows costs more than the allocations
it saves, so the grid still builds them fresh.

## The registry's side tables, flattened

`ComputedStyle::get(id)` answers from the box's own values when the property is
set there, and most reads are not: they fall through to `is_inherited(id)`,
then a walk of the ancestors, then `initial_value(id)`. Those two registry
calls are therefore the tail of nearly every property read on nearly every box.

Sampling layout-stress put `is_inherited` at 194 samples on one line -- more
than any other single line in the pass, ahead of flex and inline layout. It was
an out-of-line call doing a bounds check and a `std::vector<bool>` bit extract.
`initial_value` was worse per call: a bounds check, a pointer chase into a
`CssProperty`, and a `std::string`-to-`string_view` conversion.

Both are now inline, off flat arrays: `inherited_` holds bytes rather than
bits, and `initial_views_` holds the views themselves so the fallback return is
one load. Between 4 and 6 per cent on the large pages and up to 9 on the small
ones, with nothing slower anywhere in the corpus:

    layout-stress  -3.8%     glass       -5.8%     particles    -7.8%
    stats          -4.1%     match3      -6.4%     sample-menu  -8.3%
    settings       -5.2%     dialogue    -7.1%     card-component -9.1%

`std::vector<bool>` has now cost measurable time twice in this engine, on
`ComputedStyle::occupied_` and here. It is worth treating as a red flag in any
table an inner loop indexes.

## The last of the by-name property reads

The bulk conversion of `get(style, "name")` to a cached id missed every call
whose property is chosen at runtime -- `get(is, column ? "margin-top" :
"margin-left")` -- because the rewrite matched a literal, and a ternary is not
one. It also missed `positioning.cpp` entirely, which had no id-taking helper
at all and read `will-change`, `contain`, `overflow-x`/`-y`, `z-index`,
`outline-width`, `filter` and `margin` by name on every box in the tree.

Those are now ids as well, the ternaries picking between two constants instead
of two strings. Nothing in the corpus got slower and the large pages moved
again:

    stats       -7.4%     glass         -6.2%     quests    -8.4%
    flex-play   -4.9%     grid-play     -4.7%     dialogue  -7.6%
    vendor      -4.6%     layout-stress -3.7%     randhtml  -3.0%

Together with the registry flattening above, a layout pass of layout-stress is
down from 3.16 ms to 2.67 ms.

## Caller attribution, and the two things it found

Line-level attribution names the callee. `id_of` was still visible after every
by-name read had supposedly been converted, and `id_of` is not where the fix
goes. The sampler now aggregates the innermost address WITH its caller
(`WEVA_PAIRS` in the scratch `timesites.cpp`), which answered it in one run:

`resolve_border_edges` takes its property names as lambda PARAMETERS --
`edge("border-top-style", "border-top-width")` -- so the textual rewrite, which
matched `get(style, "literal")`, could not see them. Eight name hashes per box
per pass, and the largest single contributor left to `id_of`.

`is_border_box` was a cross-translation-unit call from block, flex, grid,
inline, table and positioning, several times per box, to read one byte's worth
of answer. Now inline in the header, with the property id as a namespace-scope
inline variable so there is no function-local static guard to check either.

    layout-stress -6.2%   glass  -5.1%   match3    -7.4%
    vendor        -4.2%   quests -4.0%   particles -7.0%
    stats         -3.4%   randhtml -3.3%

flex-playground read +1.9% on the five-sweep A/B and -2.4% on an eleven-sweep
one restricted to it. Five sweeps is not always enough to call a two per cent
move, even interleaved.

## Box is 528 bytes, and that is not the problem

A layout pass of layout-stress walks 6,926 boxes, 3.6 MB of them, which does
not fit in L2 -- and sampling put `BoxTree::operator[]` at about a tenth of the
pass. The obvious read is that the struct is too fat: 38 of those bytes are
padding around interleaved bools, and another 72 are `std::optional` offsets
that almost every box leaves empty. Splitting it hot/cold would be days of
work across every layout file.

Before starting, the cheap experiment: add 64 bytes of dead padding to `Box`
and measure. A twelve per cent size increase cost **nothing** -- layout-stress
-0.3%, vendor +0.3%, randhtml +1.4%, and the two samples that moved 2.5% are
small ones inside the noise. So the samples on `operator[]` are the index and
the loads, not misses that a smaller struct would avoid, and the whole refactor
is off the table for a fraction of a per cent.

Worth doing this way round whenever the fix is expensive and the diagnosis is
an inference: make the problem WORSE first, cheaply, and see if the metric
notices.

## The font-size memo was checked one step too late

`font_size_px` memoises its result on the style, keyed on the style's version
and the parent's resolved size. The check sat AFTER the declaration was read --
and `font-size` is inherited, so that read walks the ancestor chain whenever
the box does not set a size of its own, which is most boxes. Every memo hit
was still paying for an O(depth) walk, twice per box, for a value the memo
already had.

Moving the check above the read is the whole change. Layout writes no style,
so after the first call per style the walk is pure waste.

    stock-dashboard -6.1%   stats  -4.7%   vendor        -3.9%
    glass           -4.6%   flex-playground -2.1%   layout-stress -2.1%

The key stays sound across inheritance even though it is the style's OWN
version: what an ancestor's font-size change moves is the parent's resolved
size, and that is the other half of the key. This is the distinction the
reverted inherit memo got wrong -- it cached an ancestor's identity under a
key that an ancestor could not move.

layout-stress and grid-playground both read as small REGRESSIONS on the
five-sweep A/B (+0.9% and +1.6%) and as -2.1% and -1.0% on an eleven-sweep run
restricted to them. That is the second time a five-sweep reading has inverted;
confirm anything under about three per cent before believing it either way.

## There is no redundant relayout to remove

The standing assumption through several rounds of this work was that flex and
grid re-lay their children more than they need to, and that the win waiting to
be had was structural rather than another few per cent off a property read.
Counting says no.

Instrumenting the entry points for one pass (a temporary probe, not kept):

    sample            boxes  layout_block  relayout  relayout at the SAME width
    layout-stress      6926          3704       988         193, all with a height imposed
    vendor             8162          1907      1418          80, all with a height imposed
    flex-playground    4890          1704      1341          54, all with a height imposed
    grid-playground    3008           886      1065         202, all with a height imposed

Relayout is between a quarter and more than all of the initial layout count --
grid-playground relayouts MORE often than it lays out -- which is what made the
hypothesis look good. But of the relayouts that arrive at a width the box
already has, **every single one has had a cross size imposed on it**, so it is
re-running with a genuinely new constraint rather than repeating itself. Not
one relayout in the corpus is provably redundant.

The two-pass shape is the flex and grid algorithms doing their job: lay the
item out at the container's width to learn its natural size, then again at the
size that falls out of resolving the line. Skipping the second pass is not
available, and a guard on "same width" would fire on nothing.

## `text-indent: 0` was half the allocations in a layout pass

The biggest single win in this file, from four words of code.

`resolve_length` has two overloads. One takes a style and a property id and
reads the style's memoised parse. The other takes the value as CHARACTERS, has
no memo to read, and calls `parse_css_value` -- which allocates a CssValue --
every single time it runs.

`text-indent` computes to `0`, it is inherited so every element has it, and
`text_indent_px` reads it through the character overload once per inline
container per inline layout. Allocation-site profiling on vendor.html:

    3168 of 6022 allocations in one steady-state pass
         parse_css_value  <- resolve_length(string_view)  <- layout_inline_items

Fifty-three per cent of a layout pass's allocations, to parse the string "0"
into the number nought, over and over, on a page where no element indents
anything.

The fix is a fast path in the character overload for `0` and `0px`, which is
the same trick `resolve_box_sides_px` already uses for its all-zero case:

    vendor        -18.6%     stats         -16.4%     randhtml      -11.7%
    inventory     -18.0%     layout-stress -11.2%     quests        -14.1%

    allocations per pass:  vendor 10163 -> 2915, inventory 3140 -> 716,
                           layout-stress 7670 -> 2348

NOT the empty string, which falls through to `auto` -- a different answer from
zero in every caller that tells them apart -- and not `0%`, which is a Percent
to its callers rather than a Length, and that difference decides how it
resolves against its basis.

Worth remembering as a shape: a hot path reading a value as text rather than
through the parsed-value memo. The memo exists precisely so this does not
happen, and the character overload is the door around it.

## Anchor positioning costs 2 to 4 per cent, and I cannot say why

Landing CSS anchor positioning moved about half the corpus by 2 to 4 per cent
and the other half not at all. It is recorded here unresolved rather than
quietly absorbed, because the obvious explanations are all ruled out:

* **Not measurement noise.** The harness A/B'd against a copy of the same
  binary reads within 1 per cent, and `bench_head` against a freshly built HEAD
  reads within 1.2. Both controls were run alongside the numbers below.
* **Not the out-of-flow path.** `layout-stress` declares no `position:
  absolute` at all and still moves 1.9 per cent.
* **Not the anchor walk.** It is lazy: nothing collects a registry until a
  declaration actually reads `anchor(`. Two earlier shapes -- collecting per
  pass, and noting anchor names per element at build time -- were worse (6 to
  12 and 1 to 2 per cent), and both were replaced.
* **Not the extra translation unit.** Building `anchor.cpp` into the library
  with the positioning side reverted costs nothing measurable.
* **Not the size of the diff in positioning.cpp.** Moving the pass state and
  both resolvers out into `anchor.cpp`, leaving 27 added lines behind, changed
  nothing.

**It was the call sites, not the calls.** Disabling all six with `if (false)`
while leaving every line in place kept most of the regression -- so their mere
presence was changing how `apply_absolute` compiled. That same run also
cleared `layout-stress` and `randhtml` to 0.0 and 0.2 per cent, which means
their earlier readings were variance and the real cost only ever landed on
samples that HAVE out-of-flow content.

Collapsing the six into one cold call -- `apply_anchor_overrides`, which writes
all four insets and both extents in `anchor.cpp` and is marked
`[[gnu::cold]]` -- halves what is left:

    sample     six call sites   one cold call
    glass               +2.4%           +1.4%
    inventory           +2.5%           +1.1%
    layout-stress       +1.9%            0.0%
    randhtml            +0.2%           +0.2%

What remains is about 1 to 2 per cent on the four samples with the most
out-of-flow boxes, which is the honest price of the feature rather than an
accident of codegen. Kept: it closes nine real oracle failures and implements
a CSS module the engine did not have.

## Every animated sample, and where the font seam still costs

An animated page invalidates every frame, so a full pass runs every frame.
Every sample with an `infinite` keyframe animation, engine work per frame,
after the shaping and face-metrics caches (`frameprobe.tscn`):

    page               ms/frame   worst    hovering   hover worst
    layout-stress          7.58   10.99      10.62       57.04
    match3                 2.30    3.60       2.77       11.04
    particles              1.94    3.25       2.25       20.90
    audit-validation       1.34    3.00       2.94        4.89
    combat-hud             1.09    1.97       1.13        4.74
    hud                    0.96    2.35       1.70        5.56
    match3-endgame         0.90    1.61       0.88        4.04
    neon                   0.49    1.22       0.58        2.54
    glass                  0.03    0.16       0.59        6.98
    story-bubble           0.02    0.05       0.05        0.70

    stats  (static)        0.005   0.009      0.42        6.77
    vendor (static)        0.004   0.009      0.38        6.56

Two things the table says. Most animated pages are now under 2.3 ms, which is
the shaping cache. And `layout-stress` is an outlier at 7.6, which it should be
-- it is a synthetic worst case with 6,926 boxes and animations on top.

### face_metrics was a host round trip per box

`line_height`, `ascent` and `descent` are three entry points and each called
`face_metrics` on the host. Layout asks for a line height on every box, so
layout-stress paid 6,926 round trips an update for a value that depends on
nothing but the face and the size -- of which a page uses about ten.

Cached on (face, size) at the same adapter as shaping. layout-stress went 10.46
to 7.58 ms a frame and its layout stage from 7-9 ms to 3.6-4.1;
audit-validation 2.07 to 1.34.

The shaping cache was checked for thrashing at the same time, since it clears
wholesale when full and layout-stress is the page most likely to overflow it:
**26,862 hits, 325 misses, 0 clears**. The working set is 325 runs against a
4,096 cap, so the wholesale clear has never fired.

## flipbench.sh: what ONE change costs on a whole page

`layoutbench.sh` measures a layout from nothing. That is the cold case, and the
engine is already good at it. A running UI does the WARM case instead: the page
is laid out, one thing changes, and the engine catches up. Nothing measured
that, which is why the next section's numbers had never been seen.

`Tools/flipbench.sh` flips one property on one element, alternating between two
values so nothing can be cached, and reports two runs per sample:

    layout   padding-left flipped   -> Invalidation::Layout
    paint    background-color       -> Invalidation::Paint

Read the DELTA. A paint flip repaints the whole page too, so absolute numbers
are dominated by paint and would hide a layout change entirely; the difference
subtracts everything the two share.

### What it found on its first run

    sample             layout    paint    delta
    grid-playground    31.84     1.35     30.50
    weva-landing       29.34     1.74     27.60
    flex-playground    24.50     2.42     22.08
    form-demo          14.99     1.05     13.94
    layout-stress      11.61     8.84      2.77

grid-playground is 3,008 boxes and layout-stress is 6,926, so this is not
about page size. Staged, grid-playground's layout flip is layout 0.85 ms and
**paint 32 ms** -- and its PAINT flip is 1.35 ms total. Paint after a layout
change is twenty-four times paint after a paint change.

The texture cache explains it exactly: a paint flip is 2 hits 0 misses every
pass, a layout flip is 1 hit 1 MISS every pass. One texture regenerates
forever. Logging the missing key names it:

    MISS 1280.000;1135.000;16.000;...;radial-gradient(ellipse 80% 60% at 50...
    MISS 1269.000;1135.000;...
    MISS 1268.000;1135.000;...

The key opens with the texture's size, and `padding-left: 11px` to `12px`
changes the width of a full-page gradient by one pixel. So a one-pixel change
regenerates 1,280 x 1,135 pixels -- 1.45 million -- at about 21 nanoseconds
each, because `sample_stops` walks the stop list per pixel.

That is a window resize as much as a benchmark: every resize frame of any page
with a large gradient background pays it. Not fixed here. The two candidate
fixes -- a stop lookup table, or quantising the size in the cache key so a
small resize reuses the texture stretched -- both change pixels, and the
backend gate compares the two rasterisers on the IDENTICAL draw list so it
would not notice. That wants `visual_rank_soft.py` before and after.

### Three divides a pixel

`gradient_t` divided twice for a radial gradient and once for a linear one, and
`sample_stops` divided once more to find the position within a stop pair. A
divide is four or five times a multiply, and a full-page background is a
million pixels.

All four are precomputed in `prepare()` now. The stop table resolves `next` the
same way `sample_stops` does -- a hint sits BETWEEN two colour stops, so the
pair is not always (i, i+1) and a table that assumed so would divide by the
wrong span wherever a hint appeared.

    grid-playground   33.03 -> 28.60      flex-playground   25.43 -> 22.51
    weva-landing      29.74 -> 28.45      form-demo         15.70 -> 14.10

About 13 per cent, and no pixel moved: `visual_rank_soft.py` over all 35
samples reports 0 pages changed on either column. That check was the point --
`x * (1/y)` is not bit-identical to `x / y`, and the backend gate compares the
two rasterisers on the same draw list so it cannot see a colour change.

### Tried and reverted: adaptive supersampling for radial gradients

The first guess was that a radial gradient with a hard stop fell out of the
adaptive edge test -- which only handles linear -- and supersampled 3x3 over
the whole texture. The bound is easy enough (t = sqrt(ex^2+ey^2) and |ex| <= t
gives |dt/dx| <= 1/rx, conservative in the safe direction), and it was written.

Then `WEVA_GRADIENT_LOG` said every gradient in the corpus rasterises at
**1^2 samples**. Not one page supersamples, so the change could never fire.
Reverted: correct, unmeasurable, and complexity in a correctness-sensitive
path. The 32 ms is simply 1024 x 1024 texels x 2 tiles of ordinary per-pixel
work.

### Still open: the background cache key asks a question the pixels do not answer

The remaining 28 ms of grid-playground's warm change is one texture being
regenerated because the cache key opens with the box's width and height, and a
`padding-left` flip moves the width by a pixel.

`Tools/gradsize` measures whether the texels actually depend on that. Two
rasterisations of the same gradient, box 1280 wide against 1269, both capped to
a 1024x1024 texture:

    radial, all percentages        0 of 4,194,304 bytes differ
    radial, px radius         24,736 differ, worst 58
    linear, percentages       68,455 differ, worst  1
    linear, px stop           96,256 differ, worst  1

The first row is **bit-identical**, and it is the common case: percentage
geometry divides the box size straight back out, so `rx = 0.8w` and `cx = 0.5w`
make `ex = ((px + 0.5) / tex_w - 0.5) / 0.8` with no `w` left in it. A resize
regenerates four megabytes of identical pixels.

The other three are genuinely different and must keep missing -- a linear
gradient's angle depends on the box aspect, which is why even its "percentages"
row moves by one level.

**The fix is not a heuristic.** A textual test for "no px anywhere" trips over
`rgba(22,34,58,0)`; a size tolerance would silently share a texture between the
second row's variants, which differ by 58 of 255. The principled version keys
on the RESOLVED, size-normalised parameters the rasteriser will actually use --
`prepare()` per tile is microseconds against the 28 ms rasterisation, so it can
be run to build the key. What makes that safe rather than plausible is the
table above: any candidate must make the first row hit and the other three
miss, and gradsize says which.

### Still open: layout has no incremental path

`layout-stress` sits at 7.6 ms a frame where everything else is under 2.3, and
the reason is structural rather than a hot loop. From weva_c.cpp:

    if (pending >= Invalidation::Layout) {
        doc->tree.reset();
        ... build_document(...)
    }
    if (pending >= Invalidation::Layout) {
        block.layout_root(root, viewport_w, viewport_h);
        run_positioning(...);
        compute_visual_overflow(...);
    }

**Any** layout-affecting change rebuilds the whole box tree and lays out from
the root. There is no scoping. The cascade above it IS scoped -- it confines
its walk to touched elements and what the sheets can carry forward -- and
layout simply is not.

layout-stress animates `padding`, `width` and `font-size` on purpose (its own
header says so: it exists to exercise the relayout path), so it pays a full
6,926-box rebuild and layout on every frame for a handful of animated
elements.

**What that is worth, measured.** The identical page with its three keyframes
swapped from padding/width/font-size to `opacity` -- same tree, same box count,
same paint, only the invalidation tier different:

    layout-stress                     7.63 ms/frame   worst 12.08
    the same page, paint-only anims   3.41 ms/frame   worst  5.76

So roughly 4.2 ms is the whole-tree relayout and 3.4 ms the whole-tree repaint.
Scoping layout to the dirty subtrees would take the first most of the way to
nothing; partial repaint would do the same for the second. Both are
architectural projects rather than optimisations, and neither should be started
without the incremental-layout benchmark the C# side has.

### Still open: hover spikes

The `hover worst` column is the remaining problem, and it is not confined to
the heavy pages: `stats` and `vendor` are STATIC and still spike to 6.8 and 6.6
ms on a hover, against a 0.005 ms idle frame. layout-stress reaches 57. A hover
restyles and relayouts, so some of that is real, but a 1,300x jump on a static
page is not explained by that alone and has not been investigated yet.

## An animated page reshaped all its text every frame

`hud` ran at 12 to 15 ms a frame in the gallery and spiked on hover. That was
the engine, and text was nearly all of it.

An animated page invalidates every frame, so it runs a full pass: cascade,
layout, paint. Timed per stage, paint was 5.0 ms of it and layout 2.1. Timed
inside paint (`WEVA_PAINT_LOG`), 3.0 of paint's 4.0 ms was text -- 1.5 shaping
and 1.5 building the quads -- against 0.16 for backgrounds and 0.10 for
shadows.

The text had not changed. It was being shaped twice per frame, once by layout
to measure it and once by paint to place it, and every one of those calls
crossed the host boundary TWICE (once to size the result, once to fill it),
with the Godot host building a TextServer buffer and reading a Dictionary per
glyph each time.

Shaping is now memoised at the ABI adapter, keyed on (face, text, size), which
covers both callers. Measured through the host, engine work per frame:

    page          before   after     hover before   hover after
    hud            4.33    0.96          5.40          2.04
    combat-hud     7.01    1.06          6.92          1.09
    match3         9.03    2.30          9.48          2.70
    glass          0.03    0.03          2.45          0.57

`hud`'s worst frame went 8.64 ms to 3.04, and its worst while hovering 12.91 to
5.49.

### Where hud's frame goes now

    paint 0.82 ms total:  rest 0.57   backgrounds 0.13   shadows 0.08
                          glyphs 0.03  text 0.03

Text was 3.0 of the old 4.0 ms and is now 0.06. What is left is `rest` -- the
part of a paint pass none of the named buckets claim: the tree walk, clips,
borders, images, the layer machinery. It is the largest remaining bucket on
every animated page (0.57 ms on hud, 1.3 ms on match3, where backgrounds are
another 0.6).

Two hypotheses about it have been tested and neither survived:

* **The submission path.** `draw_mesh` is where every draw goes, and with a
  clip it copies the whole mesh, transforms every vertex and clips
  geometrically. It is now measured (`[submit ...]` in the log, reported as
  "of which" because it nests inside the other buckets): **0.14 ms across 134
  draws, 41 of them clipped.** Not it.
* **Pseudo-element restyling.** `compute_pseudo_element` runs four times per
  element and shows up in a sampled paint trace. But that trace comes from
  `weva_bench --mutate=paint`, which forces a restyle; an animated document
  deliberately skips it, and hud's cascade reads 0.000 ms on most frames. Not
  it either.

Sampling the paint path on hud is flat -- no site above five samples. So the
remaining 0.6 ms is spread thin across the tree walk, about 4.5 microseconds a
box, and there is no single thing to fix. Anyone picking this up should add
buckets rather than guess, which is what ruled the two above out.

There are now two caches on the same seam: this one, and the scalar width cache
in `FontInterfaceMetrics::measure`. They are kept apart deliberately -- measure
wants a double and would otherwise copy a glyph vector to get one. Both are
pure functions of (face, text, size) and the face is immutable, so neither has
an invalidation surface.

## Measuring text was 98 per cent repeat work

The largest win in this file, and it was invisible to every benchmark in it.

`FontInterfaceMetrics::measure` shapes the text and sums the advances. It did
that on every call, and layout calls it over and over: every relayout probe and
every intrinsic-width pass re-measures runs that have not changed. Building
`stats.html` through the Godot host made **5,384 measure() calls for 107
distinct (text, size) pairs** -- 98 per cent repeats, each one a full shaping
call across the host boundary into Godot's TextServer.

Memoised on a hash of (text, size), so a lookup allocates nothing and the
stored text is compared on a hit -- a collision costs a re-shape, never a wrong
width. Bounded at 4096 entries and cleared wholesale when it fills, because the
access pattern is a layout pass rather than a working set.

    build, engine font    266 ms -> 61.6 ms
    of which layout       241 ms -> (the rest is parse, cascade, paint, bridge)

Invisible to `layoutbench` because `MonoFontMetrics` has its own `measure` and
never touches this path -- which is exactly the point of the section below.

## The layout benchmarks measure a stub font

Everything above was measured with `Tools/layoutbench.sh`, which drives
`weva_bench`, which registers `MonoFontMetrics` -- a stub face whose advance is
a constant. The Godot host registers the real engine font. Measured through the
host, on `stats.html` at 1280x720, with a Release extension
(`hosts/godot/project/statprobe.tscn`, and `WEVA_STAGE_LOG=1` for the split):

    build, engine font    284 ms      cascade 6, layout 241, paint 23
    build, stub font       49 ms
    settled update          0 ms      the core's early-out, working

`layoutbench` puts the same page at about **0.9 ms**.

So measuring text is the overwhelming majority of what layout costs in the host,
and the numbers in the rest of this file are a fraction of a per cent of what a
real page pays. They are not wrong -- the box-model work they measure is real,
and `text-indent` at -18% was a genuine allocation removed from a hot path --
but they are not the whole picture, and nothing here has yet been measured
against the face that ships.

The gallery's stats window (`S`) shows the build time, and `F` switches faces
while it is open, so the difference is one keypress away.

## Tried, measured, and not kept

Both of these looked obviously worth doing and are slower. Recorded so the
next person -- or the next tick -- does not spend the afternoon rediscovering
them.

**Pooling the grid's auto-placement occupancy.** One heap allocation per row
per grid, replaced by a borrowed vector whose rows are cleared instead. 722
fewer allocations a pass on randhtml and a consistent 1 to 6 per cent LOSS
under `--ab`: clearing the borrowed rows costs more than the allocations save.

**Memoising the inherit chain.** `ComputedStyle::get` walks ancestors to find
who sets an inherited property, which is O(depth) and runs for every read of
colour, font, line-height and the rest on every box. Caching the ancestor that
answered, guarded by a global version counter, measured 4 to 6 per cent SLOWER
on the larger pages -- stats +6.1%, map +6.2%, vendor +4.2%. Real chains are
one or two links, so the guard costs more than the walk.

**Reserving the grid's track-list vectors.** `split_tracks` and
`parse_track_list` grow by `push_back`, so a track list takes three
allocations to reach eight. `reserve(8)` in both moved allocations by 1 per
cent -- most real track lists are two or three tracks, where the growth walk
allocated once anyway -- and cost 2 to 3 per cent in time for the
over-reservation.

**Replacing the grid's `std::stable_sort` with `std::sort`.** Sorting spanning
items by span allocates a temporary buffer inside `stable_sort`: 1276 of the
7210 allocations in a randhtml pass, the largest single site in the engine
after `text-indent`. Ordering by (span, arrival index) instead is provably
identical -- arrival order is exactly what stability preserved -- and removed
17 per cent of the pass's allocations. It is 0.4 to 4.5 per cent SLOWER. The
lists are short, and the second comparison in the comparator costs more than
the buffer it avoids.

All four were measured the wrong way first and looked like wins. All four are
losses under an interleaved A/B.

### Allocation count is not a proxy for time in this engine

Four times now, and worth stating as a rule rather than a coincidence. The one
allocation fix that DID pay -- `text-indent`, worth 18 per cent -- did not pay
because it removed allocations. It paid because it removed a full CSS value
PARSE from a per-container path: the allocation was a symptom of parsing text
that a memo already held, and the parse was the cost.

So the question to ask of an allocation site is not "how many?" but "what work
is it a symptom of?". A vector that grows is not doing avoidable work. A
`parse_css_value` on a string that has not changed since the last pass is.

## What is still slow, and why it has not been fixed

**Rasterising gradients on the CPU, in the first paint.** This is the whole of
the remaining problem and it is not close:

| page | cascade | layout | paint |
|---|---|---|---|
| hud | 6.6 ms | 2.0 ms | **134.6 ms** |
| glass | 6.2 ms | 2.7 ms | **150.0 ms** |

Five million gradient-stop evaluations and 52 blurs on hud alone. Making the
CASCADE faster -- style sharing, a cheaper property write -- addresses four
percent of that, which is why the obvious-sounding fix is the wrong one. It
was worth measuring before building: an earlier reading of the same problem
blamed the cascade on the strength of a 10,000-row synthetic page, where the
proportions are reversed.

What would actually help, and what it costs:

  * A LUT for the stop search. `sample_stops` walks the stop list per pixel.
    A table from quantised `t` to SEGMENT INDEX skips the search while leaving
    the interpolation exactly as it is, so the output stays bit-identical.
    Worth perhaps a tenth of the paint.
  * Rasterising smooth gradients at reduced resolution and upsampling. This is
    where the real factor is -- a full-screen layer is 590,000 pixels at the
    current 1024-px cap -- and it MOVES PIXELS. The render gates compare
    against the Godot backend at 0.00% on thirteen states, so this needs a
    decision about tolerance before it can be a change.
  * Gradients as GPU work rather than a baked texture. A different
    architecture, not an optimisation.

**A first cascade is ~30 us an element on a rule-heavy page.** Each element
performs ~178 property writes to materialise a ComputedStyle. Style sharing --
elements with the same shape, the same parent and no inline style pointing at
one computed style -- is the fix, and it is worth doing on a page like
layout-stress where the cascade IS the cost (16.7 ms against 7.3 for paint).
It is not worth doing for hud or glass.

Measured and worth recording: the cascade's shape cache does not earn its keep
on a uniform list. 10,000 rows with a UNIQUE class each cascade FASTER than
10,000 identical ones, which is the opposite of what a shape cache is for.

## Focused text fields (2026-09-06)

The long-input work exposed an allocation in `caret_for`: it copied the whole
field value to obtain its length before the clean-frame early return. Length
and composition equality checks now read the DOM without copying its text.
Focused single-line fields with 128 and 4,096 ASCII characters each report
zero allocations on an unchanged update. Previously they allocated one value
copy per frame (129 and 4,097 bytes respectively).

`weva_bench --full --focus=#field` measures this path. Use
`--mutate=caret --target=#field` to alternate Home and End, exercising text
scrolling and paint. With a 100px-wide, 20px-font input, 500 Release passes and
the headless fallback font, mean update time was approximately 0.028 ms for
128 characters and 0.388 ms for 4,096. The last moving-caret update made 244
allocations / 177,815 bytes and 269 allocations / 3,205,399 bytes respectively.
Key dispatch occurs before the timed/counting region. These are update costs,
not total input latency or Godot font measurements; repaint allocation still
needs work. The first End can be unchanged, so the minimum is not a useful
caret-motion statistic.

A separate Linux Godot 4.7.2 headless run using the engine font, 20 warmups
and 200 measured Home/End plus `update_document(0)` calls averaged 0.018 ms
for 128 characters and 0.159 ms for 4,096. This includes the direct host key
method and event pumping, but excludes GPU drawing and native event delivery.

Grapheme iteration itself allocates no storage. Password display mapping and
pointer boundary searches allocate temporary vectors only when painting or
handling input; they are beyond the unchanged-frame return.

After separating live form state from markup defaults, the same 500-pass
focused 128/4,096-character idle benchmarks still report zero allocations
and zero bytes per update. Control storage and input-version bookkeeping are
allocated when needed; reset walks controls only when explicitly invoked.
Textarea edits no longer replace DOM text nodes. Their version changes feed
the existing subtree layout and partial paint paths; 36 incremental form
edit/reset frames match forced complete layout, including caret geometry
and texture pixels.

After select input integration, a 1,000-option idle listbox and the focused
4,096-character input each report zero steady-state allocations/bytes over
500 `weva_bench --full` updates. The listbox benchmark is unfocused; the input
uses `--focus=#field`. A separate regression focuses a multiple select, moves
its keyboard row without changing selectedness, and verifies that 100 idle
updates preserve the draw serial. Keyboard-row inputs invalidate the select's
paint subtree; display-mode changes rebuild its child boxes. Retained paint
matches forced full layout across navigation, focus, scrolling and removal.

After Unicode select typeahead and label rendering, both 500-pass idle cases
still report zero allocations/bytes. Typeahead allocates only during input;
its timeout is evaluated when a key arrives. Label layout has a separate
input version so selection changes retain paint-only invalidation.

The embedded ICU 78.3 dependency adds 1,278,800 bytes of Unicode data plus
code. For the verified desktop builds, the Linux library grew from 5,881,648
to 9,287,056 bytes and the Windows DLL from 2,251,776 to 4,644,352 bytes.
The two-platform ZIP is approximately 5.3 MB. No ICU shared library or data
file is needed at runtime. First source builds compile ICU; later builds
reuse it. These size figures describe these toolchain configurations, not
all release builds.

Before separating form visual inputs from selector scopes, large-list
selection was expensive. The standalone Godot benchmark
`hosts/godot/project/typeahead_bench.gd` creates 1,000 options, resets selection
and focus outside each measurement, then times `send_text("z")` and
`update_document(0)` choosing the last option. On Windows Godot 4.7.2 with the
engine font, 20 warmups and 200 samples averaged 46.234 ms: 0.661 ms in input
and event dispatch, 45.573 ms in the following update. An earlier run averaged
36.947 ms; these are desktop observations, not stable latency guarantees.
Separate `WEVA_STAGE_LOG=1` runs place the largest measured cost in cascade
(approximately 20–30 ms), followed by layout (~3 ms) and paint (~1–2 ms).
These measurements motivated the invalidation work below. Zero idle
allocations alone do not establish fast selection in a large list.

Run from the host project with `godot --headless --path . --script
res://typeahead_bench.gd`. Input delivery is direct, and GPU drawing is excluded.

Held listbox scrolling has its own benchmark, `select_autoscroll_bench.gd` in
the same project. With 1,000 options, an armed pointer below the viewport,
20 warmups and 200 measured `update_document(1.0/60.0)` calls, Windows Godot
4.7.2 with its engine font measured **1.416 ms mean**, 3.331 ms maximum and
1.515 ms for the first tick. The list moved 5,893.3 CSS pixels without reaching
its limit or changing the pending selection. This includes the update and
scroll-event pumping, and excludes GPU drawing/native event delivery. It is
a desktop observation, not a frame-time guarantee.

`text_autoscroll_bench.gd` measures held selection in a 160-pixel input with
4,096 UTF-8 bytes. On Windows Godot 4.7.2 with engine fonts, the same 20-warmup,
200-sample method measured **4.567 ms mean** for ASCII (first tick 2.633 ms,
maximum 24.808 ms) and **3.919 ms mean** for accented text with eight emoji
runs (first tick 2.143 ms, maximum 15.839 ms). Both gestures kept exposing new
text throughout measurement. This includes repeated hit measurement, painting
and scroll-event pumping; it excludes native pointer delivery and GPU drawing.
It establishes an active-update cost to improve, not a stable frame budget.
The fixture deliberately stays below stock Godot's separate emoji-stack defect;
see [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md). Both 500-pass idle fixtures
still report zero allocations/bytes after text-selection autoscroll.

Autoscroll changes the list's paint input versions without reflow. A retained
render matches forced full layout through scrolling, live viewport resizing
and nested scrolling. At a boundary, unchanged offsets publish neither draws
nor scroll events. After this change, the 500-pass unfocused 1,000-option
listbox and focused 4,096-character input benchmarks still report zero idle
allocations/bytes. Active painting has its existing allocation costs.

After coalescing cascade scopes, consuming form visual versions separately,
and removing the unused layout-time color snapshot, a back-to-back Windows
run of that same benchmark measured:

| 1,000-option choice, 200 samples | Previous preview | Updated build |
|---|---:|---:|
| First choice | 17.694 ms | 3.534 ms |
| Mean input + update | 38.044 ms | 2.066 ms |
| Mean input/event dispatch | 0.548 ms | 0.537 ms |
| Mean following update | 37.497 ms | 1.529 ms |

The approximately 18× improvement is in the resulting update. The stage
profile now visits two options instead of 3,005 elements, with cascade at
0.05–0.08 ms. Ordinary selection no longer runs layout; paint is approximately
1–2 ms. Idle 1,000-option listboxes and focused 4,096-character inputs retain
zero allocations/bytes over 500 updates. Moving focus into the entire select
still restyles its descendants (~10 ms in this fixture); further focus-scope
work remains. Color changes now use paint invalidation throughout the engine,
with complete-render comparisons covering inherited text, decoration,
`currentColor`, sibling selectors and `:has()`.
