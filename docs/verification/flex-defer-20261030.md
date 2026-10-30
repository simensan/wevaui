# Deferring unnecessary flex measurements — October 30, 2026

The shared core now defers the natural-width child layout of eligible row
flex items until their allocated width is known. Their explicit basis and
minimum width already determine allocation without intrinsic content sizes.
This removes the survival backpack's 1022px provisional layout before its
720px final layout, so the unchanged inventory grid can remain retained.

Eligibility is conservative: definite row main size and flex basis, explicit
length minimum width, auto width, no maximum width or aspect ratio, ordinary
horizontal block flow, and no replaced/form sizing path. Other items use the
existing measurement. Box-model and font resolution still happen before
allocation. Final child layout is forced even if allocated and provisional
widths happen to match. Unmeasured natural exports stay marked unknown;
incremental layout must not treat fabricated measurements as proof.

This is shared C++ behavior, with no ABI or host API change. Agentic, Godot,
and Unity were rebuilt against it; the Unity plugin was redeployed locally.

## Performance

Eight alternating process pairs per viewport, 20 warmup and 200 measured
mutations each. Same authored survival fixtures, real font/icons and complete
selection updates as the [GPU audit](agentic-gpu-20261030.md). Tests/builds
finished before timing. Milliseconds below are medians of process means,
not per-frame p95. Complete CPU includes controller setters, update, paint
snapshot, upload preparation, encoding and queue submission. It excludes
GPU completion, presentation and the surrounding game world.

| Viewport | Inventory core before → after | Inventory complete CPU before → after | Complete reduction |
|---|---:|---:|---:|
| 1280x720 | 1.034 → 0.622 | 1.3545 → 0.9185 | 32.2% |
| 1920x1080 | 1.039 → 0.621 | 1.3515 → 0.9300 | 31.2% |

Every selection pair improved: 28.0–38.4% at 720p and 27.3–33.3% at 1080p.
HUD complete CPU was 0.1780→0.1675ms and 0.1795→0.1600ms, respectively;
those smaller differences are not attributed to this optimization because
the HUD's retained-layout pattern did not change.

Stage traces show one retained grid in all 220 survival inventory updates,
versus none previously. The pre-change executable links commit 2c7da541.
Hashes, fixture hashes and all raw run means are in the companion JSON;
logs are retained under `.utmp/render-audit-20261030/flex-defer-*`.

## Correctness and host verification

- Windows 16/16 CTest suites; GCC 15/15; Clang ASan/UBSan 17/17.
  Main checks: 492,686 Windows/GCC, 492,680 sanitized. Captured final builds
  contain no compiler warnings.
- Five additional incremental variants cover wrapping text, definite and
  percentage bases, baseline/wrap modes and maximum-width fallback. Existing
  mutations exercise padding, fonts, constraints and fresh-build equivalence.
- New tracked Chrome 152 reference `73-flex-definite-basis` covers allocated
  text wrapping, nested grids, explicit minimums, em units, wrapping rows,
  equal provisional/final width and the max-width fallback. All 339 tracked
  captures pass the 1.5px gate (47 samples, 67 hand, 225 harvest).
- Unity Native EditMode: 243 passed, 2 environment-gated inconclusive,
  zero failures, after rebuilding/deploying the final plugin.
- Godot rebuilt: timing/form/validity/IME/binding/dialog suites pass
  31/522/104/28/186/1199 checks, respectively.
- Agentic final build: 12 focused renderer tests pass. Before/after inventory
  renders with identical `Depth=6` binding have no RGB pixel differences;
  the final render was inspected.

These are affected-suite checks, not a new full local gate. No cold-start,
busy-world or Chrome GPU-parity improvement is claimed. Agentic's accepted
renderer branch still needs integration into the configured runtime.

## Separate unresolved finding: nested baselines

Follow-up: the captured discrepancy is resolved by the
[nested baseline pass](nested-baseline-20261030.md). The findings below record
the state at this receipt's commit.

An initial version of the new fixture used `align-items:baseline`. Both
unmodified 2c7da541 and the optimized core disagree with pinned Chrome 152
by up to 47px when a flex item contains nested grid/block content. Flex's
baseline helper currently looks only for direct line children, then falls
back to the item's bottom edge. This is a pre-existing rendering defect,
not evidence that baseline alignment is fully supported by this change.

The failing reproduction and reference are preserved in
[flex-baseline-20261030.json](flex-baseline-20261030.json), and raw before/after
oracle failures remain locally. The tracked optimization case uses start
alignment and passes without exemptions. Product readiness names the
unresolved baseline limitation; it remains work for the broader rendering
audit, alongside runtime integration, matched Chrome rendering, lifecycle,
cold-opening and busy-world validation.
