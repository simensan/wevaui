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
