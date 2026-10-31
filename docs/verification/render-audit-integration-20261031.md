# Rendering audit integration — 2026-10-31

This receipt integrates the accepted rendering work through `a1352706` and
distinguishes measured improvements from experiments that were removed.
It does not claim universal Chrome parity, hitch-free first use or performance
on untested hardware. Earlier timing results belong to the exact builds and
workloads in their receipts; their percentages must not be compounded.

## Accepted changes and ownership

| Change | Owner and evidence | Practical result and limit |
| --- | --- | --- |
| Batch attribute writes before an update | [Godot adapter](render-batching-20261030.md) | Stops painting intermediate selection states. Initial diagnostic improved inventory update median by 35.8%; it was one sequential before/after pair. |
| Exclude unrelated sibling-selector subjects | [Shared core](sibling-subjects-20261031.md) | Inventory cascade decreased 25.9% and core update 6.9% in that Agentic diagnostic; total inventory CPU did not improve. Both supported hosts receive the same core. |
| Retain hidden documents and prepare during loading | [Unity API and Godot workflow](unity-hidden-preparation-20261031.md) | Hidden automatic work is deferred while explicit preparation applies current data. Showing reuses the document. Component disable still releases it. |
| Prepare renderer materials during loading | [Unity adapter](unity-material-preparation-20261031.md) | First command recording decreased from 3.092 to 1.310 ms in paired editor runs. Work moves into loading; GPU shader execution is not warmed. |
| Upload native vertex layout directly | [Unity adapter](unity-direct-vertices-20261031.md) | 32-byte vertices and bulk copy replace managed per-vertex construction. Full inventory mesh-sync median decreased 11.9%; total-frame tails were mixed. Godot uses a different packed-array API. |
| Defer invisible direct rendering and unused cold resources | [Agentic hidden work](agentic-hidden-ui-20261030.md), [cold setup](agentic-cold-render-20261030.md) | Measured gains on the leased Agentic audit branch, not an upstream integration or a supported-host release claim. |

## Audit coverage and open evidence gaps

The audit traced controller updates, cascade, incremental layout/box building,
paint/glyph work, snapshot capture, texture/mesh preparation, command recording
and submission. [Native sampling](core-selection-profile-20261031.md) uses a
synthetic font; it does not predict host text cost. [Mutation isolation](inventory-invalidation-20261031.md)
shows that title changes can rebuild 57 draws despite unchanged visible bounds.
The subsequent [flex-row allocation proof](flex-row-allocation-20261031.md)
now accepts the smaller details subtree for this case, rebuilding 44 draws
instead of 57, with paired Unity and Agentic CPU improvements. This integration
receipt predates that change; its newer gate evidence is in the follow-up.

[Texture replacement tests](texture-lifetime-20261031.md) cover resize/reload,
normal/multiply blending, replacement pixels and bounded exposed resource counts.
They do not establish long-duration GPU memory stability. The Unity mesh tests
cover pooled meshes, changing size, 16/32-bit indices and backdrop transitions.

The [prepared Chrome comparison](prepared-render-comparison-20261031.md) uses
matched content and mutations with clearly separated timing boundaries. It
predates later shared-core changes and needs remeasurement before describing
current relative speed. That [current-core rerun](current-render-comparison-20261031.md)
is now recorded separately and still identifies an inventory style/layout gap.
Chrome paint/raster CPU, host update elapsed time and
whole-player GPU timings are not interchangeable.

The [Unity 3D scene diagnostic](unity-player-world-20261031.md) includes a modest
synthetic world; it is not GPU-bound, and its inventory updates only quantity
and highlights. Full selection is covered by editor CPU benchmarks, not that
player timing run. Lower-end hardware, higher-resolution scaling, prolonged
lifecycle load, player cold shader work and matched Chrome GPU timing remain
unqualified. These are not hidden inside the integration pass result.

## Rejected candidates remain rejected

The [bounded texture cache](bounded-textures-20261031.md),
[glyph hash map](glyph-map-20261031.md),
[lazy transform-font calculation](transform-font-20261031.md), and
[stretched-item probe shortcut](stretched-probe-host-20261031.md) did not
establish the required improvement in their paired workloads and are absent
from production. The stretched-item case specifically demonstrated why a
synthetic-font gain needs host verification. Its generic regression tests remain.
Stale next-step pointers in earlier receipts now link to those dispositions.

## Integrated verification

The qualified `check.sh --release` completed with **all gates passing and no
skipped stages**. It includes 15 GCC and 17 Clang ASan/UBSan CTest suites,
340 Chrome layout captures within the 1.5 px ceiling without exemptions,
cached-property and asset checks, native plugin builds/load checks, isolated
and exported Unicode safety, 19,833 Godot font-adapter checks, addon installation,
debug/release/embedded export startup and pixel checks, backend/interactive
render comparisons and the live host scenes. The resulting UI render was
inspected. Tooling suites pass 70 release/oracle, nine text-safety and 32 Frontier
performance-runner tests.

Live Chrome behavior checks pass 66 scripts with one existing documented IME
exception and no unexpected failures; the full-gate pass does not erase that
exception. With the full gate's incremental corpus enabled, the main core suites
report 705,835 GCC and 705,829 sanitizer checks (the small-stack test skips under
sanitizers).

Full Unity EditMode passes **255 tests with two existing environment-gated
inconclusive results**. Windows x64 Mono player rendering passes **7/7**, with
all sixteen PNGs byte-identical to the accepted direct-vertex renderer run.
The player runner's two whitespace-only ProjectSettings changes were removed
after it exited; no user scene was edited. IL2CPP and release stripping remain
outside these results.

Evidence root: `.utmp/render-audit-20261030/integration-20261031/`. The checked
runner uses the qualified patched Linux Godot editor/templates, pinned Windows
Chrome 152 through the WSL bridge, and a private Xvfb display. This is distinct
from stock-Godot Unicode qualification and other platforms or graphics APIs.
The [machine-readable receipt](render-audit-integration-20261031.json) records
the checked source revision, results and evidence hashes. This integration
pass makes no new before/after timing claim.
