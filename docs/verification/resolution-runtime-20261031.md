# Agentic runtime at 1080p and 1440p — October 31, 2026

The accepted Agentic submission and retained-uniform changes also reduce
observed worker UI-phase CPU cost at larger render targets. This comparison
covers the combined changes from Agentic `4c5cf4ecd` to `26fc09a7e`, not an
isolated uniform optimization. The shared Weva core remains `d863b7d3`.

The audit generator and runner now accept `--viewport WIDTH HEIGHT`. The
runner verifies the actual profile viewport and offsets clicks for the
fixture's centered, fixed-size inventory. The minimum supported fixture
viewport remains 1280×720. The default invocation is unchanged.

## Method and checks

Each resolution uses three alternating before/after process pairs, five
modes, 120 warmup frames and 600 measured frames per mode. Across both
resolutions this is 36,000 measured frames, 12 processes and 1,332 reloads
(663 at 1080p and 669 at 1440p). All processes pass selection, equip, close
and post-reload clicks and stop successfully. All six selected-inventory
captures at each resolution are byte-identical; an after capture at each
resolution was visually inspected. The centered panel, selected hatchet,
text, HUD and scene render correctly.

The runner also verifies artifact hashes, 243 entities throughout, unmarked
frames, one measured configuration and normal CPU-only sampling. Runs were
sequential without concurrent builds/tests. Both SDKs use the same core:

- Before SDK: `733f49a0c786cdc9ef5884aa140a5e93bf140e0d11ffa99d92112a62cd06dfc8`.
- After SDK: `b0764fc224eab5f2c3e8fa1c247e7c8a99989582e19cdba98de2621f81f3c636`.

## Results

Median of three per-run statistics, milliseconds. Worker cost includes
pending presentation submission and is not GPU execution time. It overlaps
main-thread work and must not be added to frame intervals.

| Resolution / mode | Worker UI-phase mean before → after | Frame median before → after | Frame p95 before → after |
| --- | ---: | ---: | ---: |
| 1080p hidden HTML | 0.024417 → 0.023429 | 0.51935 → 0.50660 | 0.6744 → 0.6574 |
| 1080p static HUD | 0.080333 → 0.068828 | 0.59225 → 0.57350 | 0.7748 → 0.7537 |
| 1080p updating HUD | 0.102067 → 0.088356 | 0.76020 → 0.73960 | 0.9562 → 0.9412 |
| 1080p cycling inventory | 0.166762 → 0.147389 | 1.23215 → 1.18730 | 1.4553 → 1.3876 |
| 1080p static inventory | 0.094243 → 0.081467 | 0.59975 → 0.58070 | 0.7489 → 0.7377 |
| 1440p hidden HTML | 0.028681 → 0.027082 | 0.54945 → 0.52445 | 0.7608 → 0.6863 |
| 1440p static HUD | 0.082170 → 0.068400 | 0.60080 → 0.57895 | 0.7728 → 0.7983 |
| 1440p updating HUD | 0.113119 → 0.092521 | 0.84275 → 0.76840 | 1.1574 → 0.9787 |
| 1440p cycling inventory | 0.168484 → 0.147320 | 1.24000 → 1.21815 | 1.5356 → 1.4837 |
| 1440p static inventory | 0.117549 → 0.083275 | 0.71600 → 0.59085 | 1.1310 → 0.7964 |

Cycling inventory worker cost falls 11.6% at 1080p and 12.6% at 1440p;
whole-frame medians fall 3.6% and 1.8%. Updating HUD worker cost falls
13.4% and 18.2%. The 1440p static HUD p95 worsens 3.3%, so this does not
establish an improvement in every latency statistic.

This is one RTX 5080 machine and a synthetic 240-cube scene with fixed CSS
dimensions. Increasing resolution enlarges the scene and full-screen overlay,
not the inventory or text. It does not establish DPI scaling, lower-end GPU
performance, GPU execution gains, cold-opening performance, long-session
memory stability or Chrome parity.

[All run statistics, runtime hashes and session checks](resolution-runtime-20261031.json).
Raw evidence: `.utmp/render-audit-20261030/resolution-1080-paired/` and
`resolution-1440-paired/`.

No engine behavior changed in this validation chunk. The previously accepted
shared-core fixes remain available to Godot and Unity. Agentic's wgpu
submission/uniform changes have no equivalent buffer ownership in those
hosts and are not copied into them. Their existing gate results stand;
this is not a new full release gate or a host-to-host performance comparison.
The Agentic changes remain on the local audit branch; no upstream merge,
push or configured-SDK switch is claimed.
