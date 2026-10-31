# Rendering audit results — 2026-10-31

**Follow-up qualification:** the [IL2CPP run](il2cpp-qualification-20261031.md)
fixed a real callback-marshalling startup failure. It also discovered five
leftover audit players rendering during earlier benchmarks. Timing percentages
below are historical and require clean remeasurement; correctness evidence
remains applicable. [Agentic is now integrated locally](agentic-integration-20261031.md).
The [30-minute lifecycle soak](lifecycle-soak-20261031.md) passes interaction
checks but shows unresolved CPU-memory growth.
The [clean CPU comparison](clean-render-comparison-20261031.md) replaces the
old current-build comparison and still shows an inventory gap against Chrome.

The deep rendering audit and optimization work is complete. It identified and
fixed shared-core and host costs, verified measurable improvements with real
fonts and rendered UI, and checked the resulting integration. This does **not**
mean Weva is release-qualified on every platform, always faster than Chrome,
or free of cold-opening hitches. Those stronger outcomes remain unproven.

## Delivered results

The latest shared-core flex change reduces full inventory selection CPU by
**14.92% in Unity**, **10.49% in Godot**, and **12.57% in Agentic**, using the
paired metrics defined in their receipts. These are different timing boundaries,
not directly comparable host rankings. The percentages must not be compounded
with earlier improvements. HUD, retained work and frame tails remain mixed.

| Requirement | Evidence and disposition |
| --- | --- |
| Audit the rendering pipeline deeply | Controller, invalidation, cascade, box/layout, glyph/paint, snapshot, texture/mesh preparation, encoding, submission and GPU intervals were examined. [Integration](render-audit-integration-20261031.md), [paint callers](paint-callers-20261031.md), [GPU/resolution](resolution-audit-20261031.md). |
| Deliver measured speed increases | [Shared flex allocation](flex-row-allocation-20261031.md), [rendered Godot pairs](flex-row-godot-20261031.md), [Unity direct vertices](unity-direct-vertices-20261031.md), [Agentic command encoding](agentic-scissor-dedup-20261031.md). These remain in source. |
| Keep Unity and Godot synchronized where applicable | Both receive the same optimized libweva; current installed native binaries match the accepted candidate hashes. Host-specific paths were inspected instead of duplicating inapplicable implementations. |
| Preserve behavior and rendering | Full qualified gate, Chrome captures, incremental/full-layout equivalence, Unity editor/player checks, Godot captures, and Agentic GPU regressions cover the accepted changes. See exact scopes below. |
| Check normal game UI and interactions | [Current integrated runtime](integrated-runtime-20261031.md): 9,000 CPU-profiled frames over a synthetic 240-cube scene, five UI modes, selection/use/close/reopen checks and inspected inventory. Earlier receipts add Unity player and reload coverage. |
| Make Chrome comparisons meaningful | [Matched current-core comparison](optimized-render-comparison-20261031.md) uses identical content, mutations and font/image bytes and separates CPU stages. Inventory style/layout remains 0.336 versus 0.242 ms. No unmatched end-to-end ratio is claimed. |
| Address opening cost | [Unity preparation](unity-renderer-api-20261031.md) and [Agentic preparation](script-preparation-20261031.md) move work into loading. Godot's manual hidden preparation is verified. GPU first-use and cold outliers remain explicit. |
| Reject unsupported optimizations | Slower or inconclusive candidates were removed, including the latest [bounds experiment](backdrop-bounds-20261031.md). Logs, failed evidence and paired outcomes are retained. |

## Final evidence audit

On final review, the shared core, Godot host and Unity package have **no diff
from `a0943b78`**, the latest full-gate implementation revision. The subsequent
core-stack profiler fix changes diagnostic output only. Current Agentic is
`6ab7386e36d2f18fdf8c83150b2e9ea1f6e130de`, with a clean worktree; rejected
experiments are absent. Main was `2727764f` before this report.

Thirteen source/evidence SHA-256 values from the flex and Agentic scissor
receipts were recomputed and all match. The original full-gate log ends in
`all gates pass`; original Unity XML confirms 255 passes, zero failures and
two existing inconclusive tests, plus 7/7 Windows Mono player tests. The
original Agentic log confirms 84 passes, zero failures and two existing
ignored tests. This review reuses applicable completed tests; it does not
claim a new execution of those suites.

The full core/host gate covers 15 GCC and 17 sanitizer suites, 341 Chrome
layout captures at the 1.5 px ceiling, 66 browser behavior passes with the
existing IME exception, 19,833 Godot font checks, rendering, Unicode, exports
and live scenes. The Unity player captures and the 40 Godot paired captures
match their baselines. The Agentic renderer suite covers inherited scissor,
clipping, blending, backdrop filters, viewport changes and document ownership.

Current Unity plugin SHA-256:
`895186f9a41378e1aed48c9e2b51c4e7a53fc0e4c352b5ca931b78982b603913`.
Current Godot addon SHA-256:
`51b29309d0478ada193629fdb823e2d61c27b39bb900476be0a62604a471ea19`.
Both match their paired candidate receipts. The immutable local Agentic SDK
and combined runtime check are identified in the integrated-runtime receipt.

## Remaining product work, in priority order

1. Inventory style/layout and paint remain the principal CPU costs. The clean
   rerun measures 0.280/0.255 ms against Chrome's 0.142/0.201 ms. A structural
   improvement is still needed; recent small renderer-loop experiments do not
   justify further speed claims.
2. Qualify a GPU-busy production scene on target hardware, including weaker
   devices. Current RTX 5080 synthetic scenes and native-resolution tests do
   not establish that result or high-DPI readability.
3. Qualify cold player/shader first use and macOS. Windows IL2CPP development
   and high-stripping release test players now pass 7/7; production consumer
   stripping graphs still need their own checks. The 30-minute reload soak
   passes interactions, but its 13.7 MiB private-memory growth needs attribution.
4. Local Agentic integration is complete at `c2288c71c`, with an explicitly
   sourced immutable SDK and runtime check. No push or global retarget occurred.
   The default published native pin still needs a cross-platform release update;
   it does not automatically include the current local shared core.

These remaining items prevent a universal performance or production-release
claim. They do not erase the measured and verified improvements delivered by
this audit. Keep using the individual receipts for exact workloads and timing
boundaries rather than one combined speedup number.
