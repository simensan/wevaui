# Agentic UI submission optimization — October 31, 2026

Agentic now appends HTML overlay passes to the pending presentation encoder,
removing a separate queue submission when both are present. Rendering order
stays presentation then HTML, including backdrop reads. The GPU UI marker is
encoded after presentation. An already-consumed presentation encoder still
leads to the ordinary standalone HTML encoder.

The companion change keeps empty legacy-screen preparation on the render
worker, where the existing renderer performs cache cleanup. Active layouts
still use parallel preparation. Empty serial preparation no longer flushes
presentation or starts a UI marker. The empty-job-only comparison was
essentially flat; the combined submission change is the measured improvement.

## Paired runtime results

Three alternating process pairs reverse mode order on odd rounds. Each of
five modes has 120 warmup and 600 measured frames: 18,000 measured frames in
the existing 1280×720 240-cube world, RTX 5080/Vulkan, no VSync, off-desktop,
CPU-only sampling. No builds/tests overlapped these profiles. Medians of
three per-run statistics, milliseconds:

| Mode | Worker UI-phase mean before → after | Frame median before → after | Frame p95 before → after |
| --- | ---: | ---: | ---: |
| Hidden HTML | 0.02401 → 0.02311 | 0.51430 → 0.51090 | 0.6508 → 0.6442 |
| Static HUD | 0.07864 → 0.06793 | 0.56920 → 0.56270 | 0.7307 → 0.7454 |
| Updating HUD | 0.10187 → 0.08792 | 0.75575 → 0.73680 | 0.9205 → 0.9143 |
| Cycling inventory | 0.18845 → 0.15614 | 1.25105 → 1.20935 | 1.4257 → 1.4034 |
| Static inventory | 0.09110 → 0.08235 | 0.57385 → 0.58105 | 0.7363 → 0.7677 |

Cycling inventory worker cost falls 17.1%; frame median falls 3.3%. Updating
HUD worker cost falls 13.7%. Worker cost improves in all modes, but static
inventory frame median/p95 and static HUD p95 worsen slightly. This is not
universal latency improvement, lower-end evidence or Chrome rendering parity.

`ui_worker_cpu` excludes the UI join wait but includes any pending presentation
submission flushed in that phase. Hidden HTML's nonzero worker value does not
prove hidden HTML is drawn. Main-thread UI remains separate. Do not add
overlapping thread intervals, or compound these percentages with old receipts.

## Validation and provenance

All six processes pass selection, use, close and post-reload clicks, with 713
reloads total. All six capture hashes equal the previously inspected image
(`df5d0ec8b0509c51588985f324465baef9302c3dd4bddc98626bbc2cf242c17e`),
and every owned session stops. The runner verifies 243 entities throughout,
one runtime configuration per profile, unmarked frames and immutable hashes.

A separate GPU-diagnostics run measures 600 frames with 599 matched timestamp
samples, zero dropped passes and one pending/missing sample. Every matched UI
sample is finite and nonnegative; median is 0.012928 ms. This verifies the
encoded marker path and is not a paired GPU speedup result.

The focused Agentic `render_thread::tests` suite and its production compile
check pass (`builds/verify/20261031-012812`); the optimized SDK build passes.
This is affected-path validation, not a full engine/release gate.

Before: Agentic `4c5cf4ecd`, SDK
`733f49a0c786cdc9ef5884aa140a5e93bf140e0d11ffa99d92112a62cd06dfc8`.
After: Agentic `851ffbd71` (including empty-job change `a399c1039`), SDK
`ffb6261eaf36e64770157d0f910ee6136f285c08be1248d2c7d9454d08b101e8`.
Both use the accepted Weva core; after build source is `eb8559d6`, with core
unchanged since `d863b7d3`. [Every run and hashes](ui-submission-20261031.json).

Raw evidence is `.utmp/render-audit-20261030/ui-submit-paired/`,
`ui-submit-gpu/`, `ui-submit-sdk.log`; the isolated first comparison is retained
at `empty-ui-paired/`. No core ABI or Godot/Unity code changed: this optimization
belongs to Agentic's wgpu encoder ownership. Accepted shared-core behavior
remains in sync. Agentic stays on the leased audit branch; nothing was pushed
or merged into managed upstream and the configured user SDK is unchanged.
