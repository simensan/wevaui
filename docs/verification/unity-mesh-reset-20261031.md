# Rejected mesh reset experiment — 2026-10-31

Removing `Mesh.Clear()` from ordinary pooled-mesh reuse did not improve the
full inventory selection workload. The production renderer was restored
byte-for-byte to its pre-experiment version. No speedup is shipped by this pass.

The candidate retained clearing for backdrop shapes, then relied on ordinary
`SetVertexBufferParams`, `SetVertexBufferData` and `SetIndices` calls to replace
the previous ordinary mesh data. Unity documents explicit vertex-buffer size
and layout setup in its [mesh API](https://docs.unity.cn/ScriptReference/Mesh.SetVertexBufferParams.html).
This was a measured experiment, not an assumption that removing a call must
improve performance. Godot's canvas clear removes prior commands before
`canvas_item_add_triangle_array` appends new ones; removing it is not equivalent.

Three pairs of fresh Unity editor processes alternated baseline/candidate,
candidate/baseline, baseline/candidate. Each process ran the four runtime audit
workloads, including complete inventory selection, with 128 warmups and 2048
measured iterations each: **49,152 measured iterations**. All target validation
and retained-versus-fresh-render checks passed. Each workload's final PNG hash
was identical across all six processes and both variants.

Full-selection synchronization median was worse in every pair (milliseconds):

| Pair | Baseline sync | Candidate sync | Baseline total | Candidate total |
|---|---:|---:|---:|---:|
| 1 | 0.35340 | 0.35585 | 1.15635 | 1.20195 |
| 2 | 0.37325 | 0.37505 | 1.26145 | 1.26915 |
| 3 | 0.35295 | 0.36120 | 1.19865 | 1.22875 |

Across runs, median sync increased 2.2% and median total increased 2.5%.
Smaller HUD and quantity-only workloads improved, and aggregate p95 was mixed;
those results do not justify keeping the change for the target full workflow.
These are CPU editor update/sync/record timings, excluding GPU submission and
frame pacing. No claim is made that the removed call itself caused the small
slowdown; the experiment failed to demonstrate the intended gain.

Evidence: `.utmp/render-audit-20261030/mesh-reset-paired/`,
`mesh-reset-baseline.cs`, `mesh-reset-candidate.cs` and
`run_mesh_reset_pairs.py`. The adjacent JSON preserves summaries and hashes.
Production suites were not rerun after restoration because production source
is unchanged. Failed or negative experimental evidence is retained.

Next candidate: ordinary upload currently reconstructs each 32-byte core vertex
into a 36-byte position/color/UV structure. Investigate preserving the core's
two-component position layout and copying contiguous vertices, with measured
comparisons and grow/shrink, index-width and backdrop reuse coverage before
acceptance. Godot already uses two-component packed positions, so this specific
conversion belongs to Unity rather than the shared core.
