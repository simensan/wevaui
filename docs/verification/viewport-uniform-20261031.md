# Retained viewport uniforms — October 31, 2026

The host engine's document renderer now uploads its viewport/color-space uniform only
on first preparation or a viewport change. Color space is fixed when the
renderer is constructed. A new draw serial or document identity with unchanged
dimensions does not require rewriting those bytes. Resize still uploads the
new dimensions, and complete-frame reuse keeps its existing early return.

This removes a redundant queue buffer write on ordinary changed UI frames.
It does not add a cache, extend texture lifetime, or alter core/host APIs.

## Verification

All 14 focused renderer tests pass, including a new GPU readback sequence at
64→128→128→64 pixel widths with changing document colors. It verifies the
painted region, the clear region after enlargement, and identical output on
repeated preparation of the same snapshot. Existing tests cover document
identity/texture changes and queued rendering after reload/destruction.
Receipt: Host-engine `builds/verify/20261031-013103`. The optimized SDK builds.

Three alternating before/after process pairs cover five modes in the same
720p 240-cube scene: 120 warmups and 600 measured frames per mode, 18,000
measured frames total, CPU-only sampling, no concurrent builds/tests.
All six processes pass clicks, use/close and post-reload clicks, with 656
reloads. All sessions stop and all six inventory captures are byte-identical
to the previously inspected output. The runner checks immutable hashes,
unmarked frames, one measured configuration and 243 entities throughout.

Median of three per-run statistics, milliseconds:

| Mode | Worker UI-phase mean before → after | Frame median before → after | Frame p95 before → after |
| --- | ---: | ---: | ---: |
| Hidden HTML | 0.023260 → 0.023165 | 0.50995 → 0.50955 | 0.6441 → 0.6622 |
| Static HUD | 0.067440 → 0.069464 | 0.55985 → 0.56150 | 0.7381 → 0.7637 |
| Updating HUD | 0.089173 → 0.087538 | 0.74375 → 0.73550 | 0.9236 → 0.9179 |
| Cycling inventory | 0.157597 → 0.155297 | 1.20960 → 1.20725 | 1.4165 → 1.4142 |
| Static inventory | 0.081378 → 0.081363 | 0.56645 → 0.56590 | 0.7405 → 0.7343 |

The changed-frame worker difference is small: approximately 1.8% for HUD and
1.5% for inventory. Static HUD is slower in this batch, despite static frames
already bypassing preparation in both versions. These data support a modest
observed reduction, not a large FPS gain or universal latency guarantee.
Worker UI-phase cost includes pending presentation submission; it is not GPU
execution time or a sum to add to the main-thread interval.

Before is host-engine `851ffbd71`, SDK
`ffb6261eaf36e64770157d0f910ee6136f285c08be1248d2c7d9454d08b101e8`.
After is `26fc09a7e`, SDK
`b0764fc224eab5f2c3e8fa1c247e7c8a99989582e19cdba98de2621f81f3c636`.
Weva build source is `7d9f5d9f`; the shared core remains unchanged since
`d863b7d3`. [All run statistics and runtime hashes](viewport-uniform-20261031.json).
Raw evidence: `.utmp/render-audit-20261030/viewport-uniform-paired/` and
`viewport-uniform-sdk.log`.

This is a host-engine wgpu buffer-ownership change. Godot and Unity do not use
this buffer; their accepted shared-core behavior is unchanged. No new full
release gate, GPU comparison, upstream merge or configured-SDK switch is claimed.
