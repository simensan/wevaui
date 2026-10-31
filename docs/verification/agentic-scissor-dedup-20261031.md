# Avoid redundant Agentic scissor commands — 2026-10-31

Agentic now emits a scissor command only when the normalized rectangle changes
within a render pass. The first usable batch always establishes its state,
preserving the [inherited-scissor fix](agentic-pass-scissor-20261031.md).
Clip clamping, empty-clip skips, draw order, textures, blending and backdrop
pass boundaries remain unchanged. State is local to each pass invocation.

Four alternating real-font pairs reduce command-encoding CPU in every pair:
**5.04% median paired for HUD and 4.26% for inventory selection**. This is a
small host optimization; it does not change layout or paint generation.

| Changing workload/stage | Baseline (ms) | Candidate (ms) | Median paired change | Wins |
| --- | ---: | ---: | ---: | ---: |
| HUD encoding | 0.1120 | 0.1065 | -5.04% | 4/4 |
| HUD complete CPU | 0.6065 | 0.5920 | -2.64% | 4/4 |
| Inventory encoding | 0.1305 | 0.1235 | -4.26% | 4/4 |
| Inventory complete CPU | 1.3075 | 1.2735 | -2.61% | 3/4 |

Values are medians of four per-process means; paired percentages are medians
of corresponding run ratios. The unchanged core also varies between runs
(inventory paint is 2.48% lower in the paired median), so the complete-CPU
difference must not all be attributed to this host change. Retained HUD CPU
is mixed. No outlier or losing pair is discarded. These results do not claim
GPU execution, FPS, p95 latency or universal improvement.

## Measurement and validation

Baseline Agentic `3c84a1819` includes the scissor correctness fix. Candidate
`6ab7386e36d2f18fdf8c83150b2e9ea1f6e130de` changes only `render.rs` on the leased
`codex/weva-render-audit` branch. Both optimized diagnostic binaries use the
same current shared core from Weva `6a30cf9a` through explicit `WEVA_SOURCE_DIR`.

The prepared survival HTML/CSS, real font and PNGs, twenty-field selection
mutations, black background and 1280×720 viewport are unchanged. Each fresh
process warms 20 updates per case, then measures 200 changing and 200 retained
updates at requested 60 Hz: 6,400 measured updates across eight processes.
No build or test overlaps timing. Diagnostic paint/font logging and optional
GPU timestamps are disabled. Initial/final target bounds match exactly across
all eight processes. Encoding is host CPU elapsed; complete CPU additionally
includes controller/update, capture, resource preparation and queue submission.

The selected full `agentic_weva` suite passes **84 tests**, zero failures,
with the same two ignored Arabic-font/optimized-timing cases. It includes the
inherited-scissor GPU regression, clipping, blending, backdrop effects,
viewport updates and queued document ownership. Separate optimized before/after
HUD and inventory renders are pixel-identical; the candidate inventory image
was inspected. These captures cover initial fixture states, not every mutation.

Unity and Godot use already-clipped core geometry and do not implement this
Agentic per-batch scissor loop. No equivalent redundant commands were identified
there, and no shared-core, ABI or supported-host change is needed. This is
Windows evidence; macOS and GPU/presentation timing remain outside its scope.

The change is committed on the Agentic audit branch, not pushed, merged into
the configured engine or published as an SDK. The [JSON receipt](agentic-scissor-dedup-20261031.json)
contains all run means, geometry/cadence data, binary hashes and verification
evidence. Raw files remain in
`.utmp/render-audit-20261030/agentic-scissor-dedup/`.
