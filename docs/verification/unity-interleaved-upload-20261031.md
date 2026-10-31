# Unity interleaved mesh upload — October 31, 2026

Changed ordinary mesh uploads from three scratch lists and separate position,
color and UV uploads to one interleaved list and vertex-buffer upload. The
layout remains three Float32 position values, four color values and two UV
values: 36 bytes per vertex, preserving the shader inputs and color precision.
Index rebasing, 16/32-bit selection, bounds, batching and version reuse remain
unchanged. Backdrop position-only geometry keeps its existing path.

The host uses Unity's documented
[SetVertexBufferData](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/mesh/setvertexbufferdata)
API. This is a Unity adapter improvement, not a core ABI change. Godot uploads
separate packed arrays through its canvas API and cannot directly use this
Unity-specific change; no Godot performance claim is made.

## Paired performance

Three baseline/candidate pairs alternate source revisions, with each run in a
fresh Unity process. Both versions use the same core plugin, fixtures and
benchmark. Each process runs 128 warmup plus 2048 measured iterations for
idle HUD, changing HUD and inventory highlight/quantity changes. There are
36,864 measured iterations across six processes. Compilation occurs before
measurement. Source snapshots and hashes are retained.

Median of per-run results, milliseconds:

| Workload / stage | Baseline median | Candidate median | Baseline p95 | Candidate p95 |
| --- | ---: | ---: | ---: | ---: |
| HUD synchronization | 0.0334 | 0.0295 | 0.0464 | 0.0446 |
| HUD CPU total | 0.1211 | 0.1104 | 0.1648 | 0.1658 |
| Inventory synchronization | 0.1056 | 0.0928 | 0.1520 | 0.1617 |
| Inventory CPU total | 0.2780 | 0.2607 | 0.4984 | 0.4779 |

Inventory synchronization median is 12.1% lower; measured CPU total median is
6.2% lower. Tails are mixed: inventory synchronization p95 rises 6.4%, total
inventory p95 falls 4.1%, and HUD total p95 rises 0.6%. Idle work does not take
the changed path; its timing variation is measurement noise, not an optimization.
This small sample supports a modest median upload improvement, not an always
faster result, a player frame-time gain, or a GPU rendering claim.

See the [runtime audit](unity-runtime-stages-20261031.md) for exact stage and
workload boundaries. GPU submission and frame pacing remain excluded.

## Evidence

All six processes completed and captures match across both versions and the
previously inspected reference. Raw samples/logs/images are in
`.utmp/render-audit-20261030/interleaved-paired`; the preflight is retained in
`interleaved-preflight`. The adjacent JSON records individual summaries and
evidence hashes. `run_interleaved_pairs.py` and the two renderer source snapshots
remain in the evidence root for reproduction with the runtime audit runner.

Full EditMode passes 252 tests with two existing environment-gated inconclusive
and zero failures, including the added 17,000-quad merged-mesh regression for
32-bit indices. Existing textured, blended, backdrop and material preparation
pixel tests pass with the new layout. No native/plugin ABI rebuild is required.
URP PlayMode passes 7/7; all 16 captures exactly match the previous renderer.
Windows x64 Mono development player passes 7/7; its captures also match. These
are correctness checks, not a player timing or IL2CPP/release stripping result.
Python tooling compilation and diff checks pass. The full integration gate
was not rerun for this host-only change.
