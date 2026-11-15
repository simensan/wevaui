# Rendering resolution audit — 2026-10-31

On the tested RTX 5080, this isolated UI's retained inventory GPU draw cost
rises with target area but remains small: **0.0106 ms at 720p, 0.0142 ms at
1080p and 0.0340 ms at 4K**. Changing-inventory complete CPU stays around
1.2 ms. This supports continuing CPU-side investigation for this workload;
it does not establish performance in a GPU-busy game or on lower-end hardware.

| Target | HUD changing CPU mean | HUD retained GPU median / p95 | Inventory changing CPU mean | Inventory retained GPU median / p95 |
| --- | ---: | ---: | ---: | ---: |
| 1280×720 | 0.568 ms | 0.004864 / 0.005792 ms | 1.229 ms | 0.010592 / 0.012768 ms |
| 1920×1080 | 0.579 ms | 0.004800 / 0.005664 ms | 1.255 ms | 0.014240 / 0.016320 ms |
| 3840×2160 | 0.575 ms | 0.003904 / 0.004800 ms | 1.199 ms | 0.033952 / 0.034720 ms |

Each table entry is the median of three per-process statistics. CPU and GPU
columns are separate passes and workloads; they must not be added to estimate
frame time or divided into a CPU/GPU speed ratio. Lower HUD GPU time at 4K
is not a resolution speedup claim; no monotonic model is fitted to these data.

## Workload and measurement boundaries

Nine fresh optimized host-engine processes rotate resolution order across three
rounds. Each uses identical prepared survival markup, CSS, real fonts, PNGs and
twenty-field inventory selection mutations. CPU frames target 60 Hz, with
20 warmups and 200 changing plus 200 retained updates per case. Across HUD and
inventory this is **7,200 measured CPU updates**. No build/test overlaps timing.

The separate GPU pass renders the final retained state with 20 warmups and
180 measured timestamp pairs per case: **3,240 measured GPU intervals**.
These GPU samples are submitted as an uncapped burst, not paced game frames.
The timestamp range encloses `render_document`, including its required render
passes. It excludes the preceding black target clear, CPU encoding/submission,
resource preparation, query resolution, presentation and the game world.
Every sample is nonnegative, no case is all-zero, and each run reports a 1 ns
timestamp period. There is no sample subtraction or outlier removal.

The runtime reports NVIDIA GeForce RTX 5080, Vulkan, driver 617.42. Host-engine is
`6ab7386e36d2f18fdf8c83150b2e9ea1f6e130de`, Weva checkout `1c2e0a13`; the
diagnostic hash matches the accepted scissor-command candidate. No source,
host API, SDK selection or configured runtime changes in this audit.

## Geometry, visuals and cold-start limits

Initial/final mutation-target bounds are identical across the three runs at
each resolution. All nine processes complete with the diagnostic's setter and
target checks. Six additional renders have the requested pixel dimensions;
the 1080p HUD and 4K inventory were visually inspected.

**CSS pixel sizes are unchanged.** The 4K inventory therefore occupies a smaller
fraction of the screen, and text is physically smaller. This measures larger
native-pixel viewports, not 2×/3× UI scaling, device-pixel-ratio support, 4K
readability or a release-ready responsive design. Captures represent initial
fixture states, while GPU timing uses the final retained mutation state.

Fresh-process diagnostic creation-to-first-submit medians are 22.2–24.0 ms
for HUD and 25.4–27.7 ms for inventory. Inventory is the second document on
an already-created device. Those intervals stop at CPU submission, exclude
GPU completion and do not represent a cold player or shader-cache qualification.
Their raw stage breakdowns are retained, but no cold-start improvement is claimed.

This pass changes no runtime code; Unity and Godot retain the same shared core.
It is host-engine-only measurement and must not be attributed to their renderers.
The remaining busy-world, lower-end, scaled-UI, long-duration memory and
matched Chrome GPU/presentation gaps are not closed by these results.

The [JSON receipt](resolution-audit-20261031.json) records revisions, binary and
fixture hashes, every run, timestamp samples and geometry. Logs and six renders
are in `.utmp/render-audit-20261030/resolution-audit/`; reproduction is
`run_resolution_audit.py` beside that directory and `render.py` within it.
