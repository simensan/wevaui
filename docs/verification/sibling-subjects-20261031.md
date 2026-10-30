# Sibling subject invalidation — October 31, 2026

The shared core now excludes sibling subtrees that cannot contain a subject
of any compiled sibling-dependent selector. It reuses conservative selector
keys already compiled for match-cache safety. Relevant subjects retain their
whole subtree walk for inheritance; unkeyed selectors and sibling-dependent
scope boundaries remain conservative. Overlap folding uses the same proof so
it cannot discard a separate mutation inside a skipped sibling.

Text and image writes, including binding mutations, avoid promoting their
scope to the parent when that family has no possible sibling-rule subjects.
Authored CSS stays intact. No C ABI or host API change is required.

## Measured effect

The prepared survival fixtures retain their footer `kbd + kbd` rule. A stage
probe reduces HUD restyles from 7 to 2 elements on all 220 updates. Inventory
falls from 62 to 41–43 on 219 updates (the first update is 54 to 39).

Four alternating before/after process pairs use the corrected Agentic
diagnostic, 1280x720, 20 warmups, 200 mutations and 200 retained frames, paced
at 60 Hz. Builds and tests did not overlap these timings. Medians of run means:

| CPU work, ms | HUD before → after | Inventory before → after |
| --- | ---: | ---: |
| Cascade | 0.0430 → 0.0255 | 0.4035 → 0.2990 |
| Core update | 0.1620 → 0.1435 | 0.9470 → 0.8820 |
| Complete controller/update/prepare/submit | 0.4055 → 0.3900 | 1.5070 → 1.5375 |

Inventory cascade drops 25.9% and core update 6.9%. Complete HUD CPU drops
3.8%. Inventory complete CPU is 2.0% slower in this batch: median host draw
submission rises from 0.366 to 0.457ms, and box/paint intervals also vary.
Do not claim an overall inventory speedup or attribute that submission increase
to a particular cause. GPU execution and presentation are outside this timing.
The change is retained for the demonstrated core-work reduction and HUD result.
The larger inventory submission and layout costs remain audit targets.

All measured initial/final mutation-target bounds are exactly equal between
versions. Element/box/draw counts and texture dimensions match. The final
binding-path gate was added after this direct-setter batch; final-source core
and host tests cover it. This is not a new busy-world or cold-opening result.

## Verification and host synchronization

- Windows: all 15 core CTest suites plus the native SDK smoke check pass.
- GCC: 15/15 suites; Clang ASan/UBSan: 17/17, including positive controls.
  Main suite: 493,041 checks on Windows/GCC; 493,035 sanitized, zero failures.
- New incremental cases cover unrelated footer rules, relevant adjacent and
  general siblings, inherited descendants, nested `:is`, filtered reverse
  ranks, `@scope` boundaries and separate overlapping mutations. Every state
  matches a forced full cascade's draw data.
- All 340 tracked Chrome layout captures pass the 1.5px ceiling, no exemptions.
- Unity plugin rebuilt and deployed. Native EditMode: 243 pass, two existing
  environment-gated inconclusive, zero failures.
- Godot extension rebuilt/deployed. Timing/form-state/validity-selector/IME/
  binding/dialog scenes pass 39/522/658/28/186/1199 checks. An initial invocation
  used a nonexistent `form_tests.tscn`; its failure log is retained, and the
  correctly named `form_state_tests.tscn` passes.
- Agentic's optimized diagnostic rebuilt against the final core; all 13 focused
  renderer tests pass. Its audit
  branch remains separate from the managed upstream checkout; published older
  SDKs do not silently acquire this source change.

This is affected-suite verification, not a fresh full local-gate result. No
compiler warnings were found in captured core or Unity/Godot build logs.
Raw evidence is under `.utmp/render-audit-20261030/sibling-*`.
[Paired results, hashes and stage intervals](sibling-subjects-20261031.json)
retain every run; instrumented stage timings are excluded from the table.

The [host preparation follow-up](host-preparation-20261031.md) splits the
remaining host CPU cost and identifies detail-image texture churn. Its temporary
retention probe is removed; it is not another production speedup.
