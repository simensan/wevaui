# Flex-row allocation: rendered Godot measurement — 2026-10-31

The shared-core change in `a0943b78` reduces inventory-selection controller/update
CPU in all four paired Godot runs: **10.49% median paired reduction**. P95 improves
in three pairs, with a **31.19% median paired reduction**; the third pair worsens
from 2.079 to 2.592 ms. This extends the separate
[Unity and Agentic evidence](flex-row-allocation-20261031.md).

## Method and limits

Baseline `bcb337f1` and candidate `a0943b78` use the same Windows Release build
configuration. Only the three shared-core implementation/header files differ;
the host and survival controller are unchanged. Source and candidate addon are
restored after comparison. Four pairs alternate fresh-process order.

Godot 4.7 Mobile/Vulkan renders five survival UI cases at 1280×720 on RTX 5080,
capped at 60 Hz with vsync disabled. Each case warms for 120 frames, then
measures 240: **9,600 measured frames** across eight processes. GPU timestamps,
core-stat collection and diagnostic logging are disabled. No builds or tests
overlap timing. All samples and outliers are retained.

Controller/update includes selection mutations and `update_document()`, excluding
deferred canvas draw translation and GPU execution. Render setup is separate.
These timings do not establish an FPS or total GPU rendering improvement.
This is the sample UI over its normal background, not a busy 3D-world or
lower-end-hardware qualification.

## Results

Milliseconds are medians of four per-process median / p95 values. Paired changes
are medians of the four percentage changes, not ratios of the aggregate columns.

| Controller/update workload | Baseline median / p95 | Candidate median / p95 | Paired median change |
| --- | ---: | ---: | ---: |
| Background control | 0.0010 / 0.0020 | 0.0015 / 0.0025 | 0.00% |
| Idle HUD | 0.0085 / 0.0130 | 0.0100 / 0.0170 | 0.00% |
| Changing HUD | 0.2145 / 0.3875 | 0.2175 / 0.3890 | +3.03% |
| Retained inventory | 0.0090 / 0.0170 | 0.0100 / 0.0180 | +11.11% |
| Full inventory selection | 0.9770 / 2.3490 | 0.8855 / 1.4705 | -10.49% |

HUD and retained cases do not establish a benefit; changing HUD loses three
of four median pairs. Tiny idle differences are near the microsecond timer's
resolution. Selection render-setup median is unchanged at 0.007 ms. Frame
median stays at the 16.666 ms cap; selection frame p95 worsens from 16.741 to
16.830 ms and loses all four pairs. A lower update cost does not establish
that every stage or tail improves.

All **40 captured images** are RGB-identical to the corresponding baseline
case, including final selection. The candidate inventory render was inspected.
This checks captured states, not every intermediate frame. The production
change's incremental/full-layout matrix, Chrome cases and full host gate are
recorded in the implementation receipt. This follow-up changes no production code.

The [JSON receipt](flex-row-godot-20261031.json) records every run's summaries,
build hashes and screenshot RGB hashes. Raw samples, logs, binaries and captures
remain under `.utmp/render-audit-20261030/flex-proof-godot-pairs/`.
