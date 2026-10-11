# Runtime geometry follow-up — October 11, 2026

Inventory scrolling API CPU p95 improves **11.9%**, from 1.245 to 1.097 ms,
in the final paired Godot experiment. Core-only scrolling update means improve
**15.1%**, from 0.636 to 0.540 ms. This removes repeated geometry calculations
and allocations; it adds no threads or retained per-document cache.

## What changed

The scroll profile identified rounded fills/borders and property lookup among
the sampled costs. Scrolling changes descendant paint coordinates, preventing
draw-list replay for those items. This change keeps that invalidation intact
and reduces the geometry work needed when they repaint:

- Reuse the original sine/cosine directions for default eight-segment corners.
  Other subdivision counts keep their general path.
- Calculate an AA corner displacement once and use it for both coverage rings.
- Reference existing thin-border outlines instead of copying both vectors.
- Select the shared color once for uniform-color borders. Differently colored
  sides keep their existing mitre partitions.

Sampling, subdivision count, coverage, output vertex order and visual quality
are unchanged. There is no ABI, CSS, Unity API or Godot API change. Both hosts
use the same core implementation.

The core benchmark now accepts `--full --mutate=scroll --target='#items'`.
It sweeps within a nonzero scroll range instead of measuring a clamped no-op.
The update is timed; the scroll setter remains outside the timer. The CLI test
covers a real scroll workload and rejection of a non-scrollable target.

## Final paired measurements

Baseline: `a282a0ca`, including the preceding parallel background rasterization.
Windows Ryzen 7 9800X3D / RTX 5080, qualified patched Godot 4.7.2, Vulkan Mobile,
1280×720. Each variant runs all nine workloads three times, with 120 warmups
and 600 measured frames per workload. Before/after order alternates. No builds
or other audit benchmarks overlap. User desktop activity is not controlled.

Values below are medians of three run API CPU p95 values. API time includes
drive calls and synchronous updates, excluding later drawing and GPU work.

| Workload | Before | After |
|---|---:|---:|
| Empty window | 0.001 ms | 0.001 ms |
| Idle HUD | 0.010 ms | 0.009 ms |
| Active HUD | 0.596 ms | 0.604 ms |
| Unchanged HUD writes | 0.026 ms | 0.026 ms |
| Bound HUD updates | 0.669 ms | 0.663 ms |
| Menu hover | 0.125 ms | 0.122 ms |
| Menu opacity animation | 0.143 ms | 0.139 ms |
| Inventory scrolling | 1.245 ms | 1.097 ms |
| Chat, including frames between keys | 0.092 ms | 0.086 ms |

All **54/54 workload runs** pass, and all **24/24 paired PNG captures are
byte-identical**. The final inventory and HUD captures were inspected. An
earlier candidate experiment showed a 12.1% active-HUD gain, but that did not
hold in the final repeat: active and bound HUD performance is effectively
unchanged. The reliable result is scrolling; do not generalize small shifts
in the other rows into broad speedup claims. Both experiments are retained.

Separate paired GCC Release runs in WSL use the same inventory markup/CSS,
the built-in font backend and 5,000 scrolling updates per run, three pairs.
Median run means are 0.636 → 0.540 ms. Final-pass allocation traffic decreases
from **3,039 allocations / 1,795,328 bytes** to **2,941 / 1,738,880**: 98 fewer
allocations and 56,448 fewer requested bytes. These are allocation traffic,
not retained or peak memory. Instrumented CPU/phase profiles are separate
from the reported uninstrumented timings.

This experiment does not measure cold opening, GPU duration, busy game scenes,
lower-end hardware or Unity performance. The prior cold-opening results remain
scoped to their own binaries and reports. Inventory reorder and settings
toggles remain separate workloads needing current measurements.

## Correctness and evidence

The geometry allocation suite adds 36 comparisons against independent scalar
AA construction across subdivision counts, fractional coordinates, asymmetric
and clamped radii, and square corners. It also pins the thin-border budget to
four allocations. All **176 geometry checks** pass. Existing geometry and
draw-mesh output digests match the baseline exactly, including the 56-case
paint/clip/filter/transform set.

The complete `check.sh --release` gate passes with no skipped gates: 14 GCC
suites, 16 Clang sanitizer suites, 334 Chrome layout captures, 66 Chrome
behavior scripts plus the existing known IME failure, and Godot rendering,
Unicode, export and interaction checks. GCC, Clang and both Windows host
builds have no compiler warnings. Unity Native EditMode passes 242 tests with
the same two environment-gated inconclusive tests.

Raw evidence is preserved in `.utmp/runtime-opt-20261011/`, including both
Godot experiments, saved baseline binaries, profiles, paired core logs,
geometry digests, builds, full release gate and Unity XML. The first CLI test
run failed because its CMake argument containing `#` was unquoted; the harness
was corrected, rerun and passed. That failed log remains preserved.

The [verification receipt](runtime-geometry-20261011.json) records final gate
results, per-run timing data, binary and evidence hashes. Reproduce Godot with
`hosts/godot/run_game_ui_bench.py --baseline <saved-library> --library <candidate-library>
--godot <qualified-editor> --runs 3 --frames 600 --warmups 120 --out <new-directory>`.
Repeat core scroll runs with `weva_bench`, the saved inventory fixture and
`5000 --full --mutate=scroll --target='#items'`.
