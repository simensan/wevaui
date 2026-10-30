# Current Agentic SDK runtime — October 31, 2026

The accepted shared-core changes now have actual Agentic runtime validation,
using a newly published local SDK. Inventory median frame interval improves
10.7% and mean main-thread UI CPU improves 14.9% (medians of three run
statistics). Tail results are mixed: the median of inventory run p95 frame
intervals increases from 1.6818 to 1.9230 ms. This is not an across-the-board
performance win or Chrome parity evidence.

## Method and provenance

`Tools/performance/run_agentic_runtime_audit.py` drives explicit immutable SDKs,
without changing the configured engine. The fixture is the prepared survival
UI in a 240-cube world (243 entities throughout), 1280×720, threaded renderer,
VSync disabled, off-desktop/unfocused, on the existing Windows RTX 5080 machine.
Three alternating before/after process pairs reverse mode order on odd rounds.
Each of five modes has 120 warmup and 600 measured frames: 30 profiles and
18,000 measured frames. CPU-only sampling preserves normal renderer scheduling;
no GPU execution timestamps are collected. No builds overlap these profiles.

Before uses Agentic `88e0e7f4b` and Weva `faf04f31`, SDK
`f0da9c269542cfcb828793e7644d09d57a222103159fd90f6cf022a3c812bdcd`.
After uses Agentic `5c5d8ca98` and Weva `d863b7d3`, SDK
`7b76a22b973cf346f9a8b407e46b1b30c9eb0e5a347fcb28b423bf1ffbbc065d`.
The new SDK was built with explicit `WEVA_SOURCE_DIR` using
`tools/sdk_build.py --components cli headless runtime --publish <local-output>`.
The SDK manifest does not directly record the Weva commit; the build invocation
and clean source revision supply that provenance. Runtime executable hashes,
per-run statistics and capture hashes are in the adjacent JSON receipt.

## Results

Median of three per-run p95 values, milliseconds:

| Mode | Frame interval before → after | Main UI CPU before → after | Worker UI CPU before → after |
| --- | ---: | ---: | ---: |
| Hidden UI | 0.7650 → 0.8182 | 0.0350 → 0.0424 | 0.0337 → 0.0403 |
| Static HUD | 0.8511 → 0.8171 | 0.1139 → 0.1062 | 0.1110 → 0.1031 |
| Updating HUD | 1.0526 → 0.9475 | 0.3517 → 0.2879 | 0.1822 → 0.1282 |
| Cycling inventory | 1.6818 → 1.9230 | 1.0065 → 1.1013 | 0.2242 → 0.2910 |
| Static inventory | 0.8087 → 0.7832 | 0.1195 → 0.1179 | 0.1173 → 0.1142 |

Inventory pair detail prevents the aggregate from hiding variability:

| Round | Frame median before → after | Frame p95 before → after | Main UI mean before → after |
| --- | ---: | ---: | ---: |
| 0 | 1.43280 → 1.29275 | 1.6818 → 1.9230 | 0.90222 → 0.77728 |
| 1 | 1.44700 → 1.24550 | 1.6783 → 1.4417 | 0.91312 → 0.71918 |
| 2 | 2.43625 → 1.70505 | 3.5209 → 2.3298 | 1.60025 → 1.05232 |

Inventory median and mean UI cost improve in every pair, while p95 worsens in
round 0. Both versions have slower round-2 inventory profiles; the cause is
unproven. Main/worker intervals overlap and must not be summed. Updating every
tick is a stress workload, not human click latency. The threaded binding path
already filters equal values, so standalone C ABI gains do not transfer directly.

## Behavior and lifecycle

All six processes pass item selection, use, close and post-reload click checks.
They complete 716 reloads total (384 before, 332 after), at least 100 each.
All six selected-inventory PNGs are byte-identical; the after capture was
visually inspected and shows the selected hatchet, details and usable controls.
Every profile has 600 unmarked frames, one measured runtime configuration,
no missing configuration, and the expected entity count. Immutable response
artifacts are hash-checked. All six owned sessions were stopped successfully.

Whole-process private-byte growth across the short reload loops ranges from
512,000 to 1,622,016 bytes. This is a short functional lifecycle check, not a
long-duration leak result. Lower-end hardware, larger resolutions, GPU timing
and representative production worlds remain outside this result.

An initial runner attempt incorrectly hash-checked the growing runtime log.
Its failure evidence is retained and its owned session was stopped. The runner
now excludes mutable log artifacts; that attempt contributes no measurements.

## Evidence and host scope

Local raw evidence is under `.utmp/render-audit-20261030/`:
`current-sdk-build.log`, `current-runtime-comparison-v2/` (profiles, lifecycle,
raw responses, captures and stop receipts), and `current-runtime-comparison/`
(excluded initial failure). The adjacent JSON retains compact measured data.

This chunk adds a reusable runner and evidence only. Shared changes already
reach Godot and Unity through the core, with their affected host checks recorded
in [the no-op receipt](scoped-noop-20261031.md). No ABI change or new full gate
is claimed here. Agentic remains on its local audit branch; managed upstream
and the user's configured SDK are unchanged. Nothing was pushed.
