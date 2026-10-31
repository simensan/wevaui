# Inventory invalidation attribution — 2026-10-31

Follow-up: [stretched-item probe tests](stretched-probe-20261031.md) were added,
but the speculative-probe shortcut was [reverted after real-font Unity timing](stretched-probe-host-20261031.md).
Narrowing the final replacement boundary remains unimplemented; neither the
shortcut nor its synthetic-font timing established that optimization.

Title-only text changes rebuild 57 draw commands even though the inventory
container, details panel and title bounds remain unchanged across all twelve
selections. This is a concrete target for a smaller incremental layout/paint
boundary. No production change or speedup is claimed by this audit.

## Isolation and measurements

The existing fixed-font C ABI selection diagnostic was extended to isolate
slot classes, six text fields, the detail icon, or disabled state. Every process
first applies the entire canonical initial selection, then changes only its
chosen group. This avoids measuring uninitialized binding placeholders.
Every setter status and all twenty final values/presence states are checked,
including unchanged targets; missing assets must be zero. All runs passed.

Three rounds of five fresh processes, with the second round in reverse order,
performed 120 warmups and 5,000 measured updates per process: **75,000 updates**.
Sampling, diagnostic logging and trace collection were disabled for timing.
The table reports median of three run means; setters are outside the timed
core update. It is not real-font host timing, GPU time or a Chrome comparison.
Groups interact, so their times must not be summed into an estimated full frame.

| Mutation group | Core update mean, aggregated (ms) | Draw reuse from separate traces |
|---|---:|---|
| Complete selection | 0.394123 | 236 of 293–301 |
| Slot highlights | 0.080322 | 292 of 299 |
| Six text fields | 0.281990 | 242 of 293–301 |
| Detail icon | 0.039702 | 295 of 299 |
| Disabled state | 0.006303 | 294 when toggled, otherwise 299 of 299 |

The disabled sequence contains only two actual flips in twelve states, so its
mean is not the cost of changing disabled state every frame. Slot mutations
identify only the previously/currently selected slots as paint changes. Icon
changes identify only the detail image as a layout change. Each icon change
publishes one new texture; all groups hold 25 textures after settling. Text
changes publish no new textures. The previous bounded inactive-texture cache
experiment remains rejected; this trace is not grounds to revive it.

## Title-only finding

A follow-up changes only `#item-name`, leaving all other selection targets at
their canonical values. Each of twelve transitions publishes 299 draws, reuses
242, and creates zero textures. The changed-element list contains the title.
The layout rejection trace reports `intrinsic contribution` at the title,
then either `natural intrinsic contribution` or `outer geometry` at the details
panel; the accepted replacement is the `.inventory-view` flex container.
Its unrelated inventory grid is retained, so this is not a full-document rebuild.

The visible border-box rectangles are identical in all twelve title states:

| Element | x | y | width | height |
|---|---:|---:|---:|---:|
| `.inventory-view` | 101 | 210 | 1078 | 401 |
| `.item-details` | 877 | 234 | 274 | 361 |
| `#item-name` | 904 | 374.6 | 247 | 24 |

The title's intrinsic contributions change while its visible geometry does not.
The details panel participates in a fixed-height row flex layout and receives a
stretched cross size. Current incremental proofs conservatively compare natural
exports before accepting the child; the row parent is consequently rebuilt.
Stable rectangles in this fixture do **not** prove that intrinsic changes can
always be ignored. Correct reuse must account for flex shrinking, automatic
minimum sizes, wrapping, baselines, imposed cross sizes, min/max constraints,
overflow and sibling allocation. It must remain a shared-engine fix, not a
special case for the inventory or authored dimensions.

## Evidence and next step

Evidence is in `.utmp/render-audit-20261030/core-selection-profile/`:
`group-0.log` through `group-4.log`, `group-timings/`,
`text-layout-trace.log`, `title-layout-trace.log`, `title-bounds.log`,
the generated diagnostic sources/executables, generator and timing driver.
The adjacent JSON records raw hashes and parsed counters. Trace data is separate
from timings. Draw serials distinguish idle publication from stale changed-element
and cache statistics. Earlier broad stage logs include synchronous diagnostic
output and are not used as latency data.

No production source, host binary or API changes were made. Next, build a minimal
generic flex-row regression and prove which parent allocation inputs actually
depend on changed text before narrowing the replacement boundary. Verify fresh
layout/render equivalence and Chrome behavior before timing both real-font hosts.
