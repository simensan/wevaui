# Direct core vertex upload — 2026-10-31

Unity now copies the core's contiguous vertex records into a reusable managed
array and uploads that layout directly. It avoids constructing a position,
color and UV value for every vertex and adding each to a list. The position
descriptor is two floats, matching the core's xy; the shader already uses xy.
Each uploaded vertex is 32 bytes instead of 36. Color space, UVs, index rebasing,
16/32-bit index choice, bounds, batching and mesh clearing remain unchanged.

The array grows with checked counts, each native copy is bounded by its remaining
capacity, and only the populated prefix is uploaded. This follows the explicit
buffer-layout contract of [Unity's mesh API](https://docs.unity.cn/ScriptReference/Mesh.SetVertexBufferParams.html).
There is no ABI or shared-core behavior change. Godot already builds packed
two-component positions and requires separate color/UV arrays, including host
color conversion; this interleaved Unity upload is not applicable to its canvas
API. No Godot source or package compatibility surface changes.

## Paired performance

Three pairs of fresh Unity 6000.4.1f1 editor processes ran baseline/candidate,
candidate/baseline, baseline/candidate. Each process measured four workloads
after 128 warmups, 2048 iterations each: **49,152 measured iterations** total.
Full inventory includes all 20 mutations across twelve selections through
bindings. Every target was checked, and each final retained render exactly
matched a fresh renderer. Final PNG hashes matched across all six processes.
The candidate full inventory capture was visually inspected.

Milliseconds below are medians of the three per-process medians / p95 values.
Totals are aggregated from each iteration's sum, not independent percentiles.

| Workload | Baseline sync | Candidate sync | Baseline total CPU | Candidate total CPU |
|---|---:|---:|---:|---:|
| HUD | 0.06135 / 0.0769 | 0.05390 / 0.0723 | 0.22655 / 0.2818 | 0.22970 / 0.2803 |
| Quantity/highlights | 0.16410 / 0.1940 | 0.13405 / 0.1626 | 0.46130 / 0.5513 | 0.42295 / 0.5357 |
| Full inventory | 0.36145 / 0.5629 | 0.31835 / 0.5388 | 1.16425 / 1.3736 | 1.14335 / 1.4120 |

Full-inventory sync median fell **11.9%**, with a reduction in every pair.
Quantity/highlight sync fell **18.3%**. Full-inventory total median fell only
**1.8%**, and total p95 increased **2.8%**. HUD total median also increased
slightly. This is an upload-stage improvement with mixed end-to-end results,
not an always-faster or FPS claim. Unchanged update work and system variation
remain part of the full total. GPU submission, execution and frame pacing are
outside these editor timings; hardware was the same Windows RTX 5080 machine.

## Correctness and evidence

The new pooled-mesh regression alternates large and small geometry, crosses
16/32-bit addressing, switches into and out of backdrop geometry, and compares
every phase against a fresh renderer. It also pins the core vertex size at 32
bytes. Existing large-mesh/channel, texture replacement and rendering cases
remain in the affected suites.

- Full EditMode: **255 passed, two existing environment-gated inconclusive**.
- URP PlayMode: **7/7 passed**; all 16 captures exactly match the prior renderer.
- Windows x64 Mono development player: **7/7 passed**; all 16 captures exactly
  match the prior renderer.
- Inspected the pooled backdrop and large-mesh captures as well as the full
  inventory benchmark image. Python evidence checks and `git diff --check` pass.

The player runner's generated assets were cleaned by the runner; its two
whitespace-only ProjectSettings changes were restored after completion.

Raw paired evidence is in
`.utmp/render-audit-20261030/direct-vertex-paired/`. Baseline/candidate source
snapshots and `run_direct_vertex_pairs.py` are beside it. Correctness evidence
uses the `direct-vertex-edit`, `direct-vertex-play` and `direct-vertex-player`
prefixes in the same evidence directory. The adjacent JSON records hashes,
paired summaries and accepted test counts.

This does not cover IL2CPP, other graphics backends, weaker hardware or a matched
Chrome GPU comparison. Shared-core paint remains a separate optimization target.
