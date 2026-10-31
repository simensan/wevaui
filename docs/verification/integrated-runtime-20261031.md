# Integrated Agentic runtime verification — 2026-10-31

The current shared core and Agentic renderer work together in the scripted
survival UI and synthetic 240-cube scene. Selection, use, close and reopen
passed; the captured inventory was visually inspected. This is a current-build
measurement, **not a paired speedup or a release qualification**.

| Case | Main UI median / p95 | Render-worker UI median / p95 | Whole-frame interval median / p95 |
| --- | ---: | ---: | ---: |
| Hidden | 0.0004 / 0.0004 ms | 0.0359 / 0.1312 ms | 0.7920 / 1.8783 ms |
| Static HUD | 0.0022 / 0.0025 ms | 0.0972 / 0.2967 ms | 0.8749 / 1.9901 ms |
| Updating HUD | 0.1893 / 0.3547 ms | 0.1227 / 0.3699 ms | 1.1263 / 2.1585 ms |
| Changing inventory | 0.5940 / 0.9418 ms | 0.1988 / 0.4595 ms | 1.6184 / 2.8443 ms |
| Static inventory | 0.0022 / 0.0025 ms | 0.1175 / 0.3803 ms | 0.9042 / 2.0053 ms |

Each entry is the median of three per-run statistics in **one process**;
these are not three independent launches. Five modes, alternating forward and
reverse order, each use 120 warmup and 600 measured CPU-only frames: 9,000
measured frames. Thread timings can overlap and must not be added to frame
time. Hidden worker UI includes host overhead, not hidden-document layout.
Absolute frame intervals are higher than the earlier submission receipt;
without an interleaved baseline this run cannot attribute that difference to
code, nor establish a cumulative improvement.

A separate static-inventory GPU-diagnostics pass requested 600 frames after
120 warmups. Only **517 GPU samples** returned. Its UI interval median was
0.014272 ms, p95 0.015232 ms, p99 0.528 ms and maximum 1.039584 ms.
Missing samples and retained outliers prevent a complete-frame-tail claim.
Diagnostic CPU frame times are excluded from the CPU table. GPU intervals
are not equivalent to the isolated resolution audit's draw-only timestamps.

## Reproduction and scope

- Shared checkout: `acbb7089`; latest core behavior is the accepted flex-row
  allocation change. Agentic: `6ab7386e36d2f18fdf8c83150b2e9ea1f6e130de`.
- Locally published immutable SDK:
  `db868f7bb698e366c41911bf6f45e70733c9678e01acdf34b7f41e3730ca5248`.
  Built through `tools/sdk_build.py --components cli headless runtime` with
  `WEVA_SOURCE_DIR` pointing to this checkout. No global SDK was retargeted.
- Windows, RTX 5080, Vulkan NVIDIA 617.42, 1280×720, AutoNoVsync,
  offdesktop window; configured foreground/background FPS limits are zero.
- Existing `agentic-runtime-project-v2` fixture and controller are unchanged.
- Raw evidence: `.utmp/render-audit-20261030/integrated-runtime/`;
  runner: `.utmp/render-audit-20261030/run-integrated-runtime.py`;
  SDK build receipt: `current-runtime-sdk.log` in the same parent directory.
- The initial runner incorrectly required `completed` from asynchronous
  launch, which correctly returned `accepted`. Its launch evidence was
  retained and the runner resumed the same owned session. Subsequent commands
  completed; the session was stopped successfully.
- Inspected inventory PNG SHA-256:
  `00af05c633c5d52c7f58d934515f7e7aa25d9307504e36c7a970dbd45d747d0a`.

No engine code changes in this chunk. Shared optimizations already reach
Unity and Godot through libweva; their paired verification remains in the
flex-row receipts. Agentic scissor commands have no equivalent per-batch loop
to port to those hosts. Lower-end hardware, a GPU-busy production game,
long lifecycle soak and macOS remain outside this result.
