# In-game UI performance

`hosts/godot/project/game_ui_bench.gd` exercises the public Godot API with
ordinary game UI. It uses native fonts, a 1280x720 document, 120 warmup frames,
600 measured frames and a deterministic 1/60 simulation step by default.
Construction is outside measurement. All workloads keep their document alive.

| Workload | Per-frame work |
|---|---|
| Empty window | Loop and renderer reference, without a document |
| Idle HUD | 49 elements, health/mana/stamina, party, objectives, action slots and ammunition |
| Active HUD | Three bar widths at 60 Hz, health/ammunition labels at 10 Hz |
| Redundant HUD | The same three widths and two labels pushed every frame |
| Bound HUD | Three bars and two numeric labels pushed through `data` at 60 Hz |
| Menu hover | Move between two of five buttons at 10 Hz |
| Menu animation | Repeat a half-second opacity transition |
| Inventory scroll | 96 slots / 294 elements, scroll at 60 Hz within the actual scroll range |
| Chat typing | 40 retained messages, a focused field and text input at 10 Hz |

The script validates that controls, scrolling, hover and bindings took effect.
JSON includes means, median, p95, p99 and largest observed sample. Separate
every-sixth-frame CPU statistics expose label/typing/hover frames that an
overall average could hide. Each native run saves a deterministic final PNG
for every UI workload and the generated HTML/CSS for further profiling.

## Reproduce

Build the extension as described in `hosts/godot/README.md`, then run from the
repository root. The runner creates private projects and does not replace the
development project's extension. The output directory must not already exist.

```powershell
python godot-port/hosts/godot/run_game_ui_bench.py `
  --godot C:/path/to/godot.exe `
  --library C:/path/to/candidate/weva_godot.dll `
  --baseline C:/path/to/previous/weva_godot.dll `
  --out .utmp/game-ui-comparison --runs 3 --frames 600
```

Linux accepts a `.so`. Omit `--baseline` for a single-version measurement.
Both libraries are frozen in the output projects. The runner records engine,
library and benchmark-script SHA-256 digests, reverses A/B order on alternate
pairs and rejects failed or incomplete runs. Native A/B runs require identical
final PNGs. A setup/import failure stops the run and leaves its log for review.

`--headless` measures the host and core with the dummy renderer; keep those
results separate from native rendering. `--draw-profile` enables existing
`WEVA_GODOT_DRAW_LOG` scopes for packing/submission attribution and labels the
receipt as instrumented. Run benchmarks without other builds/tests in flight.

## Read the numbers correctly

`api_cpu` times the complete GDScript drive operation: selector resolution,
value formatting, API calls and their updates, texture preparation, events and
the explicit final update. `last_core_update` is only the **last** core call;
before writes were coalesced, a single drive could make several such calls.
It cannot stand in for the complete API cost.

`whole_frame` extends through the next process-frame signal, including queued
drawing, native renderer work and scheduling/presentation waits. The empty
window exposes that background cost. Subtracting two unrelated whole-frame
averages does not establish GPU cost or speedup. GPU time is not measured here.
Draw-profile logs measure CPU packing/submission, not GPU completion.

These are repeatable standalone workloads, with no game simulation or 3D
scene. They support decisions about normal UI update costs on the tested
hardware. They do not establish a full game's budget, lower-end device results,
maximum supported UI size or a worst-case latency bound. Cold screen opening
has separate measurements in [PERFORMANCE.md](PERFORMANCE.md).

## Western survival sample: direct text updates

The newer standalone [Frontier Camp performance report](../examples/frontier_camp/PERFORMANCE.md)
measures the actual game's bindings and native input in Windows release exports.
Runtime64 fixes the full-rebuild binding path and broad caret repainting. In
three paired release runs per renderer, continuous bound meter updates fall
from about 1.45 ms to 0.33–0.44 ms p95. At 10 Hz, changing-frame p95 is
0.52–0.58 ms; idle ticks remain 0.006–0.007 ms. All 84 before/after screenshots
are identical. Inventory reordering and dialog transitions still cost about
2–3 ms, and the low-frequency clock has larger tails in the short sample.
See that report for the full measurements and limits; these are separate
workloads from the older HUD measurements below.

The [Dust & Iron sample](../hosts/godot/project/samples/western_survival/README.md)
adds a larger authored HUD with a retained, initially hidden 24-slot inventory.
Its stamina controller changes one numeric label and one meter width at most
10 times per second. `survival_bench.gd` measures controller work plus the public
document update, with 60 warmup and 300 measured frames per case.

Profiling runtime60 found that a changed direct-text write requested a full box
rebuild, causing all 260 elements to be restyled, including the hidden inventory.
Width-only updates were much cheaper. Runtime61 queues the text owner's layout
input and uses the existing subtree proof and selector dependency rules. Stable
numeric labels retain surrounding layout and paint. Changes to wrapping,
intrinsic size or relevant selectors can still require more work.

On 2026-09-07, three native paired runs on Ryzen 7 9800X3D / RTX 5080,
Godot 4.7.2, Compatibility renderer, 1280x720 produced the following median
per-run p95s. A/B order alternated and no other tests/builds ran during timing.

| CPU case | runtime60 | runtime61 |
|---|---:|---:|
| Idle HUD | 0.009 ms | 0.008 ms |
| Sprinting, all frames | 4.509 ms | 0.772 ms |
| Sprinting, changing frames only | 5.191 ms | 1.547 ms |
| Open inventory, idle | 0.007 ms | 0.007 ms |

