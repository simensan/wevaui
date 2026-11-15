# Unchanged attributes after sibling pruning — October 31, 2026

The C ABI now avoids scheduling a restyle when AttributeMap rejected an equal
write or a removal of an absent attribute. It reads the existing DOM mutation
version before/after the write, adding no attribute lookup or retained cache.
Explicit IME commits and dialog synchronization still execute before return.
Observers continue to own invalidation for those side effects.

This repeats the previously rejected implementation on a materially different
pipeline: sibling-subject pruning in `4c8bb0af` no longer pulls every inventory
slot into the cascade when only two class attributes changed. With that change
in place, skipping the other ten unchanged class writes now avoids actual work.
The earlier rejected measurements remain valid for their earlier core builds.

## Paired performance

Four alternating before/after process pairs use the prepared survival fixtures
at 1280x720, 20 warmups, 200 mutations and 200 retained frames, targeting 60 Hz.
No builds or tests overlapped measurement. Medians of per-run CPU means, ms:

| Work | HUD before → after | Inventory before → after |
| --- | ---: | ---: |
| Cascade | 0.0235 → 0.0245 | 0.2360 → 0.1025 |
| Core update | 0.1380 → 0.1395 | 0.7695 → 0.6665 |
| Complete controller/update/prepare/submit | 0.3800 → 0.3805 | 1.2155 → 1.1265 |

Inventory complete CPU falls 7.3%, core update 13.4%, and cascade 56.6%.
HUD is essentially unchanged. Inventory restyles 10–13 elements on 219
warmup/measured updates, versus 41–43 with sibling pruning alone. The first
update restyles one element; HUD remains at two on every update.
Initial/final mutation-target geometry is identical between binaries.

These are warmed CPU measurements on one machine, not GPU execution,
presentation latency, per-frame p95, cold opening or busy-world FPS. Do not
compound this percentage with older receipts measured under different timing
conditions. The rejected bounded-texture cache is absent from both binaries.

## Verification

New regressions cover 80 repeated writes/removals, uppercase attribute names,
empty versus missing attributes, a queued real mutation followed by no-ops,
unchanged draw serial/data, forced full-cascade equivalence, and the explicit
composition commits for equal `type` and absent `readonly`/`disabled` writes.

Windows passes 16 CTest suites; GCC passes 15. Main checks are 493,402 with
zero failures. All 340 tracked Chrome captures pass the 1.5px ceiling without
known-gap exemptions. The host engine's 13 focused renderer tests pass. Godot's rebuilt
extension passes timing/form-state/validity-selector/IME/binding/dialog scenes
with 39/522/658/28/186/1199 checks.

Clang ASan/UBSan passes all 17 suites, including positive controls, with
493,396 main checks and zero failures. The rebuilt/deployed Unity plugin passes
Native EditMode: 243 passed, two existing environment-gated inconclusive,
zero failures. Captured core/host builds contain no compiler warnings.

The core change reaches host-engine, Godot and Unity without C ABI or host API
changes. Published older host-engine SDKs remain older builds; no global engine
selection or managed upstream checkout was changed. This is affected-suite
verification, not a new complete local gate or a Chrome GPU-parity claim.

[Every paired result, binary hash and CPU phase](scoped-noop-20261031.json)
is retained. Local raw evidence is `.utmp/render-audit-20261030/scoped-noop-*`.
