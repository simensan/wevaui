# Relational selector invalidation and inert templates — 2026-10-25

Ordinary sibling and `:has()` selectors previously disabled caching or widened
style work for unrelated controls. The core now records hover/active dependencies
and possible sibling-rule subjects. Local inline-style writes stay local unless
the stylesheet actually reads `[style]`. Focus, other attributes, structural
mutations and unkeyed selector subjects retain conservative behavior.

Template source bodies no longer enter the cascade or start animations.
Unchanged empty repeats skip their sibling search. These changes support sparse
control templates without requiring new markup syntax or host APIs.

Validation:

- GCC and Clang: 14/14 CTest suites pass on each; final build logs have no
  warnings. The earlier Clang build's subsecond WSL clock-skew warnings are
  preserved in `deep-selector-build-clang.log`; its follow-up build is clean.
- A 610-element relational stylesheet restyles one element on unrelated hover.
  Incremental output matches a full 610-element cascade. Eight scenarios cover
  direct and nested `:has()`, sibling hover/active dependencies, `[style]`,
  inline-style edits, pointer press/leave and checkbox/launcher state changes.
- Match-cache coverage checks that sibling-rule subjects remain uncached while
  an unrelated element retains its valid cached result.
- Template checks cover inert animations, mounting/unmounting, unchanged empty
  refreshes, retained focused controls and nested zero/one repeats.
- Chrome hand layout oracle: 64/64 agree at 800x600, 1.5px ceiling, no gaps.
  The headless Chrome template check confirms zero inert animations, one after
  mounting and zero after removal.

Evidence: `deep-selector-build-gcc.log`, `deep-selector-build-clang-final.log`,
`deep-selector-test-{gcc,clang}.log`, `deep-selector-oracle.log`, and the detailed
CTest logs in each existing build directory. Chrome receipts and oracle outputs
are retained in Convoy's `builds/test-evidence/weva-developer-ui/deep-perf/`.

This establishes bounded style work and rendering correctness for the selected
cases. It does not establish game FPS. Live performance qualification is pending
an uncontended run; the user was playing Dota during the investigation. No
Unity/Godot host or macOS qualification is claimed by this scoped pass.

## Stationary tooltip follow-up

The pointer feed previously forced layout on every sample while a tooltip was
visible, even with identical coordinates. Tooltip placement now updates only
when the pointer moves. Host changes, dwell, button dismissal and hit testing
still run on every sample. The interaction regression checks 120 stationary
samples for unchanged cascade counters and draw serial, then verifies movement,
the retained tooltip handle, dismissal and pass-through hit testing.

Both GCC and Clang pass all 14 CTest suites; the final builds have no warnings.
At 1920x1080, the release Agentic document profile with a stationary F1 tooltip
falls from 2.516 ms to 0.0675 ms, with zero restyled elements after settling.
The live Resources panel's two hovered runs measure 364.7 and 381.7 FPS,
against 377.5 and 379.5 FPS with the pointer away. Hovered UI CPU averages
0.4809 and 0.4681 ms; all four 2,400-frame runs remain below 16.7 ms.
The earlier hovered baseline was 103–115 FPS and 7.2–7.9 ms UI CPU.

Raw profiles and the inspected tooltip capture are retained under
`E:/Convoy_Agentic/builds/test-evidence/weva-developer-ui/interaction-fixes-20261025/`.
These results qualify this tooltip fix; graph refresh and panel mounting are
separate remaining workloads.

## Handle cleanup follow-up

Removing a node previously scanned the full lifetime handle table to tombstone
its slot. A 100-row conditional control fixture took 14 ms to close initially,
and 118 ms at its tenth close as stale slots accumulated. Removal now uses the
existing reverse index, retaining stale-handle rejection and survivor identity.
The same fixture closes in 2.3–3.7 ms across ten cycles. Pointer hit results,
queued event targets and element statistics also use that live index.

Both GCC and Clang pass all 14 suites, including repeated removal/replacement,
stale handles, surviving handles, hit testing and live element counts. Fixture
and raw before/after CSVs are retained as `mount-*` in the evidence directory
above. This does not remove first-use image decode costs.