Each sprint run has 39 measured changing frames. Their p95 range is
4.971–6.371 ms before and 1.501–1.577 ms after; the maximum individual sample
is 6.907 ms before and 1.695 ms after. This is about 70% lower changed-frame
p95 CPU cost. Idle frames must not be used to characterize those updates.
GPU work, allocations and full-game frame time are outside this sample probe.
These results do not establish a budget on lower-end hardware.

The runtime61 DLL SHA-256 is
`ecb802df49073fd69a8812d74ddf5ab2a12fbbc20472e1a5b5bedb019c0b5fb7`;
the baseline is
`4be4308824a9d3acf65e900ae29177401935d21f02680862d4f8c8cb261e8090`.
Local run receipts, logs and paired captures are under `.utmp/runtime61/`.

Correctness checks include direct text versus full recomputation across block,
flex, grid, shrink-to-fit, table and `display:contents` layouts; empty labels,
`:has()` and filtered sibling ranks; mixed text/element children; hidden text;
batched writes; removal; reload; and viewport/font changes. A draw-version
assertion verifies actual retention. The full 47-sample mutation corpus passes
under GCC and ASan/UBSan. All 8,393 Godot host checks pass, and 11 deterministic
native survival captures are byte-identical to runtime60. The installed DLL
also passes the sample's 56 checks in Compatibility and Forward Mobile.
A separate native game-UI comparison (one pair, 120 warmup and 120 measured
frames per workload) passes all nine workload checks and produces identical
final PNGs. That short run is a correctness check, not evidence of a general
performance improvement across every workload.

## Reusing image textures

Runtime62 caches rasterized `<img>` content in the existing texture cache.
Previously, repainting an image's containing row rasterized and uploaded its
pixels again even when the source and size were unchanged. The stamina row's
icon accounted for roughly 0.26–0.34 ms of instrumented background work, in
addition to the host's texture preparation.

Identical image inputs now share a texture. Position, opacity and clipping
remain per-draw operations. The key includes source content version, source
URL, object-fit/position inputs, exact sizes, length-resolution context and
color filters. Unused textures are released by the existing cache lifecycle.
Source swaps, base-path changes and asset-reader replacements schedule their
necessary layout/paint work; changing `src` also respects filtered sibling
selector dependencies.

Three final paired runtime61/runtime62 runs on the same Ryzen 7 9800X3D /
RTX 5080, Godot 4.7.2 Compatibility setup at 1280x720 gave these median per-run
CPU statistics. Run order alternated, with profiling disabled and no builds
or tests running during measurement.

| Image workload | runtime61 median | runtime62 median | runtime61 p95 | runtime62 p95 |
|---|---:|---:|---:|---:|
| Inventory scrolling | 2.996 ms | 0.299 ms | 3.212 ms | 0.430 ms |
| Inventory panel fading | 3.157 ms | 0.839 ms | 3.360 ms | 1.097 ms |

Scrolling p95 improves by about 87%; fading p95 improves by about 67%.
Across the three runs, scroll p95 is 3.193–3.297 ms before and 0.417–0.435 ms
after; fade p95 is 3.340–3.403 ms before and 0.964–1.154 ms after.
Every paired final PNG is byte-identical. These measurements cover public API
CPU updates, not GPU completion or a whole game's frame budget.

Three separate paired runs of the full survival HUD show a smaller improvement
in its tail: changing-frame p95 moves from 1.286 ms to 1.025 ms. The per-run
ranges are 1.225–1.630 ms before and 0.968–1.220 ms after, with 39 measured
changing frames per run. Sprinting all-frame p95 falls from 0.613 to 0.241 ms,
while idle HUD/inventory median remains 0.005 ms. The HUD's changing-frame tail
is still about 1 ms on this fast desktop; the larger scroll/fade gains above
should not be presented as the HUD's speedup.

`samples/western_survival/image_ui_bench.gd` exercises a retained 96-item
inventory using twelve distinct item images. It measures continuous scrolling
and panel opacity updates, with 60 warmup and 300 measured frames per case.
Every measured frame changes an input. Set `WEVA_IMAGE_BENCH_OUT` to an existing
directory to save JSON statistics and deterministic final PNGs, or run directly
for console results:

```sh
godot --path godot-port/hosts/godot/project --rendering-method gl_compatibility --script res://samples/western_survival/image_ui_bench.gd
```

The image-cache tests compare each repaint against an uncached render, checking
texture pixels and geometry through fractional sizes, object-fit/position,
font/viewport/root-size/DPI changes, own and inherited filters, resource resets,
missing images and cache eviction. They also verify shared texture IDs and
no new texture on unchanged repaints. Public API tests cover source replacement,
removal, resource reader/base-path changes and preceding-sibling selectors.

The final runtime62 passes 9/9 core CTest suites and 11/11 ASan/UBSan CTest
suites, including the full 47-sample mutation corpus and sanitizer positive
controls. All 8,393 Godot host checks pass; 11 survival captures match runtime61
exactly, and the installed sample passes 56 checks in both Compatibility and
Forward Mobile. The installed DLL SHA-256 is
`7465354b775542d256fbfc20a0780bdafb6d982ab34f46ed02c0be77a5dd384c`.
Local receipts and captures are under `.utmp/runtime62/`.
