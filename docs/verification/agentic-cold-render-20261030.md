# Agentic cold renderer audit — October 30, 2026

Agentic `f90b04b9b` removes unused GPU setup from ordinary UI opening:

- Backdrop-filter pipelines and uniforms are created on the first applicable
  draw, then reused. A page with no backdrop filters never allocates them.
- An Overlay used only to publish paint snapshots no longer constructs an
  unused renderer on the document thread. The render thread owns the renderer
  that actually draws. Direct updates still construct and use a local renderer.

These are Agentic-owned changes. Unity `NativeDocumentRenderer.DrawBackdrop`
and Godot `WevaDocument::backdrop_material` already allocate their backdrop
material/shader on demand. No shared core, ABI, Unity or Godot code changed;
the [latest shared-host gates](nested-baseline-20261030.md) remain applicable.

## Measurement method

The before SDK is Agentic `be88b3cb2`, after SDK `f90b04b9b`; both link the same
Weva core `faf04f31`. Both are official immutable published SDK bundles built
with the optimized fast profile. The configured `engine-main` was not changed.

The new `--defer-ui` fixture option starts the synthetic 240-cube scene with no
UI. It avoids both script init loading and the host's conventional `main.html`
autoload. An injected key opens the survival inventory while a 6,000-frame,
zero-warmup CPU-only profile is active. The runner verifies that the exact input
tick and following frame are inside the retained profile, checks artifact hashes,
and captures the world before and the UI afterward. Every run uses a fresh
process. Before/after order alternates between rounds.

Hardware/settings match the [runtime audit](agentic-runtime-20261030.md):
Windows, RTX 5080, Vulkan 617.42, 1280x720, no VSync, off-desktop unfocused
window. This is first UI use within a running scene, not process startup,
disk-cache-cold launch, low-end hardware, or input-to-photon latency.

## Results

Five threaded pairs: median opening-frame main CPU **21.979 → 20.525 ms**,
a **6.6% reduction**. The following frame interval, which reflects the opening
stall, is **22.034 → 20.588 ms**. Median render-worker UI CPU is
**1.557 → 1.416 ms**; spans overlap and must not be summed.

One after run takes **69.318 ms**, dominated by **66.712 ms** in the main UI
section. It is retained in the distribution. An earlier exploratory before
run also stalled at **66.929 ms**, but the five paired before runs do not
reproduce that tail. These samples establish neither a p95 improvement nor
a bounded opening time. Cold opening still exceeds a 60 Hz frame budget.

Three direct-rendering pairs have median opening main CPU **28.307 → 27.472 ms**
and following interval **28.422 → 27.599 ms**. The last pair regresses from
29.126 to 33.839 ms, so this small sample is not evidence of consistent direct
latency improvement. Both routes remain functional with identical before/after
pixels in their first pair. Direct profiles have no worker UI span; their main
`ui` section does not encompass this opening path, so use main CPU and interval
rather than treating that section's near-zero value as the UI's cost.

Seven standalone diagnostic pairs isolate the backdrop change. Median CPU time
through first queue submission improves **24.309 → 22.440 ms** for the HUD and
**20.461 → 18.765 ms** for inventory. Renderer creation changes
**9.140 → 5.974 ms** and **1.432 → 0.468 ms**, respectively. The inventory is
the second document in that diagnostic process, so it can reuse process-wide
raster entries and driver state. Renderer setup timing also includes the small
missing-assets validation immediately before construction. This diagnostic
does not measure the snapshot-only Overlay saving.

Parsing is approximately 1.5–1.7 ms in the exploratory standalone samples;
first paint and layout account for substantially more. Precompiling markup
alone would therefore leave most of the measured opening cost intact.

## Validation and remaining work

- Agentic renderer suite: 13 pass, including first usable backdrop allocation,
  reuse after page changes and unsupported target behavior. Existing GPU pixel
  checks cover blur, recoloring, sRGB, text, texture order and worker ownership.
- Overlay suite: 42 pass, including snapshot-only operation without a local
  renderer and switching between snapshots and direct updates after a reload.
- Optimized runtime SDK built and published coherently. The paired threaded
  before/after captures have identical RGB pixels; the after capture and a
  first-use blur render were inspected. Direct runtime before/after captures
  also have identical RGB pixels and the after capture was inspected. The 16
  paired runtime runs retain 96,000 frames in total; every opening tick is
  verified inside its profile and all 32 report/frame artifact hashes match.
- Core and other hosts were unchanged; this is not a rerun of the full gate.

`Tools/performance/run_agentic_cold_audit.py` accepts explicit labeled SDK
bundles, uses its own session registry, preserves raw responses and artifacts,
and stops only its owned sessions. Generate a project first with
`create_agentic_audit_project.py --defer-ui`; add `--direct` for the direct path.

The [JSON receipt](agentic-cold-render-20261030.json) records per-run observations,
binary identities and artifact paths. Raw evidence remains under
`.utmp/render-audit-20261030/`, including exploratory `cold-*`, paired
`cold-paired/`, standalone `lazy-backdrop-*`, build/test logs and captures.
The first direct-run analysis failed because direct profiles omit worker spans;
the runner now preserves that absence as null rather than inventing a zero.
That failed analysis and its runtime evidence are retained separately.

Next: attribute the remaining first-paint/layout cost and the cold tail,
verify preload/reuse as an actual runtime workflow, and complete matched Chrome
rendering comparisons and longer lifecycle testing. This is a scoped speedup,
not a release-readiness or Chrome-parity claim.
